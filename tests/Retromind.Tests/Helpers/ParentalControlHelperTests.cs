using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class ParentalControlHelperTests
{
    [Fact]
    public void IsNodeEffectivelyProtected_UsesAllItemsInSubtree()
    {
        var root = new MediaNode("Root", NodeType.Group);
        var child = new MediaNode("Child", NodeType.Group);
        root.Items.Add(new MediaItem("Protected") { IsProtected = true });
        child.Items.Add(new MediaItem("Unprotected"));
        root.Children.Add(child);

        Assert.False(ParentalControlHelper.IsNodeEffectivelyProtected(root));

        child.Items[0].IsProtected = true;

        Assert.True(ParentalControlHelper.IsNodeEffectivelyProtected(root));
    }

    [Fact]
    public void IsNodeEffectivelyProtected_UsesAutoProtectStateForEmptySubtree()
    {
        var node = new MediaNode("Empty", NodeType.Group)
        {
            AutoProtectNewChildren = true
        };

        Assert.True(ParentalControlHelper.IsNodeEffectivelyProtected(node));
    }

    [Fact]
    public void ApplyNodeProtectionRecursive_UpdatesEntireSubtree()
    {
        var root = new MediaNode("Root", NodeType.Group);
        var child = new MediaNode("Child", NodeType.Group);
        root.Items.Add(new MediaItem("Root item"));
        child.Items.Add(new MediaItem("Child item"));
        root.Children.Add(child);

        ParentalControlHelper.ApplyNodeProtectionRecursive(root, true);

        Assert.True(root.AutoProtectNewChildren);
        Assert.True(child.AutoProtectNewChildren);
        Assert.True(root.Items[0].IsProtected);
        Assert.True(child.Items[0].IsProtected);
    }

    [Fact]
    public void RecalculateAutoProtectStates_UpdatesNonEmptyNodesAndPreservesEmptyNodes()
    {
        var root = new MediaNode("Root", NodeType.Group);
        var protectedChild = new MediaNode("Protected", NodeType.Group);
        var emptyChild = new MediaNode("Empty", NodeType.Group)
        {
            AutoProtectNewChildren = true
        };
        protectedChild.Items.Add(new MediaItem("Protected item") { IsProtected = true });
        root.Items.Add(new MediaItem("Unprotected item"));
        root.Children.Add(protectedChild);
        root.Children.Add(emptyChild);

        ParentalControlHelper.RecalculateAutoProtectStates([root]);

        Assert.False(root.AutoProtectNewChildren);
        Assert.True(protectedChild.AutoProtectNewChildren);
        Assert.True(emptyChild.AutoProtectNewChildren);
    }
}
