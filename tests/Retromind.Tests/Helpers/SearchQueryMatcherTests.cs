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

    [Theory]
    [InlineData("gog", "store:gog", true)]
    [InlineData("steam", "store:steam", true)]
    [InlineData("epic", "store:epic", true)]
    [InlineData("epic", "store:heroic", true)]
    [InlineData("steam", "store:gog", false)]
    public void StoreFilterUsesNormalizedStoreIdentity(string providerId, string query, bool expected)
    {
        var item = CreateStoreItem(providerId);

        Assert.Equal(expected, SearchQueryMatcher.Create(query).Matches(item));
    }

    [Fact]
    public void StoreFilterRecognizesLegacySteamLaunches()
    {
        var item = new MediaItem
        {
            Files = [new MediaFileRef { Path = "steam", Kind = MediaFileKind.Absolute }],
            LauncherArgs = "steam://rungameid/123"
        };

        Assert.True(SearchQueryMatcher.Create("store:steam").Matches(item));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GogUpdateFilterIncludesMainGameAndDlcUpdates(bool mainGameUpdate, bool dlcUpdate)
    {
        var item = CreateStoreItem("gog");
        item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = mainGameUpdate.ToString();
        item.CustomFields[CustomFieldKeyHelper.StoreDlcUpdateAvailable] = dlcUpdate.ToString();

        Assert.True(SearchQueryMatcher.Create("gogupdate:true").Matches(item));
        Assert.False(SearchQueryMatcher.Create("gogupdate:false").Matches(item));
    }

    [Fact]
    public void GogUpdateFalseMatchesOnlyGogItemsWithoutUpdates()
    {
        var gogItem = CreateStoreItem("gog");
        var steamItem = CreateStoreItem("steam");

        Assert.True(SearchQueryMatcher.Create("gogupdate:false").Matches(gogItem));
        Assert.False(SearchQueryMatcher.Create("gogupdate:false").Matches(steamItem));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void HasGogUpdateMatchesOnlyItemsWithTheVisibleUpdateBadge(
        bool mainGameUpdate,
        bool dlcUpdate,
        bool expected)
    {
        var item = CreateStoreItem("gog");
        item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = mainGameUpdate.ToString();
        item.CustomFields[CustomFieldKeyHelper.StoreDlcUpdateAvailable] = dlcUpdate.ToString();

        Assert.Equal(expected, SearchQueryMatcher.Create("has:gogupdate").Matches(item));
        Assert.Equal(!expected, SearchQueryMatcher.Create("missing:gogupdate").Matches(item));
    }

    [Fact]
    public void HasGogUpdateDoesNotMatchOtherStores()
    {
        var item = CreateStoreItem("steam");
        item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "true";

        Assert.False(SearchQueryMatcher.Create("has:gogupdate").Matches(item));
    }

    [Fact]
    public void FilterBuilderOffersStoreAndGogUpdateFields()
    {
        var data = SearchQueryBuilderHelper.BuildData([]);

        Assert.Contains(data.Fields, field => field.Key == "store");
        Assert.Contains(data.Fields, field => field.Key == "gogupdate");
        Assert.Equal(["epic", "gog", "steam"], data.SuggestionsByField["store"]);
        Assert.Equal(["false", "true"], data.SuggestionsByField["gogupdate"]);
    }

    [Theory]
    [InlineData(AssetType.Cover, "cover")]
    [InlineData(AssetType.Wallpaper, "wallpaper")]
    [InlineData(AssetType.Logo, "logo")]
    [InlineData(AssetType.Video, "video")]
    [InlineData(AssetType.Marquee, "marquee")]
    [InlineData(AssetType.Music, "music")]
    [InlineData(AssetType.Banner, "banner")]
    [InlineData(AssetType.Bezel, "bezel")]
    [InlineData(AssetType.ControlPanel, "controlpanel")]
    [InlineData(AssetType.Manual, "manual")]
    [InlineData(AssetType.Screenshot, "screenshot")]
    public void AssetPresenceFiltersMatchStoredMedia(AssetType assetType, string field)
    {
        var item = new MediaItem();
        item.Assets.Add(new MediaAsset
        {
            Type = assetType,
            RelativePath = $"Library/Games/Test/{field}/asset.bin"
        });

        Assert.True(SearchQueryMatcher.Create($"has:{field}").Matches(item));
        Assert.False(SearchQueryMatcher.Create($"missing:{field}").Matches(item));
    }

    [Fact]
    public void AssetPresenceRequiresANonEmptyStoredPath()
    {
        var item = new MediaItem();
        item.Assets.Add(new MediaAsset { Type = AssetType.Logo, RelativePath = "  " });

        Assert.False(SearchQueryMatcher.Create("has:logo").Matches(item));
        Assert.True(SearchQueryMatcher.Create("missing:logo").Matches(item));
    }

    [Fact]
    public void AssetFiltersSupportAliasesAndBooleanComposition()
    {
        var item = new MediaItem();
        item.Assets.Add(new MediaAsset
        {
            Type = AssetType.Music,
            RelativePath = "Library/Games/Test/music/theme.opus"
        });

        Assert.True(SearchQueryMatcher.Create("has:audio AND missing:documents").Matches(item));
        Assert.True(SearchQueryMatcher.Create("music:theme.opus").Matches(item));
    }

    [Fact]
    public void FilterBuilderOffersAllMediaAssetFields()
    {
        var data = SearchQueryBuilderHelper.BuildData([]);
        var expectedFields = new[]
        {
            "cover", "wallpaper", "logo", "video", "marquee", "music",
            "banner", "bezel", "controlpanel", "manual", "screenshot"
        };

        foreach (var field in expectedFields)
            Assert.Contains(data.Fields, option => option.Key == field);
    }

    private static MediaItem CreateStoreItem(string providerId)
    {
        return new MediaItem
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CustomFieldKeyHelper.StoreProviderId] = providerId,
                [CustomFieldKeyHelper.StoreGameId] = "123"
            }
        };
    }
}
