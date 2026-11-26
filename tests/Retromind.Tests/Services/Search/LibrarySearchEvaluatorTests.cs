using System.Globalization;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.BigMode;
using Retromind.Services.Search;

namespace Retromind.Tests.Services.Search;

public sealed class LibrarySearchEvaluatorTests
{
    public static IEnumerable<object[]> Queries()
    {
        foreach (var query in new[]
        {
            "", "   ", "Game", "grusse", "title:Game", "sorttitle:1", "description:space",
            "developer:studio", "publisher:house", "platform:pc", "source:import", "genre:rpg", "series:saga",
            "releasetype:remake", "playmode:coop", "maxplayers:4", "status:completed", "year:1999",
            "date:1999", "tag:space", "custom:edition", "customkey:edition", "customvalue:gold",
            "custom.edition:gold", "id:item-1", "fav:true", "played:true", "started:false", "store:gog",
            "store:heroic", "gogupdate:true", "rating:>=4",
            "video:intro", "has:audio OR has:documents", "NOT fav:true",
            "(title:Game OR tag:space) AND NOT year:2001", "title:\"Game 2\"", "Game -tag:space", "(", "\""
        }) yield return [query];
        foreach (var field in new[] { "title", "sort", "desc", "dev", "pub", "platform", "source", "genre", "series",
                     "release", "mode", "players", "status", "year", "date", "tag", "custom", "id", "fav", "played",
                     "store", "gogupdate", "rating", "video", "music", "logo", "cover", "wallpaper", "marquee",
                     "banner", "bezel", "controlpanel", "manual", "screenshot" })
        {
            yield return [$"has:{field}"];
            yield return [$"missing:{field}"];
        }
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void FrozenEvaluationMatchesLegacyDesktopComposition(string text)
    {
        var roots = CreateLibrary();
        var snapshot = LibrarySearchSnapshot.Capture(roots);
        foreach (var (year, favorites, status) in new (string?, bool, PlayStatus?)[]
        {
            (null, false, null), ("1999", false, null), ("invalid", false, null),
            (null, true, null), (null, false, PlayStatus.Completed), ("1999", true, PlayStatus.Completed)
        })
        {
            var expected = LegacyEvaluate(roots, text, year, favorites, status);
            var query = new LibrarySearchQuery(text, year, favorites, status);
            var actual = LibrarySearchEvaluator.Evaluate(snapshot, query);
            Assert.Equal(expected.Select(i => i.Id), actual.Select(i => i.ItemId));
            Assert.Equal(expected.Select(i => i.Id), LibrarySearchEvaluator.Evaluate(
                LibrarySearchSnapshot.Capture(roots, query: query), query).Select(i => i.ItemId));
            foreach (var entry in snapshot.Entries)
                Assert.Equal(SearchQueryMatcher.Create(text).Matches(entry.Item), SearchQueryMatcher.Create(text).Matches(entry.Document));
        }
    }

    [Fact]
    public void QuerySpecificCaptureOmitsUnusedCollectionsAndRejectsReuseForAnotherQuery()
    {
        var query = new LibrarySearchQuery("Game");
        var snapshot = LibrarySearchSnapshot.Capture(CreateLibrary(), query: query);
        Assert.All(snapshot.Entries, entry =>
        {
            Assert.Equal(0, entry.Document.TagCount); Assert.Empty(entry.Document.CustomFields); Assert.Equal(0, entry.Document.AssetCount);
            Assert.Null(entry.Document.StoreProviderId);
        });
        Assert.Throws<InvalidOperationException>(() => LibrarySearchEvaluator.Evaluate(snapshot, new("tag:space")));
    }

    [Fact]
    public void ScopesOwnersTypesAndDuplicateTitlesRetainStableIdentity()
    {
        var roots = CreateLibrary();
        var child = roots[0].Children[0];
        var snapshot = LibrarySearchSnapshot.Capture(roots, new HashSet<string> { child.Id });
        var hits = LibrarySearchEvaluator.Evaluate(snapshot, new("Game"));
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => { Assert.Same(child, hit.Owner); Assert.Equal(child.Id, hit.OwnerId); });
        Assert.Equal(2, hits.Select(hit => hit.ItemId).Distinct().Count());
        Assert.Equal(Enum.GetValues<MediaType>().Order(),
            LibrarySearchEvaluator.Evaluate(LibrarySearchSnapshot.Capture(roots), new("Game")).Select(h => h.MediaType).Distinct().Order());
        Assert.Empty(LibrarySearchEvaluator.Evaluate(LibrarySearchSnapshot.Capture(roots, new HashSet<string>()), new("Game")));
        Assert.Equal(3, LibrarySearchEvaluator.Evaluate(LibrarySearchSnapshot.Capture(roots, new HashSet<string> { roots[0].Id, child.Id }), new("Game")).Count);
    }

    [Fact]
    public void ProtectionAndConsumerVisibilityPolicyAreAppliedDuringCapture()
    {
        var roots = CreateLibrary();
        roots[0].Items[0].IsProtected = true;
        var child = roots[0].Children[0];
        var all = LibrarySearchSnapshot.Capture(roots, parentalFilterActive: true);
        Assert.DoesNotContain(all.Entries, e => e.Item == roots[0].Items[0]);
        var visible = LibrarySearchSnapshot.Capture(roots, isNodeVisible: n => n != child);
        Assert.DoesNotContain(visible.Entries, e => e.Owner == child);
        Assert.Contains(all.Entries, e => e.Owner == child);
        foreach (var item in child.Items) item.IsProtected = true;
        var policy = new BigModeVisibilityPolicy(true);
        var protectedSnapshot = LibrarySearchSnapshot.Capture(roots, parentalFilterActive: true, isNodeVisible: policy.IsNodeVisible);
        Assert.DoesNotContain(protectedSnapshot.Nodes, n => n.Id == child.Id);
        Assert.DoesNotContain(protectedSnapshot.Entries, e => e.Owner == child);
    }

    [Fact]
    public void DeduplicationKeepsTheFirstOwnerButMatchCallbacksRetainAllScopeCounts()
    {
        var roots = CreateLibrary();
        roots[1].Items.Add(new MediaItem("Duplicate") { Id = "item-1" });
        roots[1].Items.Add(new MediaItem("No ID") { Id = "" });
        roots[1].Items.Add(new MediaItem("No ID") { Id = "" });
        var matched = new List<LibrarySearchHit>();
        var result = LibrarySearchEvaluator.Evaluate(LibrarySearchSnapshot.Capture(roots), new("NOT id:absent"), onMatch: matched.Add);
        Assert.Equal(7, matched.Count);
        Assert.Equal(6, result.Count);
        Assert.Same(roots[0], result.Single(h => h.ItemId == "item-1").Owner);
        Assert.Equal(2, result.Count(h => h.ItemId == ""));
    }

    [Fact]
    public void FrozenValuesSurviveEditsMovesRemovalAndCultureChanges()
    {
        var roots = CreateLibrary();
        var snapshot = LibrarySearchSnapshot.Capture(roots);
        var expected = LibrarySearchEvaluator.Evaluate(snapshot, new("tag:space AND has:video"));
        var item = roots[0].Items[0];
        item.Title = "Changed"; item.Id = "changed"; item.SortTitle = "Changed";
        item.Tags.Clear(); item.CustomFields.Clear(); item.Assets[0].RelativePath = ""; item.Assets.Clear();
        roots[0].Items.Clear();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var actual = LibrarySearchEvaluator.Evaluate(snapshot, new("tag:space AND has:video"));
            Assert.Equal(expected.Select(h => h.ItemId), actual.Select(h => h.ItemId));
            Assert.Equal("tr-TR", CultureInfo.CurrentCulture.Name);
            Assert.Equal("Game 2", Assert.Single(actual).Document.Title);
            Assert.Same(roots[0], Assert.Single(actual).Owner);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task IndependentConcurrentEvaluationsAndCancellationDoNotShareState()
    {
        var snapshot = LibrarySearchSnapshot.Capture(CreateLibrary());
        using var cts = new CancellationTokenSource();
        var canceled = LibrarySearchEvaluator.EvaluateAsync(snapshot, new("Game"), cts.Token, _ => cts.Cancel());
        var surviving = LibrarySearchEvaluator.EvaluateAsync(snapshot, new("fav:true"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Single(await surviving);
        Assert.Equal(3, LibrarySearchEvaluator.Evaluate(snapshot, new("Game")).Count);
        using var preCanceled = new CancellationTokenSource(); preCanceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LibrarySearchSnapshot.Capture(CreateLibrary(), token: preCanceled.Token));
    }

    [Fact]
    public void HitCanBeRevealedByTheExistingBigModeSession()
    {
        var roots = CreateLibrary();
        var hit = LibrarySearchEvaluator.Evaluate(LibrarySearchSnapshot.Capture(roots), new("id:item-3")).Single();
        var navigation = new BigModeNavigationSession(roots);
        Assert.True(navigation.TryReveal(new(BigModeNavigationEntryKind.MediaItem, hit.ItemId)));
        Assert.Same(hit.Owner, navigation.Current.OpenedNode);
    }

    internal static List<MediaNode> CreateLibrary()
    {
        var root = new MediaNode("All") { Id = "root" };
        var child = new MediaNode("Child") { Id = "child" };
        root.Children.Add(child);
        var item = new MediaItem("Game 2")
        {
            Id = "item-1", SortTitle = "1", Description = "Space", Developer = "Studio", Publisher = "House",
            Platform = "PC", Source = "Import", Genre = "RPG", Series = "Saga", ReleaseType = "Remake", PlayMode = "Coop",
            MaxPlayers = "4", ReleaseDate = new DateTime(1999, 1, 2), Rating = 4.5, Status = PlayStatus.Completed,
            IsFavorite = true, PlayCount = 1, MediaType = MediaType.Native,
            CustomFields = new() { ["edition"] = "gold", [CustomFieldKeyHelper.StoreProviderId] = "gog", [CustomFieldKeyHelper.StoreUpdateAvailable] = "true" }
        };
        item.Tags.Add("Space"); item.Assets.Add(new MediaAsset { Type = AssetType.Video, RelativePath = "intro.mp4" });
        root.Items.Add(item);
        child.Items.Add(new MediaItem("Game 10") { Id = "item-2", MediaType = MediaType.Emulator, ReleaseDate = new DateTime(2001, 1, 1) });
        child.Items.Add(new MediaItem("Game 10") { Id = "item-3", MediaType = MediaType.Command });
        var other = new MediaNode("Other") { Id = "other" };
        other.Items.Add(new MediaItem("Grüße") { Id = "item-4" });
        return [root, other];
    }

    // Reference composition from SearchAreaViewModel before extraction: live fields, ID dedup and display comparer.
    internal static List<MediaItem> LegacyEvaluate(IEnumerable<MediaNode> roots, string? text, string? year = null,
        bool favorites = false, PlayStatus? status = null)
    {
        var matcher = SearchQueryMatcher.Create(text?.Trim());
        int? parsedYear = int.TryParse(year?.Trim(), out var parsed) ? parsed : null;
        if (string.IsNullOrWhiteSpace(text) && parsedYear == null && !favorites && status == null) return [];
        var result = new List<MediaItem>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(MediaNode node)
        {
            foreach (var item in node.Items)
                if ((!favorites || item.IsFavorite) && (!status.HasValue || item.Status == status) &&
                    (!matcher.IsActive || matcher.Matches(item)) && (!parsedYear.HasValue || item.ReleaseDate?.Year == parsedYear) &&
                    (string.IsNullOrWhiteSpace(item.Id) || seen.Add(item.Id))) result.Add(item);
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in roots) Visit(root);
        result.Sort(MediaSortHelper.DisplayOrderComparer);
        return result;
    }
}
