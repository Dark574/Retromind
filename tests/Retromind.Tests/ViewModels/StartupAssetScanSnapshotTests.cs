using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class StartupAssetScanSnapshotTests
{
    [Fact]
    public void BatchMatchesIndividualScans_WithCollidingPrefixesAndPortablePaths()
    {
        using var profile = new TemporaryDirectory();
        using var env = new EnvironmentVariableScope(("APPIMAGE", profile.GetPath("Retromind.AppImage")));
        var library = profile.CreateDirectory("Library");
        var scanner = new FileManagementService(library, new RecordingErrorService());
        var node = new MediaNode("Games");
        var first = new MediaItem("Same Title") { Id = "12345678-1" };
        var sharedPrefix = new MediaItem("Same Title") { Id = "12345678-2" };
        var differentId = new MediaItem("Same Title") { Id = "87654321-3" };
        var missing = new MediaItem("Missing") { Id = "11223344-4" };
        foreach (var item in new[] { first, sharedPrefix, differentId, missing }) node.Items.Add(item);
        ObservableCollection<MediaNode> roots = [node];
        const string cover = "Library/Games/Cover/Same_Title__12345678_Cover_01.png";
        const string manual = "Library/Games/Manual/Guide__RM__Same_Title__12345678_Manual_01.pdf";
        profile.CreateFile(cover);
        profile.CreateFile(manual);
        profile.CreateFile("Library/Games/Cover/Same_Title__87654321_Cover_01.png");
        profile.CreateFile("Library/Games/Cover/Same_Title__12345678_Logo_01.png");
        profile.CreateFile("Library/Games/Cover/unrelated.png");

        var snapshot = Assert.IsType<StartupAssetScanSnapshot>(StartupAssetScanSnapshot.Capture(roots, node, default));
        var scanned = scanner.ScanItemsAssets(snapshot.Items, static item => item.Prefix, snapshot.NodePath);
        foreach (var row in scanned)
            Assert.Equal(Paths(scanner.ScanItemAssets(row.Item.Item, snapshot.NodePath)), Paths(row.Assets));
        Assert.Contains(scanned[0].Assets, asset => asset.RelativePath == cover);
        Assert.Contains(scanned[1].Assets, asset => asset.RelativePath == manual);
        Assert.Single(scanned[2].Assets);
        Assert.Empty(scanned[3].Assets);
        Assert.All(scanned.SelectMany(row => row.Assets), asset => Assert.False(Path.IsPathRooted(asset.RelativePath)));

        Assert.True(snapshot.ApplyBatch(roots, scanned, 0, default));
        var existingCover = first.Assets[0];
        snapshot = Assert.IsType<StartupAssetScanSnapshot>(StartupAssetScanSnapshot.Capture(roots, node, default));
        scanned = scanner.ScanItemsAssets(snapshot.Items, static item => item.Prefix, snapshot.NodePath);
        Assert.False(snapshot.ApplyBatch(roots, scanned, 0, default));
        Assert.Equal(2, first.Assets.Count);
        Assert.Same(existingCover, first.Assets[0]);
    }

    [Fact]
    public void EmptyNodeAndMissingDirectoryReturnOnlyEmptyResults()
    {
        using var profile = new TemporaryDirectory();
        var scanner = new FileManagementService(profile.CreateDirectory("Library"), new RecordingErrorService());
        var empty = new MediaNode("Empty");
        var missing = new MediaNode("Missing");
        missing.Items.Add(new MediaItem("Game"));
        ObservableCollection<MediaNode> roots = [empty, missing];
        var snapshot = StartupAssetScanSnapshot.Capture(roots, empty, default)!;
        Assert.Empty(scanner.ScanItemsAssets(snapshot.Items, static item => item.Prefix, snapshot.NodePath));
        snapshot = StartupAssetScanSnapshot.Capture(roots, missing, default)!;
        Assert.Empty(Assert.Single(scanner.ScanItemsAssets(snapshot.Items, static item => item.Prefix, snapshot.NodePath)).Assets);
    }

    [Fact]
    public void WorkerUsesTheCapturedPrefix_EvenIfTitleChangesAndReturns()
    {
        using var profile = new TemporaryDirectory();
        using var env = new EnvironmentVariableScope(("APPIMAGE", profile.GetPath("Retromind.AppImage")));
        var library = profile.CreateDirectory("Library");
        profile.CreateFile("Library/Games/Cover/Original__12345678_Cover_01.png");
        profile.CreateFile("Library/Games/Cover/Next__12345678_Cover_01.png");
        var scanner = new FileManagementService(library, new RecordingErrorService());
        var node = new MediaNode("Games");
        var item = new MediaItem("Original") { Id = "12345678-rest" };
        node.Items.Add(item);
        ObservableCollection<MediaNode> roots = [node];
        var snapshot = StartupAssetScanSnapshot.Capture(roots, node, default)!;

        item.Title = "Next";
        var scanned = scanner.ScanItemsAssets(snapshot.Items, static item => item.Prefix, snapshot.NodePath);
        item.Title = "Original";

        Assert.True(snapshot.ApplyBatch(roots, scanned, 0, default));
        Assert.Equal("Library/Games/Cover/Original__12345678_Cover_01.png", Assert.Single(item.Assets).RelativePath);
    }

    [Theory]
    [InlineData("item title")]
    [InlineData("item id")]
    [InlineData("asset path")]
    [InlineData("asset type")]
    [InlineData("asset collection")]
    [InlineData("item moved")]
    [InlineData("item replaced")]
    [InlineData("ancestor renamed")]
    [InlineData("node moved")]
    [InlineData("node removed")]
    public void ObsoleteSnapshotCannotReplaceLiveAssets(string edit)
    {
        var parent = new MediaNode("Parent");
        var other = new MediaNode("Other");
        var node = new MediaNode("Games");
        var item = new MediaItem("Game");
        var original = new MediaAsset { Type = AssetType.Cover, RelativePath = "original.png" };
        item.Assets.Add(original);
        node.Items.Add(item);
        parent.Children.Add(node);
        ObservableCollection<MediaNode> roots = [parent, other];
        var snapshot = StartupAssetScanSnapshot.Capture(roots, node, default)!;
        List<(StartupAssetScanSnapshot.ItemSnapshot, List<MediaAsset>)> scanned =
            [(snapshot.Items[0], [new MediaAsset { Type = AssetType.Cover, RelativePath = "scanned.png" }])];

        switch (edit)
        {
            case "item title": item.Title = "Renamed"; break;
            case "item id": item.Id = "updated-id"; break;
            case "asset path": original.RelativePath = "renamed.png"; break;
            case "asset type": original.Type = AssetType.Screenshot; break;
            case "asset collection": item.Assets = [original]; break;
            case "item moved": node.Items.Remove(item); other.Items.Add(item); break;
            case "item replaced": node.Items[0] = new MediaItem("Game") { Id = item.Id }; break;
            case "ancestor renamed": parent.Name = "Renamed"; break;
            case "node moved": parent.Children.Remove(node); other.Children.Add(node); break;
            case "node removed": roots.Remove(parent); break;
            default: throw new ArgumentOutOfRangeException(nameof(edit));
        }
        Assert.False(snapshot.ApplyBatch(roots, scanned, 0, default));
        Assert.Same(original, Assert.Single(item.Assets));
    }

    [Fact]
    public void CancellationAfterScanAndBetweenItemsPreservesUnappliedAssets()
    {
        var node = new MediaNode("Games");
        var first = new MediaItem("First");
        var second = new MediaItem("Second");
        var original = new MediaAsset { Type = AssetType.Cover, RelativePath = "second.png" };
        second.Assets.Add(original);
        node.Items.Add(first);
        node.Items.Add(second);
        ObservableCollection<MediaNode> roots = [node];
        var snapshot = StartupAssetScanSnapshot.Capture(roots, node, default)!;
        List<(StartupAssetScanSnapshot.ItemSnapshot, List<MediaAsset>)> scanned =
            [(snapshot.Items[0], [new MediaAsset { Type = AssetType.Cover, RelativePath = "first.png" }]),
             (snapshot.Items[1], [])];

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => StartupAssetScanSnapshot.Capture(roots, node, alreadyCancelled.Token));
        Assert.Throws<OperationCanceledException>(() => snapshot.ApplyBatch(roots, scanned, 0, alreadyCancelled.Token));
        Assert.Empty(first.Assets);

        using var duringApply = new CancellationTokenSource();
        first.Assets.CollectionChanged += (_, _) => duringApply.Cancel();
        Assert.True(snapshot.ApplyBatch(roots, scanned, 0, duringApply.Token));
        Assert.Single(first.Assets);
        Assert.Same(original, Assert.Single(second.Assets));
    }

    [Fact]
    public void ASecondBatchRechecksChangesMadeAfterTheFirstBatch()
    {
        var node = new MediaNode("Games");
        for (var i = 0; i < 501; i++) node.Items.Add(new MediaItem($"Item {i}"));
        ObservableCollection<MediaNode> roots = [node];
        var snapshot = StartupAssetScanSnapshot.Capture(roots, node, default)!;
        var scanned = snapshot.Items.Select((item, i) =>
            (item, new List<MediaAsset> { new() { Type = AssetType.Cover, RelativePath = $"{i}.png" } })).ToList();

        Assert.True(snapshot.ApplyBatch(roots, scanned, 0, default));
        Assert.Single(node.Items[499].Assets);
        var last = node.Items[500];
        Assert.Empty(last.Assets);
        var edited = new MediaAsset { Type = AssetType.Cover, RelativePath = "user-change.png" };
        last.Assets.Add(edited);
        Assert.False(snapshot.ApplyBatch(roots, scanned, 500, default));
        Assert.Same(edited, Assert.Single(last.Assets));
    }

    private static IEnumerable<(AssetType Type, string RelativePath)> Paths(IEnumerable<MediaAsset> assets)
        => assets.Select(asset => (asset.Type, asset.RelativePath)).OrderBy(x => x.Type).ThenBy(x => x.RelativePath);
}
