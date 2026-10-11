using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.SteamAchievements;

namespace Retromind.Tests.Services.SteamAchievements;

public sealed class SteamAppIdResolverTests
{
    [Fact]
    public void TryGetAppId_UsesStructuredStoreIdentity()
    {
        var item = new MediaItem
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CustomFieldKeyHelper.StoreProviderId] = "steam",
                [CustomFieldKeyHelper.StoreGameId] = "12345"
            }
        };

        var found = SteamAppIdResolver.TryGetAppId(item, out var appId);

        Assert.True(found);
        Assert.Equal((uint)12345, appId);
    }

    [Fact]
    public void TryGetAppId_UsesLegacySteamLaunchUri()
    {
        var item = new MediaItem
        {
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = "steam",
                    Index = 1
                }
            ],
            LauncherArgs = "steam://rungameid/54321"
        };

        var found = SteamAppIdResolver.TryGetAppId(item, out var appId);

        Assert.True(found);
        Assert.Equal((uint)54321, appId);
    }

    [Fact]
    public void TryGetAppId_RejectsNonSteamStoreIdentity()
    {
        var item = new MediaItem
        {
            CustomFields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CustomFieldKeyHelper.StoreProviderId] = "gog",
                [CustomFieldKeyHelper.StoreGameId] = "12345"
            }
        };

        Assert.False(SteamAppIdResolver.TryGetAppId(item, out _));
    }
}
