using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Tests.Services;

public sealed class LibraryChangeTrackerTests
{
    [Fact]
    public void AddingRootNode_MarksLibraryDirty()
    {
        var roots = new ObservableCollection<MediaNode>();
        var tracker = CreateTracker();
        tracker.Initialize(roots);

        roots.Add(new MediaNode("Root", NodeType.Area));

        Assert.True(tracker.IsDirty);
        Assert.Equal(1, tracker.DirtyVersion);
        tracker.StopTracking();
    }

    [Fact]
    public void AddingMediaItem_MarksLibraryDirty()
    {
        var node = new MediaNode("Games", NodeType.Group);
        var roots = new ObservableCollection<MediaNode> { node };
        var tracker = CreateTracker();
        tracker.Initialize(roots);

        node.Items.Add(new MediaItem("Game"));

        Assert.True(tracker.IsDirty);
        Assert.Equal(1, tracker.DirtyVersion);
        tracker.StopTracking();
    }

    [Fact]
    public void AddingChildNode_MarksLibraryDirty()
    {
        var node = new MediaNode("Root", NodeType.Area);
        var roots = new ObservableCollection<MediaNode> { node };
        var tracker = CreateTracker();
        tracker.Initialize(roots);

        node.Children.Add(new MediaNode("Child", NodeType.Group));

        Assert.True(tracker.IsDirty);
        Assert.Equal(1, tracker.DirtyVersion);
        tracker.StopTracking();
    }

    [Fact]
    public void ClearingItems_UntracksRemovedItems()
    {
        var removedItem = new MediaItem("Game");
        var node = new MediaNode("Games", NodeType.Group);
        node.Items.Add(removedItem);
        var roots = new ObservableCollection<MediaNode> { node };
        var tracker = CreateTracker();
        tracker.Initialize(roots);

        node.Items.Clear();
        var versionAfterClear = tracker.DirtyVersion;
        removedItem.IsFavorite = true;

        Assert.True(tracker.IsDirty);
        Assert.Equal(1, versionAfterClear);
        Assert.Equal(versionAfterClear, tracker.DirtyVersion);
        tracker.StopTracking();
    }

    [Fact]
    public void ClearingChildren_UntracksRemovedSubtree()
    {
        var child = new MediaNode("Child", NodeType.Group);
        var root = new MediaNode("Root", NodeType.Area);
        root.Children.Add(child);
        var roots = new ObservableCollection<MediaNode> { root };
        var tracker = CreateTracker();
        tracker.Initialize(roots);

        root.Children.Clear();
        var versionAfterClear = tracker.DirtyVersion;
        child.Items.Add(new MediaItem("Detached game"));

        Assert.True(tracker.IsDirty);
        Assert.Equal(1, versionAfterClear);
        Assert.Equal(versionAfterClear, tracker.DirtyVersion);
        tracker.StopTracking();
    }

    private static LibraryChangeTracker CreateTracker()
        => new(new MediaDataService(), (_, _) => { });
}
