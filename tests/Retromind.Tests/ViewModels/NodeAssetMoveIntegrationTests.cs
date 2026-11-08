using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class NodeAssetMoveIntegrationTests
{
    [Fact]
    public async Task Rename_SaveUpdatesNodeAndItemAssetsAndKeepsExistingUiFlow()
    {
        using var state = new State();
        var item = new MediaItem("Game");
        state.Source.Items.Add(item);
        var nodeAsset = state.AddAsset(state.Source, "Logo", "Node_Logo_01.png");
        var itemAsset = state.AddAsset(item, "Cover", "Game_Cover_01.png");
        var vm = state.Editor();
        vm.Name = "Renamed";
        bool? closed = null;
        vm.RequestClose += value => closed = value;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Equal("Renamed", state.Source.Name);
        Assert.Equal("Library/Renamed/Logo/Node_Logo_01.png", nodeAsset.RelativePath);
        Assert.Equal("Library/Renamed/Cover/Game_Cover_01.png", itemAsset.RelativePath);
        Assert.True(File.Exists(nodeAsset.AbsolutePath));
        Assert.True(File.Exists(itemAsset.AbsolutePath));
        Assert.Empty(state.Errors.Reports);
    }

    [Fact]
    public async Task Rename_InvalidTreeReturnsFailureBeforeImportOrFileChanges()
    {
        using var state = new State();
        var asset = state.AddAsset(state.Source, "Cover", "Game_Cover_01.png");
        var vm = state.Editor();
        vm.Name = "Renamed";
        state.Source.Children.Add(state.Source);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Old", state.Source.Name);
        Assert.StartsWith("Library/Old/", asset.RelativePath);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.False(string.IsNullOrWhiteSpace(vm.SaveErrorMessage));
        var report = Assert.Single(state.Errors.Reports);
        Assert.Equal("NodeAssets.TraversalFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
    }

    [Fact]
    public async Task Rename_PathFailureAfterMoveReturnsExistingErrorAndRollsBack()
    {
        using var state = new State();
        var asset = state.AddAsset(state.Source, "Cover", "Game_Cover_01.png");
        var vm = state.Editor();
        vm.Name = "Renamed";
        vm.AssetFileOperations = new NodeAssetFileOperations { AfterFilesMoved = () => throw new IOException("Fixture failure") };
        bool? closed = null;
        vm.RequestClose += value => closed = value;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(closed);
        Assert.Equal("Old", state.Source.Name);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.StartsWith("Library/Old/", asset.RelativePath);
        Assert.False(vm.IsSaving);
        Assert.Equal("NodeAssets.PathUpdateFailed", Assert.Single(state.Errors.Reports).Code);
    }

    [Fact]
    public async Task Rename_IncompleteRollbackDoesNotLetEditorCleanupDeleteImportedRecoveryFile()
    {
        using var state = new State();
        var incoming = state.Temp.CreateFile("incoming.png", "new logo");
        var vm = state.Editor();
        vm.NodeLogoPath = await vm.ImportNodeAssetAsync(incoming, AssetType.Logo);
        vm.Name = "Renamed";
        string? retained = null;
        vm.AssetFileOperations = new NodeAssetFileOperations
        {
            MoveFile = (source, target) =>
            {
                if (source.Contains("/Renamed/")) throw new IOException("Fixture rollback blocked");
                File.Move(source, target);
                if (target.Contains("/Renamed/")) retained = target;
            },
            AfterFilesMoved = () => throw new IOException("Fixture publication blocked")
        };

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(retained);
        Assert.Equal("new logo", File.ReadAllText(retained));
        Assert.False(vm.LastNodeAssetOperation!.RollbackComplete);
        Assert.NotNull(vm.LastNodeAssetOperation.RecoveryManifest);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Contains(vm.LastNodeAssetOperation.RecoveryManifest!, vm.SaveErrorMessage);
        Assert.Equal(["NodeAssets.PathUpdateFailed", "NodeAssets.RollbackIncomplete"], state.Errors.Reports.Select(report => report.Code));
        Assert.All(state.Errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task Move_WithChildAssetsPublishesTreeAndPathsAndSavesOnce()
    {
        using var state = new State();
        var child = new MediaNode("Child");
        state.Source.Children.Add(child);
        var full = state.Temp.CreateFile("Library/Old/Child/Logo/Child_Logo_01.png", "child");
        child.Assets.Add(new MediaAsset { Type = AssetType.Logo, RelativePath = NodeAssetFolderHelper.RelativeLocation(full) });
        var vm = state.Main();
        var saved = 0;
        var moved = await state.Move(vm, save: () => { saved++; return Task.FromResult(true); });
        Assert.True(moved);
        Assert.Equal(1, saved);
        Assert.DoesNotContain(state.Source, state.Roots);
        Assert.Contains(state.Source, state.Target.Children);
        Assert.Equal("Library/Target/Old/Child/Logo/Child_Logo_01.png", child.Assets[0].RelativePath);
        Assert.True(File.Exists(child.Assets[0].AbsolutePath));
        Assert.Empty(state.Errors.Reports);
    }

    [Fact]
    public async Task Move_InvalidTreeReturnsFalseAndReportsOnceWithoutAccessingRecursiveCallers()
    {
        using var state = new State();
        state.Source.Children.Add(state.Source);
        var vm = state.Main();
        Assert.False(await state.Move(vm));
        Assert.Contains(state.Source, state.Roots);
        Assert.Empty(state.Target.Children);
        Assert.Equal("NodeAssets.TraversalFailed", Assert.Single(state.Errors.Reports).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_FileFailureReturnsFalseAndDiagnosesCompleteOrIncompleteRollback(bool blockRollback)
    {
        using var state = new State();
        var asset = state.AddAsset(state.Source, "Cover", "Game_Cover_01.png");
        var vm = state.Main();
        vm.NodeAssetFileOperations = new NodeAssetFileOperations { MoveFile = (source, target) =>
        {
            if (target.StartsWith(state.Temp.GetPath("Library", "Target")))
                throw new IOException("Fixture commit failed");
            if (blockRollback && source.Contains(".retromind_asset_move_staging") &&
                target.StartsWith(state.Temp.GetPath("Library", "Old")))
                throw new IOException("Fixture rollback blocked");
            File.Move(source, target);
        } };
        Assert.False(await state.Move(vm));
        Assert.Contains(state.Source, state.Roots);
        Assert.Empty(state.Target.Children);
        Assert.Equal(!blockRollback, vm.LastNodeAssetOperation!.RollbackComplete);
        if (blockRollback)
        {
            Assert.NotNull(vm.LastNodeAssetOperation.RecoveryManifest);
            Assert.All(vm.LastNodeAssetOperation.RecoveryFiles,
                recovery => Assert.True(File.Exists(AppPaths.ResolveDataPath(recovery.RetainedPath))));
            Assert.Equal(["NodeAssets.MoveFailed", "NodeAssets.RollbackIncomplete"], state.Errors.Reports.Select(report => report.Code));
        }
        else
        {
            Assert.True(File.Exists(asset.AbsolutePath));
            Assert.Equal("NodeAssets.MoveFailed", Assert.Single(state.Errors.Reports).Code);
        }
    }

    [Fact]
    public async Task Move_ProjectedDepthViolationIsRejectedBeforeFilesMove()
    {
        using var state = new State();
        var asset = state.AddAsset(state.Source, "Cover", "Game_Cover_01.png");
        var target = state.Target;
        for (var index = 0; index < 256; index++)
        {
            var child = new MediaNode("n");
            target.Children.Add(child);
            target = child;
        }
        var vm = state.Main();
        var moves = 0;
        vm.NodeAssetFileOperations = new NodeAssetFileOperations { MoveFile = (_, _) => moves++ };
        Assert.False(await vm.TryMoveNodeAsync(state.Source, target, MainWindowViewModel.NodeDropPosition.Inside,
            _ => Task.FromResult(true), _ => { }, () => Task.FromResult(true)));
        Assert.Equal(0, moves);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.Contains(state.Source, state.Roots);
        var report = Assert.Single(state.Errors.Reports);
        Assert.Equal("NodeAssets.TraversalFailed", report.Code);
        Assert.Equal("MaximumDepthExceeded", report.Context!["FailureReason"]);
    }

    [Fact]
    public async Task Move_FailedCollectionPublicationRollsBackTreeFilesAndPaths()
    {
        using var state = new State();
        var asset = state.AddAsset(state.Source, "Cover", "Game_Cover_01.png");
        state.Target.Children.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add) throw new IOException("Fixture collection failure");
        };
        var vm = state.Main();
        var saved = false;
        Assert.False(await state.Move(vm, save: () => { saved = true; return Task.FromResult(true); }));
        Assert.False(saved);
        Assert.Contains(state.Source, state.Roots);
        Assert.Empty(state.Target.Children);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.StartsWith("Library/Old/", asset.RelativePath);
        Assert.True(vm.LastNodeAssetOperation!.RollbackComplete);
        Assert.Equal("NodeAssets.PathUpdateFailed", Assert.Single(state.Errors.Reports).Code);
    }

    [Fact]
    public async Task Move_FailedSaveKeepsCompletedFilesystemAndLibraryMoveForRetry()
    {
        using var state = new State();
        var asset = state.AddAsset(state.Source, "Cover", "Game_Cover_01.png");
        var vm = state.Main();
        Assert.False(await state.Move(vm, save: () => Task.FromResult(false)));
        Assert.True(vm.LastNodeAssetOperation!.Success);
        Assert.Contains(state.Source, state.Target.Children);
        Assert.StartsWith("Library/Target/Old/", asset.RelativePath);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.Empty(state.Errors.Reports); // Persistence owns its separate existing diagnosis/notice.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_FailedMergeRestoresSourceAndTargetCollectionsAndLeavesOtherAssetsUntouched(bool sameFolder)
    {
        using var state = new State();
        var mergeTarget = sameFolder ? state.Target : new MediaNode("Old");
        if (sameFolder) state.Target.Name = "Old";
        else state.Target.Children.Add(mergeTarget);
        var child = new MediaNode("Child");
        state.Source.Children.Add(child);
        var item = new MediaItem("Game");
        state.Source.Items.Add(item);
        var asset = state.AddAsset(item, "Cover", "Game_Cover_01.png");
        var other = new MediaAsset { Type = AssetType.Cover, RelativePath = "Library/Other/Cover/foreign.png" };
        mergeTarget.Assets.Add(other);
        var vm = state.Main();
        var failed = false;
        Assert.False(await state.Move(vm, select: _ =>
        {
            if (!failed) { failed = true; throw new IOException("Fixture selection failed"); }
        }));
        Assert.Contains(state.Source, state.Roots);
        Assert.Equal([child], state.Source.Children);
        Assert.Equal([item], state.Source.Items);
        Assert.Empty(mergeTarget.Children);
        Assert.Empty(mergeTarget.Items);
        Assert.Equal([other], mergeTarget.Assets);
        Assert.Equal("Library/Other/Cover/foreign.png", other.RelativePath);
        Assert.True(File.Exists(asset.AbsolutePath));
        Assert.True(vm.LastNodeAssetOperation!.RollbackComplete);
    }

    private sealed class State : IDisposable
    {
        public TemporaryDirectory Temp { get; } = new();
        private readonly EnvironmentVariableScope _environment;
        public RecordingErrorService Errors { get; } = new();
        public MediaNode Source { get; } = new("Old");
        public MediaNode Target { get; } = new("Target");
        public ObservableCollection<MediaNode> Roots { get; }
        public State()
        {
            _environment = new(("APPIMAGE", Temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            Roots = new() { Source, Target };
        }
        public MediaAsset AddAsset(object owner, string type, string name)
        {
            var full = Temp.CreateFile(Path.Combine("Library", "Old", type, name), "ours");
            var asset = new MediaAsset { Type = Enum.Parse<AssetType>(type), RelativePath = NodeAssetFolderHelper.RelativeLocation(full) };
            if (owner is MediaNode node) node.Assets.Add(asset);
            else ((MediaItem)owner).Assets.Add(asset);
            return asset;
        }
        public NodeSettingsViewModel Editor() => new(Source, Roots, new AppSettings(),
            new FileManagementService(AppPaths.LibraryRoot, Errors), ["Old"], Errors, _ => Task.FromResult(true));
        public MainWindowViewModel Main()
        {
            var vm = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
            typeof(MainWindowViewModel).GetField("_rootItems", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, Roots);
            typeof(MainWindowViewModel).GetField("_errorService", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, Errors);
            return vm;
        }
        public Task<bool> Move(MainWindowViewModel vm, Func<Task<bool>>? save = null, Action<MediaNode?>? select = null) =>
            vm.TryMoveNodeAsync(Source, Target, MainWindowViewModel.NodeDropPosition.Inside, _ => Task.FromResult(true), select ?? (_ => { }),
                save ?? (() => Task.FromResult(true)));
        public void Dispose() { _environment.Dispose(); Temp.Dispose(); }
    }
}
