using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class MediaItemMovePolicyTests
{
    [Fact]
    public void Assess_AllowsEveryDifferentTargetNode()
    {
        var source = new MediaNode("Source");
        var target = new MediaNode("Target");
        Assert.Equal(
            MediaItemMoveTargetStatus.CurrentNode,
            MediaItemMovePolicy.Assess(source, source).Status);
        Assert.True(MediaItemMovePolicy.Assess(source, target).IsAllowed);
    }
}
