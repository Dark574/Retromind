using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class StoreGameIdentityHelperTests
{
    [Fact]
    public void IsSameGame_MatchesProviderAndStoreGameIdInsteadOfTitle()
    {
        var existing = CreateStoreItem("steam", "123", "Old title");

        Assert.True(StoreGameIdentityHelper.IsSameGame(
            existing,
            CreateStoreItem("STEAM", "123", "Renamed title")));
        Assert.False(StoreGameIdentityHelper.IsSameGame(
            existing,
            CreateStoreItem("steam", "456", "Old title")));
        Assert.False(StoreGameIdentityHelper.IsSameGame(
            existing,
            CreateStoreItem("epic", "123", "Old title")));
    }

    [Theory]
    [InlineData("steam", "steam://rungameid/123", "123")]
    [InlineData("heroic", "epic://test-id", "test-id")]
    public void IsSameGame_RecognizesLegacyImportedLaunchArguments(
        string executable,
        string launchArguments,
        string gameId)
    {
        var legacyItem = new MediaItem("Legacy import")
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = executable,
                    Index = 1
                }
            ],
            LauncherArgs = launchArguments
        };
        var providerId = executable == "steam" ? "steam" : "epic";
        var importedItem = CreateStoreItem(providerId, gameId, "Current import");

        Assert.True(StoreGameIdentityHelper.IsSameGame(legacyItem, importedItem));
    }

    [Fact]
    public void IsSameGame_DoesNotTreatManualItemWithSameTitleAsStoreDuplicate()
    {
        var manualItem = new MediaItem("Same title");
        var importedItem = CreateStoreItem("steam", "123", "Same title");

        Assert.False(StoreGameIdentityHelper.IsSameGame(manualItem, importedItem));
    }

    private static MediaItem CreateStoreItem(string providerId, string gameId, string title)
    {
        var item = new MediaItem(title);
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = providerId;
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        return item;
    }
}
