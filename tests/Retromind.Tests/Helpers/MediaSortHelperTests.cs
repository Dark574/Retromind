using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class MediaSortHelperTests : IDisposable
{
    public MediaSortHelperTests()
    {
        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(false);
    }

    public void Dispose()
    {
        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(false);
    }

    [Fact]
    public void DisplayOrderComparer_IgnoresLeadingArticleInExplicitSortTitleWhenEnabled()
    {
        var articleItem = new MediaItem
        {
            Id = "article",
            Title = "Unrelated display title",
            SortTitle = "The Alpha"
        };
        var betaItem = new MediaItem { Id = "beta", Title = "Beta" };

        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(true);

        Assert.True(MediaSortHelper.DisplayOrderComparer.Compare(articleItem, betaItem) < 0);
    }

    [Fact]
    public void DisplayOrderComparer_UsesLeadingArticleInExplicitSortTitleWhenDisabled()
    {
        var articleItem = new MediaItem
        {
            Id = "article",
            Title = "Unrelated display title",
            SortTitle = "The Alpha"
        };
        var betaItem = new MediaItem { Id = "beta", Title = "Beta" };

        Assert.True(MediaSortHelper.DisplayOrderComparer.Compare(articleItem, betaItem) > 0);
    }

    [Fact]
    public void DisplayOrderComparer_IgnoresLeadingArticleInFallbackTitleWhenEnabled()
    {
        var articleItem = new MediaItem { Id = "article", Title = "The Alpha" };
        var betaItem = new MediaItem { Id = "beta", Title = "Beta" };

        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(true);

        Assert.True(MediaSortHelper.DisplayOrderComparer.Compare(articleItem, betaItem) < 0);
    }

    [Fact]
    public void DisplayOrderComparer_PreservesNaturalOrderingAfterRemovingArticle()
    {
        var gameTen = new MediaItem { Id = "ten", Title = "Unrelated", SortTitle = "The Game 10" };
        var gameTwo = new MediaItem { Id = "two", Title = "Game 2" };

        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(true);

        Assert.True(MediaSortHelper.DisplayOrderComparer.Compare(gameTwo, gameTen) < 0);
    }
}
