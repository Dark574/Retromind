using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeHomeBuilderTests
{
    [Fact]
    public void Build_CreatesOrderedRowsAndKeepsSourceNode()
    {
        var olderFavorite = new MediaItem
        {
            Title = "Older favorite",
            IsFavorite = true,
            LastPlayed = new DateTime(2026, 9, 20, 12, 0, 0)
        };
        var newer = new MediaItem
        {
            Title = "Newer",
            LastPlayed = new DateTime(2026, 9, 25, 12, 0, 0)
        };
        var games = new MediaNode { Name = "Games", Items = [olderFavorite, newer] };

        var sections = BigModeHomeBuilder.Build(
            [games],
            parentalFilterActive: false,
            "Recent",
            "Favorites",
            "Library",
            "Games");

        Assert.Equal(["Recent", "Favorites", "Library"], sections.Select(section => section.Title));
        Assert.Same(newer, sections[0].Entries[0].Item);
        Assert.Same(games, sections[0].Entries[0].SourceNode);
        Assert.Same(olderFavorite, Assert.Single(sections[1].Entries).Item);
        Assert.Same(games, Assert.Single(sections[2].Entries).Node);
    }

    [Fact]
    public void Build_WithParentalFilter_OmitsProtectedItemsAndProtectedOnlyRoots()
    {
        var hiddenRoot = new MediaNode
        {
            Name = "Hidden",
            Items =
            [
                new MediaItem
                {
                    Title = "Protected",
                    IsProtected = true,
                    IsFavorite = true,
                    LastPlayed = new DateTime(2026, 9, 25)
                }
            ]
        };
        var visibleItem = new MediaItem
        {
            Title = "Visible",
            IsFavorite = true,
            LastPlayed = new DateTime(2026, 9, 24)
        };
        var visibleRoot = new MediaNode { Name = "Visible root", Items = [visibleItem] };

        var sections = BigModeHomeBuilder.Build(
            [hiddenRoot, visibleRoot],
            parentalFilterActive: true,
            "Recent",
            "Favorites",
            "Library",
            "Games");

        Assert.All(
            sections.SelectMany(section => section.Entries),
            entry => Assert.NotEqual("Protected", entry.Title));
        Assert.Same(visibleItem, Assert.Single(sections[0].Entries).Item);
        Assert.Same(
            visibleRoot,
            Assert.Single(sections[^1].Entries, entry => entry.Node != null).Node);
    }

    [Fact]
    public void Build_LimitsItemRowsButNotRootLibraryRow()
    {
        var root = new MediaNode { Name = "Games" };
        for (var index = 0; index < 5; index++)
        {
            root.Items.Add(new MediaItem
            {
                Title = $"Game {index}",
                IsFavorite = true,
                LastPlayed = new DateTime(2026, 9, 20).AddDays(index)
            });
        }

        var sections = BigModeHomeBuilder.Build(
            [root],
            parentalFilterActive: false,
            "Recent",
            "Favorites",
            "Library",
            "Games",
            itemLimit: 2);

        Assert.Equal(2, sections[0].Entries.Count);
        Assert.Equal(2, sections[1].Entries.Count);
        Assert.Single(sections[2].Entries);
    }

    [Fact]
    public void Build_KeepsASoleStructuralRootAsTheLibraryNavigationStart()
    {
        var games = new MediaNode
        {
            Name = "Games",
            Items = [new MediaItem { Title = "Game" }]
        };
        var movies = new MediaNode
        {
            Name = "Movies",
            Items = [new MediaItem { Title = "Movie" }]
        };
        var libraryRoot = new MediaNode
        {
            Name = "Library",
            Children = [games, movies]
        };

        var sections = BigModeHomeBuilder.Build(
            [libraryRoot],
            parentalFilterActive: false,
            "Recent",
            "Favorites",
            "Library",
            "Items");

        var librarySection = Assert.Single(sections);
        Assert.Equal(
            [libraryRoot, games, movies],
            librarySection.Entries.Select(entry => entry.Node));
        Assert.Equal(BigModeHomeEntryKind.LibraryCurrent, librarySection.Entries[0].Kind);
        Assert.All(
            librarySection.Entries.Skip(1),
            entry => Assert.Equal(BigModeHomeEntryKind.LibraryChild, entry.Kind));
        Assert.Equal("Library", librarySection.Breadcrumb);
    }

    [Fact]
    public void LibraryNavigator_DrillsIntoBranchesAndReturnsToTheParent()
    {
        var playStation = new MediaNode
        {
            Name = "PlayStation",
            Items = [new MediaItem { Title = "Game" }]
        };
        var games = new MediaNode
        {
            Name = "Games",
            Children = [playStation]
        };
        var library = new MediaNode
        {
            Name = "Library",
            Children = [games]
        };
        var navigator = new BigModeHomeLibraryNavigator(
            [library],
            parentalFilterActive: false,
            "Library",
            "Games",
            "Back",
            "Open",
            "Overview");

        Assert.Same(library, navigator.CurrentNode);
        Assert.False(navigator.CanGoBack);

        Assert.True(navigator.DrillInto(games));
        Assert.Same(games, navigator.CurrentNode);
        Assert.True(navigator.CanGoBack);
        Assert.Equal("Library  ›  Games", navigator.Breadcrumb);

        var entries = navigator.BuildEntries();
        Assert.Equal(
            [BigModeHomeEntryKind.LibraryCurrent, BigModeHomeEntryKind.LibraryChild, BigModeHomeEntryKind.LibraryBack],
            entries.Select(entry => entry.Kind));
        Assert.False(navigator.DrillInto(playStation));

        Assert.True(navigator.GoBack());
        Assert.Same(library, navigator.CurrentNode);
        Assert.False(navigator.CanGoBack);
    }

    [Fact]
    public void LibraryNavigator_UsesAVirtualOverviewForMultipleRoots()
    {
        var games = new MediaNode { Name = "Games" };
        var movies = new MediaNode { Name = "Movies" };
        var navigator = new BigModeHomeLibraryNavigator(
            [games, movies],
            parentalFilterActive: false,
            "Library",
            "Items",
            "Back",
            "Open",
            "Overview");

        Assert.Null(navigator.CurrentNode);
        Assert.False(navigator.CanGoBack);
        Assert.Equal(
            [null, games, movies],
            navigator.BuildEntries().Select(entry => entry.Node));
    }
}
