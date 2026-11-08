using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services.BigMode;
using static Retromind.Tests.Services.BigMode.BigModeNavigationSessionTests;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeNavigationRefreshTests
{
    [Fact]
    public void ReorderAndRemoval_KeepKeyOrChooseNearestRemainingEntry()
    {
        var one = new MediaItem("One");
        var two = new MediaItem("Two");
        var three = new MediaItem("Three");
        var root = new MediaNode("Root")
        {
            Children = [new MediaNode("A"), new MediaNode("B")], Items = [one, two, three]
        };
        var session = new BigModeNavigationSession(new[] { root });
        session.TryReveal(Key(two));
        var frame = session.Current;
        root.Items.Move(1, 0);
        session.Refresh();
        Assert.Same(frame, session.Current);
        Assert.Equal(Key(two), frame.Selection.Key);
        Assert.Equal(2, frame.SelectedIndex);
        root.Items.Remove(two);
        session.Refresh();
        Assert.Same(one, Assert.IsType<BigModeMediaNavigationEntry>(frame.SelectedEntry).Item);
        Assert.Equal(2, frame.SelectedIndex);
        Assert.False(session.TryReveal(Key(two)));
        root.Children.Clear();
        root.Items.Clear();
        session.Refresh();
        Assert.Empty(frame.Entries);
        Assert.Equal(-1, frame.SelectedIndex);
        Assert.IsType<BigModeNavigationResult.NoAction>(session.ActivateSelected());
    }

    [Fact]
    public void UnchangedRefresh_PreservesListAndEntriesWhileDisplayDataStaysLive()
    {
        var item = new MediaItem("Before");
        var root = new MediaNode("Root") { Items = [item] };
        var session = new BigModeNavigationSession(new[] { root });
        session.ActivateSelected();
        var entries = session.Current.Entries;
        item.Title = "After";
        session.Refresh();
        Assert.Same(entries, session.Current.Entries);
        Assert.Equal("After", entries[0].Title);
        Assert.Equal(2, session.LibraryVersion);
        Assert.Throws<NotSupportedException>(() => ((IList<BigModeNavigationEntry>)entries).Clear());
    }

    [Fact]
    public void ReplacedModelsWithSameIds_RebindRealObjectsAndPreserveSelection()
    {
        var item = new MediaItem("Old");
        var node = new MediaNode("Old node") { Items = [item] };
        var roots = new ObservableCollection<MediaNode> { node };
        var session = new BigModeNavigationSession(roots);
        session.ActivateSelected();
        var replacementItem = new MediaItem("New") { Id = item.Id };
        var replacement = new MediaNode("New node") { Id = node.Id, Items = [replacementItem] };
        roots[0] = replacement;
        session.Refresh();
        Assert.Same(replacement, session.Current.OpenedNode);
        Assert.Equal(Key(item), session.Current.Selection.Key);
        var activation = Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
        Assert.Same(replacementItem, activation.Entry.Item);
        Assert.Same(replacement, activation.Entry.SourceNode);
    }

    [Fact]
    public void MovedMedia_IsOnlyActivatedWithItsNewOwnerAfterRefresh()
    {
        var item = new MediaItem("Moved");
        var a = new MediaNode("A") { Items = [item] };
        var b = new MediaNode("B");
        var session = new BigModeNavigationSession(new[] { a, b });
        session.TryReveal(Key(item));
        a.Items.Remove(item);
        b.Items.Add(item);
        session.Refresh();
        Assert.Same(a, session.Current.OpenedNode);
        Assert.Empty(session.Current.Entries);
        Assert.IsType<BigModeNavigationResult.NoAction>(session.ActivateSelected());
        Assert.True(session.TryReveal(Key(item)));
        var result = Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
        Assert.Same(item, result.Entry.Item);
        Assert.Same(b, result.Entry.SourceNode);
        Assert.Equal([b.Id], session.CaptureState().EnteredNodeIds);
    }

    [Fact]
    public void MovedOpenedNode_ReturnsToValidAncestorAndRemembersMediaOnNewPath()
    {
        var child = new MediaNode("Moved") { Items = [new MediaItem("One"), new MediaItem("Two")] };
        var a = new MediaNode("A") { Children = [child], Items = [new MediaItem("Direct")] };
        var b = new MediaNode("B");
        var session = new BigModeNavigationSession(new[] { a, b });
        session.TryReveal(Key(child.Items[1]));
        a.Children.Remove(child);
        b.Children.Add(child);
        session.Refresh();
        Assert.Same(a, session.Current.OpenedNode);
        Assert.Equal([a.Id], session.CaptureState().EnteredNodeIds);
        Assert.True(session.TryOpenNode(Key(child)));
        Assert.Equal([b.Id, child.Id], session.CaptureState().EnteredNodeIds);
        Assert.Equal(Key(child.Items[1]), session.Current.Selection.Key);
        session.Back();
        Assert.Same(b, session.Current.OpenedNode);
        Assert.Equal(Key(child), session.Current.Selection.Key);
    }

    [Fact]
    public void RemovedOpenedAncestor_ReturnsToRootAndRejectsOldKeysAndState()
    {
        var child = new MediaNode("Child") { Items = [new MediaItem("Item")] };
        var removed = new MediaNode("Removed") { Children = [child] };
        var remaining = new MediaNode("Remaining");
        var roots = new ObservableCollection<MediaNode> { removed, remaining };
        var session = new BigModeNavigationSession(roots);
        session.TryReveal(Key(child.Items[0]));
        var state = session.CaptureState();
        roots.Remove(removed);
        session.Refresh();
        Assert.Null(session.Current.OpenedNode);
        Assert.Equal(Key(remaining), session.Current.Selection.Key);
        Assert.False(session.TryOpenNode(Key(child)));
        Assert.False(session.TryReveal(Key(child.Items[0])));
        Assert.False(session.RestoreState(state));
        Assert.False(session.CanGoBack);
    }

    [Fact]
    public void InvalidRefresh_DoesNotPartiallyInstallMalformedTopology()
    {
        var root = new MediaNode("Root") { Items = [new MediaItem("Item")] };
        var session = new BigModeNavigationSession(new[] { root });
        session.ActivateSelected();
        var frame = session.Current;
        root.Children.Add(root);
        Assert.Throws<ArgumentException>(() => session.Refresh());
        Assert.Same(frame, session.Current);
        Assert.Equal(1, session.LibraryVersion);
        root.Children.Clear();
        session.Refresh();
        Assert.Equal(2, session.LibraryVersion);
        Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
    }

    [Fact]
    public void DuplicateIdsAndSharedNodes_AreRejectedWithoutPreventingCrossTypeIds()
    {
        var first = new MediaNode("First");
        Assert.Throws<ArgumentException>(() => new BigModeNavigationSession(new[] { first, first }));
        Assert.Throws<ArgumentException>(() => new BigModeNavigationSession(new[] { first, new MediaNode { Id = first.Id } }));
        var item = new MediaItem("Item");
        Assert.Throws<ArgumentException>(() => new BigModeNavigationSession(new[]
        {
            new MediaNode { Items = [item, new MediaItem { Id = item.Id }] }
        }));
    }
}
