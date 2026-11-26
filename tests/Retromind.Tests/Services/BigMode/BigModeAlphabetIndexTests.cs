using System.Globalization;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.BigMode;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeAlphabetIndexTests
{
    [Theory]
    [InlineData("Ärger", "A")]
    [InlineData("öde", "O")]
    [InlineData("Über", "U")]
    [InlineData("éclair", "E")]
    [InlineData("e\u0301clair", "E")]
    [InlineData("çava", "C")]
    [InlineData("ßeta", "S")]
    [InlineData("Æon", "A")]
    [InlineData("Øresund", "O")]
    [InlineData("Łódź", "L")]
    [InlineData(" 42 Games", "0–9")]
    [InlineData("٣ Games", "0–9")]
    [InlineData("１２ Games", "0–9")]
    [InlineData("Ⅳ Games", "0–9")]
    [InlineData("!Alarm", "#")]
    [InlineData("🎮 Games", "#")]
    [InlineData("", "#")]
    [InlineData(" \t ", "#")]
    [InlineData("Αθήνα", "Α–Ω")]
    [InlineData("Москва", "А–Я")]
    [InlineData("東京", "漢字")]
    [InlineData("\U00020000", "漢字")]
    [InlineData("ゼルダ", "かな")]
    [InlineData("한글", "한글")]
    [InlineData("العربية", "ع")]
    [InlineData("עברית", "א")]
    [InlineData("अक्षर", "अ")]
    [InlineData("ภาษาไทย", "…")]
    public void InternationalKeys_AlwaysHaveAnAvailableGroup(string title, string expected)
    {
        var session = new BigModeNavigationSession([new MediaNode(title)]);
        var index = new BigModeAlphabetIndex(session.Current.Entries);
        var group = Assert.Single(index.Groups, group => group.IsAvailable);
        Assert.Equal(expected, group.Label);
        Assert.Equal(0, group.TargetIndex);
        Assert.Equal(group.Label, index.Groups[BigModeAlphabetIndex.GetGroupIndex(title)].Label);
    }

    [Fact]
    public void EffectiveSortTitle_ReusesArticleRuleAndCultureWithoutReordering()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousArticles = MediaSortHelper.IgnoreLeadingArticlesInTitleSort;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var node = new MediaNode("Root")
            {
                Items = [new MediaItem("Zulu") { SortTitle = "The Äther" }, new MediaItem("Der Überfall")]
            };
            var session = new BigModeNavigationSession([node]);
            session.ActivateSelected();
            MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(false);
            var before = new BigModeAlphabetIndex(session.Current.Entries);
            Assert.Equal(0, before.Groups.Single(g => g.Label == "T").TargetIndex);
            Assert.Equal(1, before.Groups.Single(g => g.Label == "D").TargetIndex);
            MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(true);
            var after = new BigModeAlphabetIndex(session.Current.Entries);
            Assert.Equal(0, after.Groups.Single(g => g.Label == "A").TargetIndex);
            Assert.Equal(1, after.Groups.Single(g => g.Label == "U").TargetIndex);
            Assert.Equal(["Zulu", "Der Überfall"], session.Current.Entries.Select(e => e.Title));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(previousArticles);
        }
    }

    [Fact]
    public void MixedUnsortedFrame_TargetsFirstOccurrenceInVisibleOrder()
    {
        var root = new MediaNode("Root")
        {
            Children = [new MediaNode("Zulu"), new MediaNode("Alpha category")],
            Items = [new MediaItem("Zebra"), new MediaItem("Alpha media"), new MediaItem("Avocado") { IsProtected = true }]
        };
        var session = new BigModeNavigationSession([root], parentalFilterActive: true);
        session.ActivateSelected();
        var index = new BigModeAlphabetIndex(session.Current.Entries);
        Assert.Equal(0, index.Groups.Single(g => g.Label == "Z").TargetIndex);
        Assert.Equal(1, index.Groups.Single(g => g.Label == "A").TargetIndex);
        Assert.Equal(4, session.Current.Entries.Count);
        Assert.Equal(2, index.Groups.Count(g => g.IsAvailable));
    }

    [Fact]
    public void GridMovement_SkipsEmptyRowsAndGroupsAndReachesEveryTarget()
    {
        var session = new BigModeNavigationSession([new("Alpha"), new("Gamma"), new("Zulu"), new("東京")]);
        var index = new BigModeAlphabetIndex(session.Current.Entries);
        var a = BigModeAlphabetIndex.GetGroupIndex("A");
        var g = BigModeAlphabetIndex.GetGroupIndex("G");
        var z = BigModeAlphabetIndex.GetGroupIndex("Z");
        var han = BigModeAlphabetIndex.GetGroupIndex("東京");
        Assert.Equal(g, index.Move(a, 1, 0));
        Assert.Equal(han, index.Move(a, -1, 0));
        Assert.Equal(g, index.Move(a, 0, 1));
        Assert.Equal(z, index.Move(g, 0, 1));
        var visited = new HashSet<int>();
        var current = a;
        do { visited.Add(current); current = index.Move(current, 1, 0); } while (current != a);
        Assert.Equal(4, visited.Count);
        Assert.All(visited, i => Assert.True(index.Groups[i].IsAvailable));
    }

    [Fact]
    public void EmptyAndSingleEntryGrids_AreStable()
    {
        var empty = new BigModeAlphabetIndex([]);
        Assert.Equal(-1, empty.FirstAvailable);
        Assert.Equal(-1, empty.Move(-1, 1, 0));
        var session = new BigModeNavigationSession([new("Alpha")]);
        var single = new BigModeAlphabetIndex(session.Current.Entries);
        foreach (var (x, y) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            Assert.Equal(single.FirstAvailable, single.Move(single.FirstAvailable, x, y));
    }
}
