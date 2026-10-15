using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Tests.TestInfrastructure;
using RetromindObjectConverters = Retromind.Helpers.ObjectConverters;

namespace Retromind.Tests.Helpers;

public sealed class ObjectConvertersStoreMenuTests
{
    [Fact]
    public void StorelessItem_OffersBothStoreLinksWithoutSwitchActions()
    {
        var item = new MediaItem("Storeless game");

        Assert.True(Convert(RetromindObjectConverters.StoreMenuVisible, item));
        Assert.True(Convert(RetromindObjectConverters.StoreLinkGogMenuVisible, item));
        Assert.True(Convert(RetromindObjectConverters.StoreLinkEpicMenuVisible, item));
        Assert.False(Convert(RetromindObjectConverters.StoreSwitchToGogMenuVisible, item));
        Assert.False(Convert(RetromindObjectConverters.StoreSwitchToEpicMenuVisible, item));
    }

    [Fact]
    public void LinkedItems_OfferOnlyTheOtherStoreAsSwitchTarget()
    {
        var gogItem = CreateStoreItem(StoreProviderIds.Gog, "gog-id");
        var epicItem = CreateStoreItem(StoreProviderIds.Epic, "epic-id");

        Assert.True(Convert(RetromindObjectConverters.StoreSwitchToEpicMenuVisible, gogItem));
        Assert.False(Convert(RetromindObjectConverters.StoreSwitchToGogMenuVisible, gogItem));
        Assert.True(Convert(RetromindObjectConverters.StoreSwitchToGogMenuVisible, epicItem));
        Assert.False(Convert(RetromindObjectConverters.StoreSwitchToEpicMenuVisible, epicItem));
    }

    [Fact]
    public void LegacyHeroicItem_OffersEpicMigrationAndGogSwitch()
    {
        var item = new MediaItem("Legacy Heroic game")
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = "heroic",
                    Index = 1
                }
            ],
            LauncherArgs = "epic://ExampleGame"
        };

        Assert.True(Convert(RetromindObjectConverters.StoreMenuVisible, item));
        Assert.True(Convert(RetromindObjectConverters.StoreLinkEpicMenuVisible, item));
        Assert.True(Convert(RetromindObjectConverters.StoreSwitchToGogMenuVisible, item));
        Assert.False(Convert(RetromindObjectConverters.StoreInstallMenuVisible, item));
    }

    [Fact]
    public void SteamItem_DoesNotShowManagedStoreMenu()
    {
        var item = CreateStoreItem(StoreProviderIds.Steam, "123");

        Assert.False(Convert(RetromindObjectConverters.StoreMenuVisible, item));
    }

    [Fact]
    public void ExternallyManagedEpicItem_OffersRetromindInstallation()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateStoreItem(StoreProviderIds.Epic, "ExampleGame");
        item.Files =
        [
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = temp.CreateFile("Game.exe"),
                Index = 1
            }
        ];

        Assert.True(Convert(RetromindObjectConverters.StoreInstallMenuVisible, item));
    }

    private static MediaItem CreateStoreItem(string providerId, string gameId)
    {
        var item = new MediaItem("Store game");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = providerId;
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        return item;
    }

    private static bool Convert(IValueConverter converter, MediaItem item) =>
        Assert.IsType<bool>(converter.Convert(
            item,
            typeof(bool),
            parameter: null,
            CultureInfo.InvariantCulture));
}
