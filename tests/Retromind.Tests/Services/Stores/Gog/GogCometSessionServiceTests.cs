using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Comet;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogCometSessionServiceTests
{
    [Fact]
    public void IsEligible_AcceptsStructuredGogIdentityRegardlessOfMediaType()
    {
        foreach (var mediaType in new[] { MediaType.Native, MediaType.Emulator, MediaType.Command })
        {
            var item = new MediaItem("GOG game") { MediaType = mediaType };
            item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "GoG";
            item.CustomFields[CustomFieldKeyHelper.StoreGameId] = "123456";

            Assert.True(GogCometSessionService.IsEligible(item));
        }
    }

    [Fact]
    public void IsEligible_RejectsUnlinkedAndOtherStoreItems()
    {
        var unlinked = new MediaItem("Unlinked");
        var epic = new MediaItem("Epic");
        epic.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "epic";
        epic.CustomFields[CustomFieldKeyHelper.StoreGameId] = "example";

        Assert.False(GogCometSessionService.IsEligible(unlinked));
        Assert.False(GogCometSessionService.IsEligible(epic));
    }
}
