using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services;

public sealed class LaunchPlaylistServiceTests
{
    [Fact]
    public void ResolveLaunchFilePath_ReturnsNullWithoutLaunchFiles()
    {
        using var temp = new TemporaryDirectory();
        var service = new LaunchPlaylistService(temp.RootPath);

        var result = service.ResolveLaunchFilePath(
            new MediaItem("Catalog entry"),
            ["Games"],
            usePlaylistForMultiDisc: true);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveLaunchFilePath_ReturnsSingleFileWithoutCreatingPlaylist()
    {
        using var temp = new TemporaryDirectory();
        var item = new MediaItem("Single file")
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = "/games/game.chd"
                }
            ]
        };
        var service = new LaunchPlaylistService(temp.RootPath);

        var result = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: true);

        Assert.Equal("/games/game.chd", result);
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
    }

    [Fact]
    public void ResolveLaunchFilePath_ReturnsPrimaryFileWhenPlaylistModeIsDisabled()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var service = new LaunchPlaylistService(temp.RootPath);

        var result = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: false);

        Assert.Equal("/games/disc-1.chd", result);
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
    }

    [Fact]
    public void ResolveLaunchFilePath_ReturnsPrimaryFileWithoutANodePath()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var service = new LaunchPlaylistService(temp.RootPath);

        var result = service.ResolveLaunchFilePath(item, nodePath: null, usePlaylistForMultiDisc: true);

        Assert.Equal("/games/disc-1.chd", result);
    }

    [Fact]
    public void ResolveLaunchFilePath_CreatesStableOrderedPlaylistInsideNodeFolder()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        item.Id = "item-id";
        item.Title = "Multi  Disc Game";
        var service = new LaunchPlaylistService(temp.RootPath);

        var result = service.ResolveLaunchFilePath(
            item,
            ["Games", "PlayStation 1"],
            usePlaylistForMultiDisc: true);

        var expectedPath = temp.GetPath(
            "Games",
            "PlayStation_1",
            "Playlists",
            "item-id_Multi_Disc_Game.m3u");
        Assert.Equal(expectedPath, result);
        Assert.Equal(
            ["/games/disc-1.chd", "/games/disc-2.chd", "/games/disc-3.chd"],
            File.ReadAllLines(expectedPath));
    }

    [Fact]
    public void ResolveLaunchFilePath_ResolvesPortableRelativePlaylistEntries()
    {
        using var temp = new TemporaryDirectory();
        const string relativeDiscPath = "Library/Games/disc-2.chd";
        var item = new MediaItem("Portable discs")
        {
            Id = "portable-item",
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = "/games/disc-1.chd",
                    Index = 1
                },
                new MediaFileRef
                {
                    Kind = MediaFileKind.LibraryRelative,
                    Path = relativeDiscPath,
                    Index = 2
                }
            ]
        };
        var service = new LaunchPlaylistService(temp.RootPath);

        var playlistPath = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: true);

        Assert.NotNull(playlistPath);
        Assert.Equal(
            [
                "/games/disc-1.chd",
                AppPaths.ResolveDataPathInsideRootOrEmpty(relativeDiscPath)
            ],
            File.ReadAllLines(playlistPath));
    }

    [Fact]
    public void ResolveLaunchFilePath_FallsBackToPrimaryFileWhenPlaylistCannotBeWritten()
    {
        using var temp = new TemporaryDirectory();
        var invalidLibraryRoot = temp.CreateFile("not-a-directory");
        var item = CreateMultiDiscItem();
        var service = new LaunchPlaylistService(invalidLibraryRoot);

        var result = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: true);

        Assert.Equal("/games/disc-1.chd", result);
    }

    private static MediaItem CreateMultiDiscItem() => new("Multi-disc game")
    {
        Files =
        [
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = "/games/disc-3.chd",
                Label = "Disc 3",
                Index = 3
            },
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = "/games/disc-1.chd",
                Label = "Disc 1",
                Index = 1
            },
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = "/games/disc-2.chd",
                Label = "Disc 2",
                Index = 2
            }
        ]
    };
}
