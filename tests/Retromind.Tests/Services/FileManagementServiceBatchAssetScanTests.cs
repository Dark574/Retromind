using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class FileManagementServiceBatchAssetScanTests
{
    [Fact]
    public void ScanItemsAssets_MapsRegularAndDisplayPrefixedAssets()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var libraryRoot = temporaryDirectory.CreateDirectory("Library");
        temporaryDirectory.CreateFile(
            "Library/Games/Cover/Game__12345678_Cover_01.png");
        temporaryDirectory.CreateFile(
            "Library/Games/Manual/Guide__RM__Game__12345678_Manual_01.pdf");
        temporaryDirectory.CreateFile(
            "Library/Games/Cover/Other__87654321_Cover_01.png");
        temporaryDirectory.CreateFile(
            "Library/Games/Cover/unrelated.png");
        var game = new MediaItem("Game") { Id = "12345678-rest" };
        var other = new MediaItem("Other") { Id = "87654321-rest" };
        var service = new FileManagementService(libraryRoot);

        var results = service.ScanItemsAssets([game, other], ["Games"]);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, results[0].Assets.Count);
        Assert.Contains(results[0].Assets, asset => asset.Type == AssetType.Cover);
        Assert.Contains(results[0].Assets, asset => asset.Type == AssetType.Manual);
        Assert.Collection(
            results[1].Assets,
            asset => Assert.Equal(AssetType.Cover, asset.Type));
    }

    [Fact]
    public void ScanItemsAssets_ObservesCancellationBeforeScanning()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var libraryRoot = temporaryDirectory.CreateDirectory("Library");
        var service = new FileManagementService(libraryRoot);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.ScanItemsAssets(
                [new MediaItem("Game")],
                ["Games"],
                cancellationToken: cancellation.Token));
    }
}
