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

    private static EditMediaViewModel CreateViewModel(MediaItem item)
    {
        var parent = new MediaNode { Name = "Games" };
        parent.Items.Add(item);
        return new EditMediaViewModel(
            item,
            new AppSettings(),
            new FileManagementService(AppPaths.LibraryRoot),
            new WinetricksService(AppPaths.LibraryRoot),
            [parent.Name],
            new UnexpectedIdentificationService(),
            rootNodes: new ObservableCollection<MediaNode> { parent },
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
