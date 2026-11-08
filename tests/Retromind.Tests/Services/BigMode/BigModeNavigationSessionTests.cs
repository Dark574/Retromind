using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services.BigMode;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeNavigationSessionTests
{
    [Fact]
    public void PureNodeLevel_PreservesOrderAndOpensRealNodes()
    {
        var z = new MediaNode("Z");
        var a = new MediaNode("A");
        var session = new BigModeNavigationSession(new[] { z, a });
        Assert.Equal(["Z", "A"], session.Current.Entries.Select(entry => entry.Title));
        Assert.True(session.TrySelect(1));
        var result = Assert.IsType<BigModeNavigationResult.NodeOpened>(session.ActivateSelected());
        Assert.Same(a, result.Frame.OpenedNode);
        Assert.Empty(result.Frame.Entries);
        Assert.True(session.CanGoBack);
    }

    [Fact]
    public void PureMediaLevel_ActivationReturnsRealItemAndOwningNodeWithoutNavigating()
    {
        var first = new MediaItem("First");
        var second = new MediaItem("Second");
        var node = new MediaNode("Media") { Items = [first, second] };
        var session = new BigModeNavigationSession(new[] { node });
        session.ActivateSelected();
        Assert.Equal(["First", "Second"], session.Current.Entries.Select(entry => entry.Title));
        session.TrySelect(1);
        var frame = session.Current;
        var version = session.Revision;
        var result = Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
        Assert.Same(second, result.Entry.Item);
        Assert.Same(node, result.Entry.SourceNode);
        Assert.Same(frame, session.Current);
        Assert.Equal(version, session.Revision);
    }

    [Fact]
    public void MixedLevel_NodesPrecedeDirectItemsAndKeysDistinguishBothKinds()
    {
        var child = new MediaNode("Child") { Id = "shared-id" };
        var item = new MediaItem("Item") { Id = "shared-id" };
        var parent = new MediaNode("Parent") { Children = [child], Items = [item] };
        var session = new BigModeNavigationSession(new[] { parent });
        session.ActivateSelected();
        var nodeEntry = Assert.IsType<BigModeNodeNavigationEntry>(session.Current.Entries[0]);
        var mediaEntry = Assert.IsType<BigModeMediaNavigationEntry>(session.Current.Entries[1]);
        Assert.Same(child, nodeEntry.Node);
        Assert.Same(parent, nodeEntry.ParentNode);
        Assert.Same(item, mediaEntry.Item);
        Assert.NotEqual(nodeEntry.Key, mediaEntry.Key);
        Assert.True(session.TrySelect(mediaEntry.Key, indexHint: 0)); // Wrong hint cannot select a same-ID node.
        Assert.Equal(1, session.Current.SelectedIndex);
        Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
        Assert.True(session.TrySelect(nodeEntry.Key, indexHint: 1));
        Assert.IsType<BigModeNavigationResult.NodeOpened>(session.ActivateSelected());
        Assert.Same(child, session.Current.OpenedNode);
    }

    [Fact]
    public void EmptyRootAndEmptyNode_HaveSafeSelectionAndDistinctBackAndExitBehavior()
    {
        var empty = new BigModeNavigationSession(Array.Empty<MediaNode>());
        Assert.Null(empty.Current.OpenedNode);
        Assert.Null(empty.Current.SelectedEntry);
        Assert.Equal(-1, empty.Current.SelectedIndex);
        Assert.False(empty.MoveSelection(1));
        Assert.False(empty.TrySelect(0));
        Assert.IsType<BigModeNavigationResult.NoAction>(empty.ActivateSelected());
        Assert.IsType<BigModeNavigationResult.ExitRequested>(empty.Back());

        var node = new MediaNode("Empty");
        var session = new BigModeNavigationSession(new[] { node }, true);
        session.ActivateSelected();
        var frame = session.Current;
        Assert.Empty(frame.Entries);
        Assert.Equal(-1, frame.SelectedIndex);
        Assert.IsType<BigModeNavigationResult.ExitRequested>(session.RequestExit());
        Assert.Same(frame, session.Current); // Hard exit never silently pops a level.
        Assert.True(session.CanGoBack);
        Assert.IsType<BigModeNavigationResult.NodeOpened>(session.Back());
        Assert.Null(session.Current.OpenedNode);
        Assert.False(session.CanGoBack);
        Assert.IsType<BigModeNavigationResult.ExitRequested>(session.Back());
    }

    [Fact]
    public void BackAndReentry_RestoreNodeAndMediaSelectionsAtEveryLevel()
    {
        var child = new MediaNode("Child");
        var parent = new MediaNode("Parent")
        {
            Children = [child], Items = [new MediaItem("One"), new MediaItem("Two")]
        };
        var session = new BigModeNavigationSession(new[] { new MediaNode("Other"), parent });
        session.TrySelect(1);
        session.ActivateSelected();
        var parentFrame = session.Current;
        session.ActivateSelected(); // Child.
        session.Back();
        Assert.Same(parentFrame, session.Current);
        Assert.Same(child, Assert.IsType<BigModeNodeNavigationEntry>(session.Current.SelectedEntry).Node);
        session.TrySelect(2);
        var selectedKey = session.Current.SelectedEntry!.Key;
        session.Back();
        Assert.Equal(1, session.Current.SelectedIndex);
        session.ActivateSelected();
        Assert.Equal(selectedKey, session.Current.SelectedEntry!.Key);
        Assert.Equal(2, session.Current.SelectedIndex);
    }

    [Fact]
    public void SelectionSteps_DoNotRebuildEntriesOrNotifyForNoopAndInvalidSelections()
    {
        var node = new MediaNode("Mixed")
        {
            Children = [new MediaNode("Child")], Items = [new MediaItem("One"), new MediaItem("Two")]
        };
        var session = new BigModeNavigationSession(new[] { node });
        session.ActivateSelected();
        var frame = session.Current;
        var entries = frame.Entries;
        var changes = 0;
        session.Changed += (_, _) => changes++;
        Assert.True(session.TrySelect(0));
        Assert.False(session.TrySelect(-1));
        Assert.False(session.TrySelect(3));
        Assert.False(session.TrySelect(new BigModeNavigationKey(BigModeNavigationEntryKind.MediaItem, "missing")));
        Assert.False(session.MoveSelection(0));
        Assert.Equal(0, changes);
        session.MoveSelection(-1);
        Assert.Equal(2, frame.SelectedIndex);
        session.MoveSelection(1);
        Assert.Equal(0, frame.SelectedIndex);
        session.MoveSelection(int.MinValue);
        Assert.InRange(frame.SelectedIndex, 0, 2);
        Assert.Same(entries, frame.Entries);
        Assert.Same(frame, session.Current);
        Assert.Equal(1, session.LibraryVersion);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void StateRoundTrip_UsesTypedKeyAndChecksIndexHint()
    {
        var leaf = new MediaNode("Leaf") { Items = [new MediaItem("One"), new MediaItem("Two")] };
        var root = new MediaNode("Root") { Children = [leaf], Items = [new MediaItem("Direct")] };
        var first = new BigModeNavigationSession(new[] { root });
        first.TryReveal(Key(leaf.Items[1]));
        var state = first.CaptureState();
        Assert.Equal([root.Id, leaf.Id], state.EnteredNodeIds);
        var restored = new BigModeNavigationSession(new[] { root });
        Assert.True(restored.RestoreState(new(state.EnteredNodeIds, state.SelectedKey, 99)));
        Assert.Same(leaf, restored.Current.OpenedNode);
        Assert.Equal(1, restored.Current.SelectedIndex);
        restored.Back();
        Assert.Same(leaf, Assert.IsType<BigModeNodeNavigationEntry>(restored.Current.SelectedEntry).Node);
        restored.Back();
        Assert.Same(root, Assert.IsType<BigModeNodeNavigationEntry>(restored.Current.SelectedEntry).Node);
    }

    [Fact]
    public void StateCopiesPathAndRecoversMissingOrInvalidTargetsToValidPrefix()
    {
        var leaf = new MediaNode("Leaf");
        var root = new MediaNode("Root") { Children = [leaf] };
        var path = new List<string> { root.Id, "missing", leaf.Id };
        var state = new BigModeNavigationState(path, Key(leaf));
        path.Clear();
        Assert.Equal(3, state.EnteredNodeIds.Count);
        var session = new BigModeNavigationSession(new[] { root });
        Assert.False(session.RestoreState(state));
        Assert.Same(root, session.Current.OpenedNode);
        Assert.Equal([root.Id], session.CaptureState().EnteredNodeIds);
        Assert.False(session.TryOpenNode(default));
        Assert.False(session.TryReveal(default));
        Assert.False(session.TryReveal(new((BigModeNavigationEntryKind)99, root.Id)));
        Assert.False(session.TryOpenNode(Key(new MediaNode("Unknown"))));
        Assert.False(session.RestoreState(new([root.Id], new(BigModeNavigationEntryKind.MediaItem, "missing"), 50)));
        Assert.Same(leaf, Assert.IsType<BigModeNodeNavigationEntry>(session.Current.SelectedEntry).Node);
    }

    [Fact]
    public void ChangedEvent_ObservesOneConsistentStatePerJump()
    {
        var child = new MediaNode("Child") { Items = [new MediaItem("Item")] };
        var root = new MediaNode("Root") { Children = [child] };
        var session = new BigModeNavigationSession(new[] { root });
        var changes = 0;
        session.Changed += (_, _) =>
        {
            changes++;
            Assert.Same(child, session.Current.OpenedNode);
            Assert.Equal(Key(child.Items[0]), session.Current.Selection.Key);
            Assert.Equal(0, session.Current.SelectedIndex);
        };
        Assert.True(session.TryReveal(Key(child.Items[0])));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void DeepTree_TraversalBackAndCaptureDoNotUseCallStackRecursion()
    {
        const int depth = 10_000;
        var root = new MediaNode("0");
        var last = root;
        for (var i = 1; i < depth; i++)
        {
            var next = new MediaNode(i.ToString());
            last.Children.Add(next);
            last = next;
        }
        last.Items.Add(new MediaItem("Bottom"));
        var session = new BigModeNavigationSession(new[] { root }, true);
        for (var i = 0; i < depth; i++)
            Assert.IsType<BigModeNavigationResult.NodeOpened>(session.ActivateSelected());
        var state = session.CaptureState();
        Assert.Equal(depth, state.EnteredNodeIds.Count);
        var restored = new BigModeNavigationSession(new[] { root }, true);
        Assert.True(restored.RestoreState(state));
        Assert.Same(last, restored.Current.OpenedNode);
        Assert.Same(last, Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected()).Entry.SourceNode);
        for (var i = 0; i < depth; i++) session.Back();
        Assert.Null(session.Current.OpenedNode);
        Assert.False(session.CanGoBack);
        Assert.True(new BigModeVisibilityPolicy(true).IsNodeVisible(root));
    }

    [Fact]
    public void Presentation_UsesLiveModelAndRealOwnerFallbackWithoutChangingAssetsOrPaths()
    {
        var item = new MediaItem("Title") { Description = "Description" };
        var logo = new MediaAsset { Type = AssetType.Logo, RelativePath = "Library/Node/logo.png" };
        var cover = new MediaAsset { Type = AssetType.Cover, RelativePath = "Library/Node/cover.png" };
        var node = new MediaNode("Owner") { Assets = [logo, cover], Items = [item] };
        var session = new BigModeNavigationSession(new[] { node });
        var nodeEntry = Assert.IsType<BigModeNodeNavigationEntry>(session.Current.SelectedEntry);
        Assert.Equal(node.PrimaryCoverAbsolutePath, nodeEntry.CoverPath);
        session.ActivateSelected();
        var entry = Assert.IsType<BigModeMediaNavigationEntry>(session.Current.SelectedEntry);
        Assert.Null(entry.LogoPath);
        node.LogoFallbackEnabled = true;
        Assert.Equal(node.PrimaryLogoAbsolutePath, entry.LogoPath);
        Assert.Equal(node.PrimaryCoverAbsolutePath, entry.CoverPath);
        item.Title = "Updated";
        Assert.Equal("Updated", entry.Title);
        Assert.Equal("Description", entry.Description);
        Assert.Empty(item.Assets);
        Assert.Equal("Library/Node/logo.png", logo.RelativePath);
        Assert.Equal("Library/Node/cover.png", cover.RelativePath);
    }

    internal static BigModeNavigationKey Key(MediaNode node) => new(BigModeNavigationEntryKind.Node, node.Id);
    internal static BigModeNavigationKey Key(MediaItem item) => new(BigModeNavigationEntryKind.MediaItem, item.Id);
}
