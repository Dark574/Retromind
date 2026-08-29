using System;
using Retromind.Helpers;
using Retromind.Models;
using Xunit;

namespace Retromind.Tests.Helpers;

public sealed class SearchQueryMatcherTests
{
    [Theory]
    [InlineData(0, 0, false, false)]
    [InlineData(1, 0, false, true)]
    [InlineData(0, 30, false, true)]
    [InlineData(0, 0, true, true)]
    public void PlayedFilterUsesTheSameEvidenceAsLibraryStatistics(
        int playCount,
        int playTimeSeconds,
        bool hasLastPlayed,
        bool expectedPlayed)
    {
        var item = new MediaItem
        {
            PlayCount = playCount,
            TotalPlayTime = TimeSpan.FromSeconds(playTimeSeconds),
            LastPlayed = hasLastPlayed ? new DateTime(2026, 8, 29) : null
        };

        Assert.Equal(expectedPlayed, SearchQueryMatcher.Create("played:true").Matches(item));
        Assert.Equal(!expectedPlayed, SearchQueryMatcher.Create("played:false").Matches(item));
    }

    [Fact]
    public void StatisticsStatusFiltersMatchTheirSummaryDefinitions()
    {
        var incompleteNotPlayed = new MediaItem { Status = PlayStatus.Incomplete };
        var incompletePlayed = new MediaItem { Status = PlayStatus.Incomplete, PlayCount = 1 };
        var completed = new MediaItem { Status = PlayStatus.Completed };
        var abandoned = new MediaItem { Status = PlayStatus.Abandoned };

        var completedQuery = SearchQueryMatcher.Create("status:completed");
        Assert.True(completedQuery.Matches(completed));
        Assert.False(completedQuery.Matches(incompletePlayed));

        var abandonedQuery = SearchQueryMatcher.Create("status:abandoned");
        Assert.True(abandonedQuery.Matches(abandoned));
        Assert.False(abandonedQuery.Matches(incompleteNotPlayed));

        var inProgressQuery = SearchQueryMatcher.Create("status:incomplete played:true");
        Assert.True(inProgressQuery.Matches(incompletePlayed));
        Assert.False(inProgressQuery.Matches(incompleteNotPlayed));
        Assert.False(inProgressQuery.Matches(completed));

        var neverStartedQuery = SearchQueryMatcher.Create("status:incomplete played:false");
        Assert.True(neverStartedQuery.Matches(incompleteNotPlayed));
        Assert.False(neverStartedQuery.Matches(incompletePlayed));
        Assert.False(neverStartedQuery.Matches(completed));
    }
}
