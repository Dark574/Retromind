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
        var service = new FileManagementService(libraryRoot, new RecordingErrorService());

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
        var service = new FileManagementService(libraryRoot, new RecordingErrorService());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            service.ScanItemsAssets(
                [new MediaItem("Game")],
                ["Games"],
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ScanItemsAssets_ObservesCancellationDuringEnumeration()
    {
        using var profile = new TemporaryDirectory();
        var library = profile.CreateDirectory("Library");
        var scanner = new FileManagementService(library, new RecordingErrorService());
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        for (var i = 0; i < 300; i++)
            profile.CreateFile($"Library/Games/Cover/Game__12345678_Cover_{i}.png");
        using var cancellation = new CancellationTokenSource();
        var progress = new ScanProgress(p =>
        {
            if (p.FilesScanned == 256)
                cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            scanner.ScanItemsAssets([item], ["Games"], progress, cancellation.Token));
        Assert.Empty(item.Assets);
    }

    [Fact]
    public void ScanItemsAssets_UnreadableDirectoryDoesNotReturnPartialResults()
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var profile = new TemporaryDirectory();
        var library = profile.CreateDirectory("Library");
        profile.CreateFile("Library/Games/Cover/Game__12345678_Cover_01.png");
        profile.CreateFile("Library/Games/Wallpaper/Game__12345678_Wallpaper_01.png");
        var denied = profile.GetPath("Library/Games/Wallpaper");
        var permissions = File.GetUnixFileMode(denied);
        var scanner = new FileManagementService(library, new RecordingErrorService());
        var item = new MediaItem("Game") { Id = "12345678-rest" };
        var original = new MediaAsset { Type = AssetType.Wallpaper, RelativePath = "original.png" };
        item.Assets.Add(original);

        File.SetUnixFileMode(denied, UnixFileMode.None);
        try
        {
            // A privileged runner can bypass Unix mode bits.
            if (Record.Exception(() => Directory.GetFiles(denied)) is not UnauthorizedAccessException)
                return;
            Assert.Throws<UnauthorizedAccessException>(() => scanner.ScanItemsAssets([item], ["Games"]));
            Assert.Same(original, Assert.Single(item.Assets));
        }
        finally
        {
            File.SetUnixFileMode(denied, permissions);
        }
    }

    private sealed class ScanProgress(Action<FolderImportProgress> report) : IProgress<FolderImportProgress>
    {
        public void Report(FolderImportProgress value) => report(value);
    }
}
