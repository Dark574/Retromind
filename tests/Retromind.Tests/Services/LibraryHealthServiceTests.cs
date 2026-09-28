using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class LibraryHealthServiceTests
{
    [Fact]
    public async Task ScanAsync_ReportsMissingAndOrphanedAssetsWithoutChangingLibrary()
    {
        using var temp = new TemporaryDirectory();
        var dataRoot = temp.RootPath;
        var libraryRoot = temp.CreateDirectory("Library");
        var coverFolder = temp.CreateDirectory("Library", "Games", "Cover");
        var existingCover = temp.CreateFile("Library/Games/Cover/Existing_Cover_01.png");
        var orphanedCover = temp.CreateFile("Library/Games/Cover/Orphaned_Cover_01.png", "orphan");
        var missingCover = Path.Combine(coverFolder, "Missing_Cover_01.png");
        var item = new MediaItem("Test game")
        {
            Assets =
            [
                new MediaAsset { Type = AssetType.Cover, RelativePath = Path.GetRelativePath(dataRoot, existingCover) },
                new MediaAsset { Type = AssetType.Cover, RelativePath = Path.GetRelativePath(dataRoot, missingCover) }
            ]
        };
        var root = new MediaNode("Games", NodeType.Area) { Items = [item] };
        var service = new LibraryHealthService(dataRoot, libraryRoot);

        var report = await service.ScanAsync([root]);

        Assert.Contains(report.Issues, issue =>
            issue.Reason == LibraryHealthIssueReason.AssetFileMissing && issue.Location == missingCover);
        Assert.Contains(report.Issues, issue =>
            issue.Reason == LibraryHealthIssueReason.OrphanedAssetFile && issue.Location == orphanedCover);
        Assert.DoesNotContain(report.Issues, issue =>
            issue.Reason == LibraryHealthIssueReason.OrphanedAssetFile && issue.Location == existingCover);
        Assert.Equal(2, item.Assets.Count);
    }

    [Fact]
    public async Task ScanAsync_DoesNotTreatSharedAssetAsOrphaned()
    {
        using var temp = new TemporaryDirectory();
        var dataRoot = temp.RootPath;
        var libraryRoot = temp.CreateDirectory("Library");
        var sharedCover = temp.CreateFile("Library/Games/Cover/Shared_Cover_01.png");
        var relative = Path.GetRelativePath(dataRoot, sharedCover);
        var root = new MediaNode("Games", NodeType.Area)
        {
            Items =
            [
                new MediaItem("One") { Assets = [new MediaAsset { Type = AssetType.Cover, RelativePath = relative }] },
                new MediaItem("Two") { Assets = [new MediaAsset { Type = AssetType.Cover, RelativePath = relative }] }
            ]
        };

        var report = await new LibraryHealthService(dataRoot, libraryRoot).ScanAsync([root]);

        Assert.DoesNotContain(report.Issues, issue => issue.Kind == LibraryHealthIssueKind.OrphanedAsset);
    }

    [Fact]
    public async Task ScanAsync_ReportsMissingLaunchFilesAndDuplicateIds()
    {
        using var temp = new TemporaryDirectory();
        var dataRoot = temp.RootPath;
        var libraryRoot = temp.CreateDirectory("Library");
        var duplicateId = Guid.NewGuid().ToString();
        var root = new MediaNode("Games", NodeType.Area)
        {
            Items =
            [
                new MediaItem("One")
                {
                    Id = duplicateId,
                    Files = [new MediaFileRef { Kind = MediaFileKind.Absolute, Path = temp.GetPath("missing.rom") }]
                },
                new MediaItem("Two") { Id = duplicateId }
            ]
        };

        var report = await new LibraryHealthService(dataRoot, libraryRoot).ScanAsync([root]);

        Assert.Contains(report.Issues, issue => issue.Reason == LibraryHealthIssueReason.LaunchFileMissing);
        Assert.Contains(report.Issues, issue => issue.Reason == LibraryHealthIssueReason.DuplicateItemId);
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("heroic")]
    [InlineData("flatpak")]
    public async Task ScanAsync_AcceptsPathResolvedCommandTargets(string command)
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("Delegated game")
        {
            MediaType = MediaType.Command,
            Files = [new MediaFileRef { Kind = MediaFileKind.Absolute, Path = command }]
        };
        var root = new MediaNode("Games", NodeType.Area) { Items = [item] };

        var report = await new LibraryHealthService(temp.RootPath, libraryRoot).ScanAsync([root]);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Reason is LibraryHealthIssueReason.InvalidLaunchPath or
                LibraryHealthIssueReason.LaunchFileMissing);
    }

    [Fact]
    public async Task ScanAsync_AcceptsLegacySteamCommandStoredAsNativeItem()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("Legacy Steam game")
        {
            MediaType = MediaType.Native,
            LauncherArgs = "steam://rungameid/123",
            Files = [new MediaFileRef { Kind = MediaFileKind.Absolute, Path = "steam" }]
        };
        var root = new MediaNode("Steam", NodeType.Group) { Items = [item] };

        var report = await new LibraryHealthService(temp.RootPath, libraryRoot).ScanAsync([root]);

        Assert.DoesNotContain(report.Issues, issue =>
            issue.Reason is LibraryHealthIssueReason.InvalidLaunchPath or
                LibraryHealthIssueReason.LaunchFileMissing);
    }

    [Fact]
    public async Task ScanAsync_DoesNotAcceptBareRelativeNativeFileAsCommand()
    {
        using var temp = new TemporaryDirectory();
        var libraryRoot = temp.CreateDirectory("Library");
        var item = new MediaItem("Broken native item")
        {
            MediaType = MediaType.Native,
            Files = [new MediaFileRef { Kind = MediaFileKind.Absolute, Path = "missing.rom" }]
        };
        var root = new MediaNode("Games", NodeType.Area) { Items = [item] };

        var report = await new LibraryHealthService(temp.RootPath, libraryRoot).ScanAsync([root]);

        Assert.Contains(report.Issues, issue => issue.Reason == LibraryHealthIssueReason.InvalidLaunchPath);
    }
}
