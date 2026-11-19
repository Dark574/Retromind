using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopMediaProjectionBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildAsync_SortsDescendantItemsTogetherAndFiltersProtectedItems(bool filterProtected)
    {
        AvaloniaTestDispatcher.EnsureStarted();
        var rootItem = new MediaItem("Game 10");
        var childItem = new MediaItem("Later title") { SortTitle = "Game 2" };
        var protectedItem = new MediaItem("Game 1") { IsProtected = true };
        var child = new MediaNode("Child") { Items = [childItem, protectedItem] };
        var root = new MediaNode("Root") { Items = [rootItem], Children = [child] };
        ObservableCollection<MediaNode> roots = [root];

        var collected = await DesktopMediaProjectionBuilder.CollectItemsAsync(
            root, filterProtected, CancellationToken.None);
        var (items, _) = await DesktopMediaProjectionBuilder.BuildAsync(
            root, roots, filterProtected, CancellationToken.None);

        MediaItem[] expectedCollected = filterProtected ? [rootItem, childItem] : [rootItem, childItem, protectedItem];
        MediaItem[] expectedSorted = filterProtected ? [childItem, rootItem] : [protectedItem, childItem, rootItem];
        Assert.Equal(expectedCollected, collected);
        Assert.Equal(expectedSorted, items);
        Assert.Same(rootItem, Assert.Single(root.Items));
        Assert.Equal(2, child.Items.Count);
    }

    [Fact]
    public async Task BuildAsync_InheritsArtworkButRespectsMusicOverrideAndDefersChangesUntilCommit()
    {
        AvaloniaTestDispatcher.EnsureStarted();
        var item = new MediaItem("Game")
        {
            Assets =
            [
                Asset(AssetType.Cover, "first-cover.png"),
                Asset(AssetType.Cover, "second-cover.png"),
                Asset(AssetType.Wallpaper, "first-wallpaper.png"),
                Asset(AssetType.Wallpaper, "second-wallpaper.png"),
                Asset(AssetType.Video, "video.mp4"),
                Asset(AssetType.Music, "first-song.mp3"),
                Asset(AssetType.Music, "second-song.mp3")
            ]
        };
        item.SetActiveAsset(AssetType.Cover, "previous-cover.png");
        item.SetActiveAsset(AssetType.Music, "second-song.mp3");
        var child = new MediaNode("Child") { Items = [item], RandomizeMusic = false };
        var root = new MediaNode("Root") { Children = [child], RandomizeCovers = true, RandomizeMusic = true };
        ObservableCollection<MediaNode> roots = [root];

        var (_, plan) = await DesktopMediaProjectionBuilder.BuildAsync(
            child, roots, filterProtected: false, CancellationToken.None);
        var selection = Assert.Single(plan);

        Assert.Equal("previous-cover.png", item.GetPrimaryAssetPath(AssetType.Cover));
        Assert.Equal("second-song.mp3", item.GetPrimaryAssetPath(AssetType.Music));
        Assert.Contains(selection.Cover, new[] { "first-cover.png", "second-cover.png" });
        Assert.Contains(selection.Wallpaper, new[] { "first-wallpaper.png", "second-wallpaper.png" });
        Assert.Null(selection.Video);
        Assert.Null(selection.Music);

        await AvaloniaTestDispatcher.InvokeAsync(_ =>
            DesktopMediaProjectionBuilder.ApplyRandomizationPlan(plan));

        Assert.Equal(selection.Cover, item.GetPrimaryAssetPath(AssetType.Cover));
        Assert.Equal(selection.Wallpaper, item.GetPrimaryAssetPath(AssetType.Wallpaper));
        Assert.Equal("video.mp4", item.GetPrimaryAssetPath(AssetType.Video));
        Assert.Equal("first-song.mp3", item.GetPrimaryAssetPath(AssetType.Music));
    }

    [Fact]
    public async Task BuildAsync_CanceledRequestDoesNotChangeActiveAssets()
    {
        var item = new MediaItem("Game");
        item.SetActiveAsset(AssetType.Cover, "current-cover.png");
        var node = new MediaNode("Root") { Items = [item] };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DesktopMediaProjectionBuilder.BuildAsync(node, [node], filterProtected: false, cts.Token));

        Assert.Equal("current-cover.png", item.GetPrimaryAssetPath(AssetType.Cover));
    }

    private static MediaAsset Asset(AssetType type, string path) => new() { Type = type, RelativePath = path };
}
