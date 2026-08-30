using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class LibraryStatisticsViewModelTests
{
    [Fact]
    public void ActiveParentalFilter_IncludesProtectedItemsButDisablesTheirNavigation()
    {
        var protectedItem = new MediaItem
        {
            Title = "Protected game",
            IsProtected = true,
            TotalPlayTime = TimeSpan.FromHours(2),
            PlayCount = 3,
            LastPlayed = new DateTime(2026, 8, 29, 20, 0, 0)
        };
        var visibleItem = new MediaItem
        {
            Title = "Visible game",
            TotalPlayTime = TimeSpan.FromHours(1),
            PlayCount = 1,
            LastPlayed = new DateTime(2026, 8, 28, 20, 0, 0)
        };
        var hiddenNode = new MediaNode
        {
            Name = "Protected category",
            IsVisibleInTree = false,
            Items = [protectedItem, visibleItem]
        };

        var viewModel = new LibraryStatisticsViewModel(
            [hiddenNode],
            isParentalFilterActive: true);

        Assert.Equal(2, viewModel.TotalItems);
        Assert.Equal(TimeSpan.FromHours(3), viewModel.TotalPlayTime);
        Assert.Contains(viewModel.ScopeOptions, option => ReferenceEquals(option.Node, hiddenNode));

        var protectedRanking = Assert.Single(
            viewModel.MostPlayedItems,
            ranking => ReferenceEquals(ranking.Item, protectedItem));
        var visibleRanking = Assert.Single(
            viewModel.MostPlayedItems,
            ranking => ReferenceEquals(ranking.Item, visibleItem));

        Assert.False(protectedRanking.CanNavigate);
        Assert.False(viewModel.OpenItemCommand.CanExecute(protectedRanking));
        Assert.True(visibleRanking.CanNavigate);
        Assert.True(viewModel.OpenItemCommand.CanExecute(visibleRanking));
    }

    [Fact]
    public void InactiveParentalFilter_AllowsProtectedItemNavigation()
    {
        var protectedItem = new MediaItem
        {
            Title = "Protected game",
            IsProtected = true,
            TotalPlayTime = TimeSpan.FromMinutes(10)
        };
        var node = new MediaNode { Name = "Games", Items = [protectedItem] };
        var viewModel = new LibraryStatisticsViewModel(
            [node],
            isParentalFilterActive: false);
        var ranking = Assert.Single(viewModel.MostPlayedItems);

        Assert.True(ranking.CanNavigate);
        Assert.True(viewModel.OpenItemCommand.CanExecute(ranking));
    }
}
