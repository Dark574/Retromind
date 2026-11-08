using Retromind.Models;
using Retromind.Services.BigMode;
using Retromind.ViewModels;
using static Retromind.Tests.Services.BigMode.BigModeNavigationSessionTests;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeNavigationVisibilityTests
{
    [Fact]
    public void ExistingHomeBuilderAndNewCore_UseTheSameVisibleRootRules()
    {
        var empty = new MediaNode("Empty");
        var mixed = new MediaNode("Mixed")
        {
            Items = [new MediaItem("Visible"), new MediaItem { IsProtected = true }],
            Children = [new MediaNode { Items = [new MediaItem { IsProtected = true }] }]
        };
        var hidden = new MediaNode("Hidden") { Items = [new MediaItem { IsProtected = true }] };
        MediaNode[] roots = [empty, hidden, mixed];
        var home = BigModeHomeBuilder.Build(roots, true, "Recent", "Favorites", "Library", "Items");
        var library = Assert.Single(home);
        var homeNodes = library.Entries.Where(entry => entry.IsLibraryChild).Select(entry => entry.Node).ToArray();
        var session = new BigModeNavigationSession(roots, true);
        Assert.Equal([empty, mixed], homeNodes);
        Assert.Equal(homeNodes, session.Current.Entries.Cast<BigModeNodeNavigationEntry>().Select(entry => entry.Node));
    }

    [Fact]
    public void Filter_PreservesEmptyNodesVisibleDescendantsAndDirectMedia()
    {
        var hidden = new MediaNode("Hidden") { Items = [new MediaItem { IsProtected = true }] };
        var empty = new MediaNode("Empty");
        var ancestor = new MediaNode("Ancestor") { Children = [empty], Items = [new MediaItem { IsProtected = true }] };
        var mixed = new MediaNode("Mixed") { Children = [hidden], Items = [new MediaItem("Visible")] };
        var policy = new BigModeVisibilityPolicy(true);
        Assert.False(policy.IsNodeVisible(hidden));
        Assert.True(policy.IsNodeVisible(empty));
        Assert.True(policy.IsNodeVisible(ancestor));
        Assert.True(policy.IsNodeVisible(mixed));
        var hiddenRoot = new MediaNode("Hidden root") { Items = [new MediaItem { IsProtected = true }] };
        var session = new BigModeNavigationSession(new[] { hiddenRoot, ancestor, mixed }, true);
        Assert.Equal(["Ancestor", "Mixed"], session.Current.Entries.Select(entry => entry.Title));
        session.TryOpenNode(Key(mixed));
        Assert.Equal("Visible", Assert.Single(session.Current.Entries).Title);
        Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
    }

    [Fact]
    public void AlternativeEntryPoints_CannotRevealRestoreOrActivateProtectedMedia()
    {
        var secret = new MediaItem("Secret") { IsProtected = true };
        var visible = new MediaItem("Visible");
        var hiddenNode = new MediaNode("Hidden") { Items = [new MediaItem { IsProtected = true }] };
        var root = new MediaNode("Mixed") { Children = [hiddenNode], Items = [secret, visible] };
        var session = new BigModeNavigationSession(new[] { root }, true);
        Assert.False(session.TryOpenNode(Key(hiddenNode)));
        Assert.False(session.TryReveal(Key(hiddenNode)));
        Assert.False(session.TryReveal(Key(secret)));
        Assert.False(session.RestoreState(new([root.Id], Key(secret), 0)));
        Assert.Equal(Key(visible), session.Current.Selection.Key);
        Assert.False(session.TrySelect(Key(secret), 0));
        var activation = Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
        Assert.Same(visible, activation.Entry.Item);
        Assert.False(session.RestoreState(new([root.Id, hiddenNode.Id], Key(hiddenNode.Items[0]))));
        Assert.Same(root, session.Current.OpenedNode);
    }

    [Fact]
    public void NewlyProtectedSelection_IsRejectedEvenBeforeAdapterRefresh()
    {
        var item = new MediaItem("Selected");
        var remaining = new MediaItem("Remaining");
        var root = new MediaNode("Root") { Items = [item, remaining] };
        var session = new BigModeNavigationSession(new[] { root }, true);
        session.ActivateSelected();
        item.IsProtected = true;
        Assert.IsType<BigModeNavigationResult.NoAction>(session.ActivateSelected());
        Assert.Equal(Key(remaining), session.Current.Selection.Key);
        Assert.False(session.TryReveal(Key(item)));
        Assert.DoesNotContain(session.Current.Entries, entry => entry.Key == Key(item));
    }

    [Fact]
    public void ProtectionRefresh_ChangesVisibilityAndRetreatsFromHiddenLevel()
    {
        var item = new MediaItem("Item");
        var node = new MediaNode("Node") { Items = [item] };
        var session = new BigModeNavigationSession(new[] { node });
        session.TryReveal(Key(item));
        item.IsProtected = true;
        session.Refresh(true);
        Assert.Null(session.Current.OpenedNode);
        Assert.Empty(session.Current.Entries);
        Assert.False(session.CanGoBack);
        session.Refresh(false);
        Assert.True(session.TryReveal(Key(item)));
        Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
    }

    [Fact]
    public void StandaloneVisibilityPolicy_HandlesDeepProtectedBranchesAndCyclesIteratively()
    {
        var root = new MediaNode("Root");
        var last = root;
        for (var i = 0; i < 10_000; i++)
        {
            var next = new MediaNode("Child");
            last.Children.Add(next);
            last = next;
        }
        last.Items.Add(new MediaItem { IsProtected = true });
        var policy = new BigModeVisibilityPolicy(true);
        Assert.False(policy.IsNodeVisible(root));
        last.Children.Add(root); // A malformed graph must not loop forever in a visibility query.
        Assert.False(policy.IsNodeVisible(root));
        Assert.True(new BigModeVisibilityPolicy(false).IsNodeVisible(root));
    }
}
