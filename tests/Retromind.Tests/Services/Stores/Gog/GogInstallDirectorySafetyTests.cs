using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogInstallDirectorySafetyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Assess_InvalidPath_ReturnsInvalidPath(string? installPath)
    {
        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.InvalidPath, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/usr")]
    [InlineData("/usr/local/share")]
    [InlineData("/etc")]
    [InlineData("/var/lib")]
    public void Assess_DangerousLinuxPath_ReturnsDangerousPath(string installPath)
    {
        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.DangerousPath, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_UserHome_ReturnsDangerousPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(home));

        var assessment = GogInstallDirectorySafety.Assess(
            home,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.DangerousPath, assessment.Status);
    }

    [Theory]
    [MemberData(nameof(RetromindRootPaths))]
    public void Assess_RetromindRoot_ReturnsDangerousPath(string installPath)
    {
        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.DangerousPath, assessment.Status);
    }

    [Fact]
    public void Assess_MissingDirectory_ReturnsNewDirectory()
    {
        using var temp = new TemporaryDirectory();
        var missingPath = temp.GetPath("new-install");

        var assessment = GogInstallDirectorySafety.Assess(
            missingPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.NewDirectory, assessment.Status);
        Assert.True(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_EmptyDirectory_ReturnsEmptyDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("empty-install");

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.EmptyDirectory, assessment.Status);
        Assert.True(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_NonEmptyDirectoryWithoutMarker_ReturnsUnownedAndPreservesContents()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("unowned-install");
        var sentinelPath = temp.CreateFile("unowned-install/sentinel.txt", "keep me");

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.UnownedDirectory, assessment.Status);
        Assert.False(assessment.IsAllowed);
        Assert.Equal("keep me", File.ReadAllText(sentinelPath));
    }

    [Fact]
    public void Assess_MatchingMarker_ReturnsOwnedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("owned-install");
        var item = CreateGogItem();
        GogInstallDirectorySafety.WriteMarker(installPath, item);
        temp.CreateFile("owned-install/game.bin");

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            item,
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.OwnedDirectory, assessment.Status);
        Assert.True(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_MarkerForDifferentMediaItem_ReturnsUnownedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("wrong-item-install");
        GogInstallDirectorySafety.WriteMarker(installPath, CreateGogItem(mediaItemId: "other-item"));

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(mediaItemId: "expected-item"),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.UnownedDirectory, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_MarkerForDifferentStoreGame_ReturnsUnownedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("wrong-game-install");
        GogInstallDirectorySafety.WriteMarker(installPath, CreateGogItem(storeGameId: "other-game"));

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(storeGameId: "expected-game"),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.UnownedDirectory, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_CorruptMarker_ReturnsUnownedDirectory()
    {
        using var temp = new TemporaryDirectory();
        var installPath = temp.CreateDirectory("corrupt-marker-install");
        temp.CreateFile(
            $"corrupt-marker-install/{GogInstallDirectorySafety.MarkerFileName}",
            "not valid json");

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.UnownedDirectory, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_SymbolicLinkAsInstallDirectory_ReturnsSymbolicLink()
    {
        using var temp = new TemporaryDirectory();
        var targetPath = temp.CreateDirectory("real-install");
        var linkPath = temp.GetPath("linked-install");
        Directory.CreateSymbolicLink(linkPath, targetPath);

        var assessment = GogInstallDirectorySafety.Assess(
            linkPath,
            CreateGogItem(),
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.SymbolicLink, assessment.Status);
        Assert.False(assessment.IsAllowed);
    }

    [Fact]
    public void Assess_NestedSymbolicLink_ReturnsSymbolicLinkAndDoesNotTouchExternalTarget()
    {
        using var installTemp = new TemporaryDirectory();
        using var externalTemp = new TemporaryDirectory();
        var installPath = installTemp.CreateDirectory("owned-install");
        var item = CreateGogItem();
        GogInstallDirectorySafety.WriteMarker(installPath, item);
        var externalSentinel = externalTemp.CreateFile("outside.txt", "outside remains");
        Directory.CreateSymbolicLink(
            installTemp.GetPath("owned-install/external-link"),
            externalTemp.RootPath);

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            item,
            rejectSymbolicLinks: true);

        Assert.Equal(GogInstallDirectoryStatus.SymbolicLink, assessment.Status);
        Assert.False(assessment.IsAllowed);
        Assert.Equal("outside remains", File.ReadAllText(externalSentinel));
    }

    public static TheoryData<string> RetromindRootPaths => new()
    {
        AppPaths.DataRoot,
        AppPaths.LibraryRoot
    };

    private static MediaItem CreateGogItem(
        string mediaItemId = "expected-item",
        string storeGameId = "expected-game")
    {
        var item = new MediaItem
        {
            Id = mediaItemId,
            Title = "Test game"
        };
        item.CustomFields["Store.GameId"] = storeGameId;
        return item;
    }
}
