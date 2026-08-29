using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class MediaItemMovePolicyTests
{
    [Fact]
    public void Assess_ProtectsSynchronizedStoreTargets()
    {
        var source = new MediaNode("Source", NodeType.Group);
        var normalTarget = new MediaNode("Normal", NodeType.Group);
        var gogTarget = new MediaNode("GOG", NodeType.Group) { StoreProviderId = "gog" };
        var item = CreateStoreItem("gog", "123");

        Assert.Equal(
            MediaItemMoveTargetStatus.CurrentNode,
            MediaItemMovePolicy.Assess(item, source, source).Status);
        Assert.True(MediaItemMovePolicy.Assess(item, source, normalTarget).IsAllowed);
        Assert.True(MediaItemMovePolicy.Assess(item, source, gogTarget).IsAllowed);

        var unrelatedItem = new MediaItem("Manual item");
        Assert.Equal(
            MediaItemMoveTargetStatus.StoreProviderMismatch,
            MediaItemMovePolicy.Assess(unrelatedItem, source, gogTarget).Status);

        gogTarget.Items.Add(CreateStoreItem("gog", "123"));
        Assert.Equal(
            MediaItemMoveTargetStatus.DuplicateStoreItem,
            MediaItemMovePolicy.Assess(item, source, gogTarget).Status);
    }

    private static MediaItem CreateStoreItem(string providerId, string gameId)
    {
        var item = new MediaItem("Store item");
        item.CustomFields["Store.ProviderId"] = providerId;
        item.CustomFields["Store.GameId"] = gameId;
        return item;
    }
}
