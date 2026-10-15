using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class EpicMediaItemStateHelperTests
{
    [Fact]
    public void BuildDefaultPrefixPath_IncludesGameIdAndReadableTitle()
    {
        var path = EpicMediaItemStateHelper.BuildDefaultPrefixPath(
            "6ef17d9f63c143ea88a4a4bcbcf1e91c",
            "TerraScape Deluxe Edition");

        Assert.Equal(
            Path.Combine(
                "Prefixes",
                "epic_6ef17d9f63c143ea88a4a4bcbcf1e91c_TerraScape_Deluxe_Edition"),
            path);
    }

    [Fact]
    public void ImportedOwnedGame_OffersInstallUntilExecutableIsConfigured()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateEpicItem();

        Assert.True(EpicMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.False(EpicMediaItemStateHelper.IsInstalled(item));

        item.Files =
        [
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = temp.CreateFile("Game.exe")
            }
        ];

        Assert.False(EpicMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.True(EpicMediaItemStateHelper.IsInstalled(item));
        Assert.False(EpicMediaItemStateHelper.IsRetromindManagedInstallation(item));
    }

    [Fact]
    public void ManagedInstallation_AlsoRequiresStoredInstallPath()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateEpicItem();
        item.Files =
        [
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = temp.CreateFile("Game.exe")
            }
        ];
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = "Library/Games/Epic/Game";

        Assert.True(EpicMediaItemStateHelper.IsRetromindManagedInstallation(item));
    }

    private static MediaItem CreateEpicItem() => new("Epic game")
    {
        CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomFieldKeyHelper.StoreProviderId] = StoreProviderIds.Epic,
            [CustomFieldKeyHelper.StoreGameId] = "ExampleGame"
        }
    };
}
