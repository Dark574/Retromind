using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class StoreProviderBadgeHelperTests
{
    [Theory]
    [InlineData("gog", "GOG")]
    [InlineData("STEAM", "STEAM")]
    [InlineData("epic", "EPIC")]
    [InlineData("heroic", "EPIC")]
    public void GetBadgeText_UsesStructuredProviderIdentity(string providerId, string expected)
    {
        var item = new MediaItem("Game");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = providerId;

        Assert.Equal(expected, StoreProviderBadgeHelper.GetBadgeText(item));
    }

    [Theory]
    [InlineData("steam", "steam://rungameid/123", "STEAM")]
    [InlineData("heroic", "epic://namespace", "EPIC")]
    public void GetBadgeText_RecognizesLegacyImports(string command, string arguments, string expected)
    {
        var item = new MediaItem("Game")
        {
            MediaType = MediaType.Command,
            Files =
            [
                new MediaFileRef
                {
                    Kind = MediaFileKind.Absolute,
                    Path = command,
                    Index = 1
                }
            ],
            LauncherArgs = arguments
        };

        Assert.Equal(expected, StoreProviderBadgeHelper.GetBadgeText(item));
    }

    [Fact]
    public void GetBadgeText_DoesNotMarkUnrelatedCommands()
    {
        var item = new MediaItem("Game")
        {
            MediaType = MediaType.Command,
            LauncherPath = "xdg-open",
            LauncherArgs = "https://example.com"
        };

        Assert.Null(StoreProviderBadgeHelper.GetBadgeText(item));
    }
}
