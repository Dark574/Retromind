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
    public void ImportedGame_OffersInstallUntilItHasAPlayableManualConfiguration()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateEpicItem();

        Assert.True(EpicMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.False(EpicMediaItemStateHelper.IsLaunchReady(item));

        item.Files =
        [
            new MediaFileRef
            {
                Kind = MediaFileKind.Absolute,
                Path = temp.CreateFile("Game.exe")
            }
        ];

        Assert.False(EpicMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.True(EpicMediaItemStateHelper.IsLaunchReady(item));
        Assert.False(EpicMediaItemStateHelper.IsRetromindManagedInstallation(item));
    }

    [Fact]
    public void ManagedInstallation_RequiresExistingStoredInstallPathButNotExecutable()
    {
        using var temp = new TemporaryDirectory();
        var item = CreateEpicItem();
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = temp.CreateDirectory("Game");

        Assert.True(EpicMediaItemStateHelper.IsRetromindManagedInstallation(item));
        Assert.False(EpicMediaItemStateHelper.IsLaunchReady(item));
        Assert.False(EpicMediaItemStateHelper.ShouldOfferInstall(item));
        Assert.True(EpicMediaItemStateHelper.CanCheckUpdates(item));
        Assert.False(EpicMediaItemStateHelper.HasUpdateAvailable(item));

        item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "true";

        Assert.True(EpicMediaItemStateHelper.HasUpdateAvailable(item));
    }

    [Fact]
    public void ManagedInstallMetadata_RemainsDetectableWhenExecutableIsMissing()
    {
        var item = CreateEpicItem();
        item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] =
            Path.Combine(Path.GetTempPath(), $"retromind-missing-{Guid.NewGuid():N}");

        Assert.False(EpicMediaItemStateHelper.IsRetromindManagedInstallation(item));
        Assert.True(EpicMediaItemStateHelper.HasRetromindManagedInstallMetadata(item));
        Assert.True(EpicMediaItemStateHelper.ShouldOfferInstall(item));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  build 42  ", "build 42")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void TryGetInstalledVersion_ReturnsOnlyKnownVersion(string storedVersion, string? expected)
    {
        var item = CreateEpicItem();
        item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = storedVersion;

        Assert.Equal(expected, EpicMediaItemStateHelper.TryGetInstalledVersion(item));
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
