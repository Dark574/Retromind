using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class EditMediaViewModelAssetTests
{
    [Fact]
    public async Task DeleteThenCancel_LeavesOriginalAssetAndFileUntouched()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var assetPath = temp.CreateFile(
            Path.Combine("Library", "Games", "Cover", "Game__12345678_Cover_01.jpg"),
            "image");
        var item = CreateItem(assetPath);
        using var viewModel = CreateViewModel(item);
        viewModel.SelectedAsset = Assert.Single(viewModel.Assets);

        await viewModel.DeleteAssetCommand.ExecuteAsync(null);
        viewModel.CancelAndCloseCommand.Execute(null);

        Assert.Empty(viewModel.Assets);
        Assert.Single(item.Assets);
        Assert.True(File.Exists(assetPath));
    }

    [Fact]
    public async Task DeleteSaveFailure_RestoresVisibleAndOriginalAssignment()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = Path.Combine("..", "unsafe-cover.jpg")
        });
        using var viewModel = CreateViewModel(item);
        viewModel.SelectedAsset = Assert.Single(viewModel.Assets);

        await viewModel.DeleteAssetCommand.ExecuteAsync(null);
        await viewModel.SaveAndCloseCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSaveError);
        Assert.Single(viewModel.Assets);
        Assert.Single(item.Assets);
        Assert.Equal(Path.Combine("..", "unsafe-cover.jpg"), item.Assets[0].RelativePath);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("other-item")]
    [InlineData("category")]
    [InlineData("same-item")]
    public async Task Delete_CleansUpOnlyAfterSaveAndChecksRemainingReferences(string reference)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var path = temp.CreateFile("Library/Games/Cover/Original.png", "artwork");
        var item = CreateItem(path);
        var parent = new MediaNode("Games");
        parent.Items.Add(item);
        var another = CreateItem(path);
        another.Id = Guid.NewGuid().ToString();
        switch (reference)
        {
            case "other-item": parent.Items.Add(another); break;
            case "category": parent.Assets.Add(another.Assets[0]); break;
            case "same-item": item.Assets.Add(another.Assets[0]); break;
        }
        var roots = new ObservableCollection<MediaNode> { parent };
        var data = new MediaDataService();
        var files = new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService());
        var tracker = new LibraryChangeTracker(data, (_, _) => { });
        try
        {
            IReadOnlyList<AssetRemoval> removals = [];
            await UiThreadHelper.InvokeAsync(async () =>
            {
                tracker.Initialize(roots);
                using var editor = CreateViewModel(item, parent: parent, roots: roots);
                editor.SelectedAsset = editor.Assets[0];
                await editor.DeleteAssetCommand.ExecuteAsync(null);
                await editor.SaveAndCloseCommand.ExecuteAsync(null);
                tracker.MarkDirty();
                removals = editor.SavedAssetRemovals;
            });

            Assert.Single(removals);
            Assert.True(File.Exists(path));
            Assert.True(await tracker.SaveAndConfirmAsync(() => files.RemoveUnreferencedAssets(removals, roots)));
            Assert.Equal(reference != "none", File.Exists(path));
            Assert.Equal(reference == "same-item" ? 1 : 0,
                (await data.LoadAsync()).Single().Items[0].Assets.Count);
        }
        finally { await UiThreadHelper.InvokeAsync(tracker.StopTracking); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_WhenLibraryWriteFailsKeepsOriginalArtworkAtItsSavedPath(bool rename)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var path = temp.CreateFile("Library/Games/Cover/Game__12345678_Cover_01.jpg", "original");
        var item = CreateItem(path);
        var parent = new MediaNode("Games");
        parent.Items.Add(item);
        var roots = new ObservableCollection<MediaNode> { parent };
        var data = new MediaDataService();
        await data.SaveJsonAsync(data.Serialize(roots));
        temp.CreateDirectory("retromind_tree.tmp");
        var tracker = new LibraryChangeTracker(data, (_, _) => { });
        try
        {
            await UiThreadHelper.InvokeAsync(async () =>
            {
                tracker.Initialize(roots);
                using var editor = CreateViewModel(item, parent: parent, roots: roots);
                if (rename)
                    editor.Title = "Renamed";
                else
                {
                    editor.SelectedAsset = Assert.Single(editor.Assets);
                    await editor.DeleteAssetCommand.ExecuteAsync(null);
                }
                await editor.SaveAndCloseCommand.ExecuteAsync(null);
                Assert.False(editor.HasSaveError);
                Assert.Single(editor.SavedAssetRemovals);
                tracker.MarkDirty();
            });
            if (rename)
            {
                var replacement = Assert.Single(item.Assets).AbsolutePath;
                Assert.NotEqual(path, replacement);
                Assert.Equal("original", await File.ReadAllTextAsync(replacement));
            }

            var error = await Record.ExceptionAsync(() => tracker.SaveAndConfirmAsync(
                () => throw new InvalidOperationException("Cleanup must wait for a successful save.")));

            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            var reloaded = Assert.Single((await data.LoadAsync()).Single().Items);
            Assert.Equal(path, Assert.Single(reloaded.Assets).AbsolutePath);
        }
        finally { await UiThreadHelper.InvokeAsync(tracker.StopTracking); }
    }

    [Fact]
    public async Task Cleanup_WaitsForAnUnchangedSnapshotAndChecksReferencesAddedDuringSave()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var path = temp.CreateFile("Library/Games/Cover/Original.png", "artwork");
        var reference = Assert.Single(CreateItem(path).Assets);
        var removal = FileManagementService.CaptureAssetRemoval(reference)!;
        var parent = new MediaNode("Games");
        var roots = new ObservableCollection<MediaNode> { parent };
        var data = new MediaDataService();
        var files = new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService());
        var snapshotTaken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracker = new LibraryChangeTracker(data, (_, _) => { }, null, null, null, async () =>
        {
            var snapshot = await UiThreadHelper.InvokeAsync(() => data.CreateSnapshot(roots));
            snapshotTaken.TrySetResult();
            await writeAllowed.Task;
            await data.SaveJsonAsync(data.Serialize(snapshot));
        });
        try
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                tracker.Initialize(roots);
                tracker.MarkDirty();
            });
            void Cleanup() => files.RemoveUnreferencedAssets([removal], roots);
            var firstSave = tracker.SaveAndConfirmAsync(Cleanup);
            try
            {
                await snapshotTaken.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await UiThreadHelper.InvokeAsync(() =>
                {
                    parent.Assets.Add(reference);
                    tracker.MarkDirty();
                });
                Assert.True(File.Exists(path));
            }
            finally { writeAllowed.TrySetResult(); }

            Assert.False(await firstSave.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await tracker.SaveAndConfirmAsync(Cleanup));
            Assert.True(File.Exists(path));
            Assert.Equal(path, Assert.Single((await data.LoadAsync()).Single().Assets).AbsolutePath);
        }
        finally { await UiThreadHelper.InvokeAsync(tracker.StopTracking); }
    }

    [Fact]
    public async Task Save_PortableModeNormalizesItemPathsAndKnownEnvironmentOnly()
    {
        using var portableRoot = new TemporaryDirectory();
        using var externalRoot = new TemporaryDirectory();
        using var environment = UseDataRoot(portableRoot.RootPath);
        var customValue = portableRoot.GetPath("custom", "unchanged");
        var externalHome = externalRoot.GetPath("home");
        var item = new MediaItem("Portable game")
        {
            MediaType = MediaType.Native,
            WorkingDirectory = portableRoot.GetPath("Library", "Games", "Portable"),
            XdgConfigPath = portableRoot.GetPath("Home", ".config", "portable"),
            XdgDataPath = portableRoot.GetPath("Home", ".local", "share", "portable"),
            XdgCachePath = portableRoot.GetPath("Home", ".cache", "portable"),
            XdgStatePath = portableRoot.GetPath("Home", ".local", "state", "portable"),
            XdgBasePath = portableRoot.GetPath("Home", "portable")
        };
        item.EnvironmentOverrides["PROTONPATH"] =
            portableRoot.GetPath("Emulators", "ProtonVersions", "GE-Proton");
        item.EnvironmentOverrides["HOME"] = externalHome;
        item.EnvironmentOverrides["RETROMIND_CUSTOM"] = customValue;
        var settings = new AppSettings { PreferPortableLaunchPaths = true };
        using var viewModel = CreateViewModel(item, settings);

        await viewModel.SaveAndCloseCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine("Library", "Games", "Portable"), item.WorkingDirectory);
        Assert.Equal(Path.Combine("Home", ".config", "portable"), item.XdgConfigPath);
        Assert.Equal(Path.Combine("Home", ".local", "share", "portable"), item.XdgDataPath);
        Assert.Equal(Path.Combine("Home", ".cache", "portable"), item.XdgCachePath);
        Assert.Equal(Path.Combine("Home", ".local", "state", "portable"), item.XdgStatePath);
        Assert.Equal(Path.Combine("Home", "portable"), item.XdgBasePath);
        Assert.Equal(
            Path.Combine("Emulators", "ProtonVersions", "GE-Proton"),
            item.EnvironmentOverrides["PROTONPATH"]);
        Assert.Equal(externalHome, item.EnvironmentOverrides["HOME"]);
        Assert.Equal(customValue, item.EnvironmentOverrides["RETROMIND_CUSTOM"]);
    }

    private static MediaItem CreateItem(string absolutePath)
    {
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Cover,
            RelativePath = Path.GetRelativePath(AppPaths.DataRoot, absolutePath)
        });
        return item;
    }

    private static EditMediaViewModel CreateViewModel(MediaItem item, AppSettings? settings = null,
        MediaNode? parent = null, ObservableCollection<MediaNode>? roots = null)
    {
        if (parent == null)
        {
            parent = new MediaNode { Name = "Games" };
            parent.Items.Add(item);
        }
        return new EditMediaViewModel(
            item,
            settings ?? new AppSettings(),
            new FileManagementService(AppPaths.LibraryRoot, new RecordingErrorService()),
            new WinetricksService(AppPaths.LibraryRoot, new RecordingErrorService()),
            [parent.Name],
            new UnexpectedIdentificationService(),
            rootNodes: roots ?? new ObservableCollection<MediaNode> { parent },
            parentNode: parent);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));

    private sealed class UnexpectedIdentificationService : IRetroAchievementsGameIdentificationService
    {
        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected identification request.");

        public Task<RetroAchievementsIdentificationResult> IdentifyAsync(
            string gameSystemId,
            string filePath,
            string apiKey,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unexpected identification request.");
    }
}
