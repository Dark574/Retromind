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
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

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
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

        var result = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: true);

        Assert.Equal("/games/game.chd", result);
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
    }

    [Fact]
    public void ResolveLaunchFilePath_ReturnsPrimaryFileWhenPlaylistModeIsDisabled()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

        var result = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: false);

        Assert.Equal("/games/disc-1.chd", result);
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
    }

    [Fact]
    public void ResolveLaunchFilePath_ReturnsPrimaryFileWithoutANodePath()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

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
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

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
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

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
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(invalidLibraryRoot, errors);

        var result = service.ResolveLaunchFilePath(item, ["Games"], usePlaylistForMultiDisc: true);

        Assert.Equal("/games/disc-1.chd", result);
        AssertFailure(errors, "CreateDirectory");
    }

    [Fact]
    public void ResolveLaunchFilePath_UpdatesExistingPlaylistWithoutChangingFormatOrConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(temp.RootPath, errors);
        var item = CreateMultiDiscItem();
        item.Title = "Multi Déjà Vu";
        var path = service.ResolveLaunchFilePath(item, ["Games"], true)!;
        item.Files[0].Path = "/games/disc-3-updated.chd";

        Assert.Equal(path, service.ResolveLaunchFilePath(item, ["Games"], true));

        Assert.Equal("/games/disc-1.chd" + Environment.NewLine +
                     "/games/disc-2.chd" + Environment.NewLine +
                     "/games/disc-3-updated.chd" + Environment.NewLine, File.ReadAllText(path));
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal("/games/disc-3-updated.chd", item.Files[0].Path);
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public void ResolveLaunchFilePath_OrdersByIndexThenLabelThenPath()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        item.Files =
        [
            new() { Path = "/z.chd", Label = "B" },
            new() { Path = "/d.chd", Label = "a" },
            new() { Path = "/c.chd", Label = "A" },
            new() { Path = "/b.chd", Index = 2 },
            new() { Path = "/a.chd", Index = 1 }
        ];
        var service = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService());

        var path = service.ResolveLaunchFilePath(item, ["Games"], true)!;
        item.Files.Reverse();
        service.ResolveLaunchFilePath(item, ["Games"], true);

        Assert.Equal(["/a.chd", "/b.chd", "/c.chd", "/d.chd", "/z.chd"], File.ReadAllLines(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveLaunchFilePath_PartialWriteFailurePreservesOldPlaylistAndCleansTemporaryFile(bool reportThrows)
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var oldPath = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService())
            .ResolveLaunchFilePath(item, ["Games"], true)!;
        var oldBytes = File.ReadAllBytes(oldPath);
        var errors = new RecordingErrorService { ThrowOnReport = reportThrows };
        string? temporaryPath = null;
        item.Files[0].Path = "/games/new-disc-3.chd";
        var service = new LaunchPlaylistService(temp.RootPath, errors, (stream, _) =>
        {
            temporaryPath = ((FileStream)stream).Name;
            Assert.Equal(Path.GetDirectoryName(oldPath), Path.GetDirectoryName(temporaryPath));
            stream.Write("partial new playlist"u8);
            stream.Flush();
            Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
            throw new IOException("Simulated disk full after partial write.");
        });

        var result = service.ResolveLaunchFilePath(item, ["Games"], true);

        Assert.Equal("/games/disc-1.chd", result);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
        Assert.NotNull(temporaryPath);
        Assert.False(File.Exists(temporaryPath));
        Assert.Equal([oldPath], Directory.GetFiles(Path.GetDirectoryName(oldPath)!));
        var report = AssertFailure(errors, "WritePlaylist");
        Assert.Equal(oldPath, report.Context!["PlaylistPath"]);

        // A later launch retries publication and can update the retained playlist.
        Assert.Equal(oldPath, new LaunchPlaylistService(temp.RootPath, errors)
            .ResolveLaunchFilePath(item, ["Games"], true));
        Assert.Equal("/games/new-disc-3.chd", File.ReadAllLines(oldPath)[2]);
    }

    [Fact]
    public void ResolveLaunchFilePath_FirstWriteFailureLeavesNoPlaylistOrTemporaryFile()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(temp.RootPath, errors, (stream, _) =>
        {
            stream.WriteByte(42);
            throw new IOException("Simulated partial write");
        });

        Assert.Equal("/games/disc-1.chd", service.ResolveLaunchFilePath(item, ["Games"], true));
        Assert.Empty(Directory.GetFiles(temp.GetPath("Games", "Playlists")));
        AssertFailure(errors, "WritePlaylist");
    }

    [Fact]
    public void ResolveLaunchFilePath_PublishFailureCleansCompletedTemporaryFile()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var errors = new RecordingErrorService();
        // A directory at the final filename deterministically rejects the rename, even as root.
        var target = temp.CreateDirectory("Games", "Playlists", $"{item.Id}_Multi-disc_game.m3u");
        var service = new LaunchPlaylistService(temp.RootPath, errors);

        Assert.Equal("/games/disc-1.chd", service.ResolveLaunchFilePath(item, ["Games"], true));
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(temp.GetPath("Games", "Playlists")));
        AssertFailure(errors, "PublishPlaylist");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/games/disc\n2.chd")]
    [InlineData("/games/disc\r2.chd")]
    [InlineData("/games/disc\0.chd")]
    public void ResolveLaunchFilePath_InvalidDiscDoesNotPublishIncompletePlaylist(string invalidPath)
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        var oldPath = new LaunchPlaylistService(temp.RootPath, new RecordingErrorService())
            .ResolveLaunchFilePath(item, ["Games"], true)!;
        var oldBytes = File.ReadAllBytes(oldPath);
        item.Files[0].Path = invalidPath;
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(temp.RootPath, errors);

        Assert.Equal("/games/disc-1.chd", service.ResolveLaunchFilePath(item, ["Games"], true));
        Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
        AssertFailure(errors, "ResolveDiscs");
    }

    [Theory]
    [InlineData(MediaFileKind.LibraryRelative, "../../outside.chd")]
    [InlineData((MediaFileKind)99, "/games/disc-2.chd")]
    public void ResolveLaunchFilePath_UnresolvableDiscFallsBackWithOneReport(MediaFileKind kind, string path)
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        item.Files[0].Kind = kind;
        item.Files[0].Path = path;
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(temp.RootPath, errors);

        Assert.Equal("/games/disc-1.chd", service.ResolveLaunchFilePath(item, ["Games"], true));
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
        AssertFailure(errors, "ResolveDiscs");
    }

    [Fact]
    public void ResolveLaunchFilePath_InvalidPrimaryDoesNotSilentlyStartAnotherDiscOrOldPlaylist()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        item.Files[1].Path = ""; // Explicitly configured Disc 1.
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(temp.RootPath, errors);

        Assert.Null(service.ResolveLaunchFilePath(item, ["Games"], true));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("False", report.Context!["FallbackToPrimary"]);
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
    }

    [Fact]
    public void ResolveLaunchFilePath_ResolvesPortablePathsAgainAfterDataRootMoves()
    {
        using var temp = new TemporaryDirectory();
        var originalRoot = temp.CreateDirectory("original");
        var movedRoot = temp.CreateDirectory("moved");
        var item = CreateMultiDiscItem();
        foreach (var disc in item.Files)
        {
            disc.Kind = MediaFileKind.LibraryRelative;
            disc.Path = $"Library/Games/disc-{disc.Index}.chd";
        }
        var storedPaths = item.Files.Select(disc => disc.Path).ToArray();

        foreach (var root in new[] { originalRoot, movedRoot })
        {
            using var environment = new EnvironmentVariableScope(("APPIMAGE", Path.Combine(root, "Retromind.AppImage")));
            var service = new LaunchPlaylistService(Path.Combine(root, "Library"), new RecordingErrorService());
            var path = service.ResolveLaunchFilePath(item, ["Games"], true)!;

            Assert.Equal(Enumerable.Range(1, 3).Select(index => Path.Combine(root, "Library", "Games", $"disc-{index}.chd")),
                File.ReadAllLines(path));
            Assert.Equal(storedPaths, item.Files.Select(disc => disc.Path));
        }
    }

    [Fact]
    public void ResolveLaunchFilePath_MissingDiscReferenceRejectsPlaylistWithoutInventingPrimary()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateMultiDiscItem();
        item.Files.Add(null!);
        var errors = new RecordingErrorService();
        var service = new LaunchPlaylistService(temp.RootPath, errors);

        Assert.Null(service.ResolveLaunchFilePath(item, ["Games"], true));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Launcher.PlaylistCreationFailed", report.Code);
        Assert.Equal("ResolveDiscs", report.Context!["Operation"]);
        Assert.Equal("False", report.Context["FallbackToPrimary"]);
        Assert.False(Directory.Exists(temp.GetPath("Games", "Playlists")));
    }

    [Fact]
    public void ResolveLaunchFilePath_UsesUniqueTemporaryFilesForRepeatedAttempts()
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var paths = new List<string>();
        var service = new LaunchPlaylistService(temp.RootPath, errors, (stream, _) =>
        {
            paths.Add(((FileStream)stream).Name);
            stream.WriteByte(42);
            throw new IOException("Simulated partial write");
        });
        var item = CreateMultiDiscItem();

        service.ResolveLaunchFilePath(item, ["Games"], true);
        service.ResolveLaunchFilePath(item, ["Games"], true);

        Assert.Equal(2, paths.Distinct().Count());
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(2, errors.Reports.Count);
    }

    [Fact]
    public void ResolveLaunchFilePath_CleanupFailureAddsContextToOriginalReport()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        string? temporaryPath = null;
        var originalFailure = new IOException("Simulated write failure");
        var service = new LaunchPlaylistService(temp.RootPath, errors, (stream, _) =>
        {
            temporaryPath = ((FileStream)stream).Name;
            // Linux permits unlinking an open file. Replacing it with a directory makes
            // File.Delete fail deterministically without permission/root dependencies.
            File.Delete(temporaryPath);
            Directory.CreateDirectory(temporaryPath);
            throw originalFailure;
        });

        Assert.Equal("/games/disc-1.chd", service.ResolveLaunchFilePath(CreateMultiDiscItem(), ["Games"], true));

        var report = AssertFailure(errors, "WritePlaylist");
        Assert.Same(originalFailure, report.Exception);
        Assert.Equal(temporaryPath, report.Context!["TemporaryPath"]);
        Assert.True(report.Context.ContainsKey("CleanupFailure"));
    }

    private static ErrorReport AssertFailure(RecordingErrorService errors, string operation)
    {
        var report = Assert.Single(errors.Reports);
        Assert.Equal("Launcher.PlaylistCreationFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal(nameof(LaunchPlaylistService), report.Component);
        Assert.Equal(operation, report.Context!["Operation"]);
        Assert.Equal("3", report.Context["DiscCount"]);
        Assert.Equal("True", report.Context["FallbackToPrimary"]);
        Assert.NotNull(report.Exception);
        Assert.All(errors.Notifications, Assert.Null);
        return report;
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
