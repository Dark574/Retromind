using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Search;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Search;

public sealed class LibrarySearchStoreTests
{
    public static IEnumerable<object?[]> ProviderCases()
    {
        yield return [" GOG ", "steam", MediaFileKind.Absolute, "heroic://launch", "gog"];
        yield return ["STEAM", "heroic", MediaFileKind.Absolute, null, "steam"];
        yield return ["Heroic", "steam", MediaFileKind.Absolute, null, "epic"];
        yield return ["epic", null, MediaFileKind.Absolute, null, "epic"];
        yield return ["unknown", "steam", MediaFileKind.Absolute, null, "steam"];
        yield return ["", "heroic", MediaFileKind.Absolute, null, "epic"];
        yield return [null, "steam", MediaFileKind.Absolute, null, "steam"];
        yield return [null, " '/usr/bin/STEAM' ", MediaFileKind.Absolute, null, "steam"];
        yield return [null, "\"/opt/HEROIC\"", MediaFileKind.Absolute, null, "epic"];
        yield return [null, "steam.exe", MediaFileKind.Absolute, null, null];
        yield return [null, "Library/Tools/steam", MediaFileKind.LibraryRelative, null, "steam"];
        yield return [null, "Library/Ä Ö/heroic", MediaFileKind.LibraryRelative, null, "epic"];
        yield return [null, "Library/../steam", MediaFileKind.LibraryRelative, null, "steam"];
        yield return [null, "../steam", MediaFileKind.LibraryRelative, null, null];
        yield return [null, "../steam", MediaFileKind.Absolute, null, "steam"];
        yield return [null, "Library/game.rom", MediaFileKind.LibraryRelative, null, null];
        yield return [null, "Library/game.rom", MediaFileKind.LibraryRelative, "  STEAM://run/123", "steam"];
        yield return [null, null, MediaFileKind.Absolute, "epic://launch/123", "epic"];
        yield return [null, "", MediaFileKind.Absolute, "heroic://launch/123", "epic"];
        yield return [null, "heroic", MediaFileKind.Absolute, "steam://run/123", "steam"];
        yield return [null, "steam", MediaFileKind.Absolute, "epic://launch/123", "steam"];
        yield return [null, null, MediaFileKind.Absolute, null, null];
        yield return [null, "   ", MediaFileKind.LibraryRelative, null, null];
        yield return [null, "bad\0/steam", MediaFileKind.LibraryRelative, null, null];
        yield return [null, "bad\0/steam", MediaFileKind.Absolute, null, "steam"];
        yield return [null, "steam", (MediaFileKind)99, null, "steam"];
    }

    [Theory]
    [MemberData(nameof(ProviderCases))]
    public async Task StoreQueriesPreserveLiveRecognition(string? storedProvider, string? path, MediaFileKind kind,
        string? arguments, string? expected)
    {
        using var directory = new TemporaryDirectory();
        using var environment = DataRoot(directory.RootPath);
        var item = new MediaItem("Game") { LauncherArgs = arguments };
        if (storedProvider != null) item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = storedProvider;
        if (path != null) item.Files.Add(new() { Path = path, Kind = kind });
        item.Assets.Add(new() { Type = AssetType.Video, RelativePath = "video.mp4" });
        var root = new MediaNode("Games"); root.Items.Add(item);
        Assert.Equal(expected, StoreProviderBadgeHelper.GetProviderId(item));
        foreach (var text in new[] { "store:gog", "provider:steam", "store:heroic", "has:store", "missing:store",
                     "NOT store:gog", "store:steam OR store:epic", "(store:steam OR store:heroic) AND has:video" })
        {
            var query = new LibrarySearchQuery(text);
            var expectedMatch = query.Matcher.Matches(item);
            var snapshot = LibrarySearchSnapshot.Capture([root], query: query);
            var hits = await LibrarySearchEvaluator.EvaluateAsync(snapshot, query);
            Assert.Equal(expectedMatch ? 1 : 0, hits.Count);
            Assert.Equal(expected, snapshot.Entries[0].Document.StoreProviderId);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PrimaryFileOrderingMatchesTheExistingLaunchSelection(int scenario)
    {
        var files = scenario switch
        {
            0 => new[] { File("heroic", null), File("steam", 1), File("heroic", 2) },
            1 => new[] { File("heroic", 9), File("steam", 3), File("heroic", 5) },
            2 => new[] { File("steam", 2), File("heroic", 2) },
            3 => new[] { File("steam", 0), File("heroic", -1), File("heroic", null) },
            _ => new[] { File(" ", 1), File("steam", 2) }
        };
        var item = new MediaItem("Game") { Files = [.. files] };
        var root = new MediaNode("Games"); root.Items.Add(item);
        var expected = StoreProviderBadgeHelper.GetProviderId(item);
        var snapshot = LibrarySearchSnapshot.Capture([root]);
        foreach (var file in files) { file.Path = "changed"; file.Kind = MediaFileKind.LibraryRelative; file.Index = 99; }
        item.Files.Clear();
        Assert.Equal(expected, snapshot.Entries[0].Document.StoreProviderId);
    }

    [Fact]
    public async Task FrozenInputsSurviveRootWorkingDirectoryAndModelChanges()
    {
        using var original = new TemporaryDirectory();
        using var next = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", "portable/Retromind.AppImage"));
        var workingDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = original.RootPath;
            var item = new MediaItem("Game")
            {
                Files = [new() { Kind = MediaFileKind.LibraryRelative, Path = "../portable/steam", Index = 1 }]
            };
            var root = new MediaNode("Games"); root.Items.Add(item);
            var query = new LibrarySearchQuery("store:steam");
            Assert.True(query.Matcher.Matches(item));
            var snapshot = LibrarySearchSnapshot.Capture([root], query: query);
            Assert.Equal(Path.Combine(original.RootPath, "portable"), snapshot.Entries[0].Document.StoreDataRoot);

            Environment.SetEnvironmentVariable("APPIMAGE", Path.Combine(next.RootPath, "Retromind.AppImage"));
            Environment.CurrentDirectory = next.RootPath;
            item.Files[0].Path = "heroic"; item.Files.Clear();
            item.LauncherArgs = "epic://launch/123";
            item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
            Assert.False(query.Matcher.Matches(item));
            Assert.Same(item, Assert.Single(await LibrarySearchEvaluator.EvaluateAsync(snapshot, query)).Item);
        }
        finally { Environment.CurrentDirectory = workingDirectory; }
    }

    [Fact]
    public void PortablePathsRetainContainmentAndLinuxCaseRules()
    {
        using var directory = new TemporaryDirectory();
        using var environment = DataRoot(directory.RootPath);
        foreach (var path in new[] { Path.Combine(directory.RootPath, "steam"), directory.RootPath + "-sibling/steam",
                     Path.Combine(directory.RootPath.ToUpperInvariant(), "steam"), "/opt/steam" })
        {
            var item = new MediaItem("Game") { Files = [new() { Kind = MediaFileKind.LibraryRelative, Path = path }] };
            var root = new MediaNode("Games"); root.Items.Add(item);
            Assert.Equal(StoreProviderBadgeHelper.GetProviderId(item),
                LibrarySearchSnapshot.Capture([root]).Entries[0].Document.StoreProviderId);
        }
    }

    [Fact]
    public void MissingFileCollectionRetainsLauncherArgumentFallback()
    {
        var item = new MediaItem("Game") { Files = null!, LauncherArgs = "steam://run/123" };
        var root = new MediaNode("Games"); root.Items.Add(item);
        Assert.Equal(StoreProviderBadgeHelper.GetProviderId(item),
            LibrarySearchSnapshot.Capture([root]).Entries[0].Document.StoreProviderId);
    }

    [Fact]
    public void StoreEvaluationRemainsCancelableBetweenItems()
    {
        var root = new MediaNode("Games");
        for (var i = 0; i < 1000; i++)
            root.Items.Add(new MediaItem($"Game {i}")
            {
                Files = [new() { Path = "Library/game.rom", Kind = MediaFileKind.LibraryRelative, Index = 1 }]
            });
        var query = new LibrarySearchQuery("missing:store");
        var snapshot = LibrarySearchSnapshot.Capture([root], query: query);
        using var cts = new CancellationTokenSource();
        var matches = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => LibrarySearchEvaluator.Evaluate(snapshot, query, cts.Token,
            _ => { if (++matches == 100) cts.Cancel(); }));
        Assert.Equal(100, matches);
    }

    private static MediaFileRef File(string path, int? index) => new() { Path = path, Index = index };
    private static EnvironmentVariableScope DataRoot(string path) => new(("APPIMAGE", Path.Combine(path, "Retromind.AppImage")));
}
