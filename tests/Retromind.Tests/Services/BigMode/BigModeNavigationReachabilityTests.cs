using System.Collections;
using Retromind.Models;
using Retromind.Services.BigMode;
using static Retromind.Tests.Services.BigMode.BigModeNavigationSessionTests;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeNavigationReachabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeMixedLibrary_AllVisibleEntriesAreReachableFromRootWithRealOwnership(bool filtered)
    {
        var rootItem = new MediaItem("Root item");
        var protectedRootItem = new MediaItem("Protected root item") { IsProtected = true };
        var middleItem = new MediaItem("Middle item");
        var protectedMiddleItem = new MediaItem("Protected middle item") { IsProtected = true };
        var bottomItem = new MediaItem("Bottom item");
        var hiddenItem = new MediaItem("Hidden branch item") { IsProtected = true };
        var otherItem = new MediaItem("Other root item");
        var empty = new MediaNode("Empty");
        var hidden = new MediaNode("Hidden") { Items = [hiddenItem] };
        var bottom = new MediaNode("Bottom") { Items = [bottomItem] };
        var middle = new MediaNode("Middle") { Children = [bottom], Items = [middleItem, protectedMiddleItem] };
        var root = new MediaNode("Root")
        {
            Children = [empty, hidden, middle], Items = [rootItem, protectedRootItem]
        };
        var other = new MediaNode("Other") { Items = [otherItem] };
        var session = new BigModeNavigationSession(new[] { root, other }, filtered);
        var expectedNodes = new HashSet<MediaNode> { root, other, empty, middle, bottom };
        if (!filtered) expectedNodes.Add(hidden);
        var expectedOwners = new Dictionary<MediaItem, MediaNode>
        {
            [rootItem] = root, [middleItem] = middle, [bottomItem] = bottom, [otherItem] = other
        };
        if (!filtered)
        {
            expectedOwners.Add(protectedRootItem, root);
            expectedOwners.Add(protectedMiddleItem, middle);
            expectedOwners.Add(hiddenItem, hidden);
        }
        var reachedNodes = new HashSet<MediaNode>();
        var reachedItems = new HashSet<MediaItem>();
        var nextPositions = new Stack<int>();
        nextPositions.Push(0);
        while (nextPositions.TryPop(out var index))
        {
            if (index >= session.Current.Entries.Count)
            {
                if (session.CanGoBack) session.Back();
                continue;
            }
            nextPositions.Push(index + 1);
            Assert.True(session.TrySelect(index));
            switch (session.Current.SelectedEntry)
            {
                case BigModeNodeNavigationEntry node:
                    Assert.True(reachedNodes.Add(node.Node));
                    Assert.Contains(node.Node, expectedNodes);
                    Assert.IsType<BigModeNavigationResult.NodeOpened>(session.ActivateSelected());
                    Assert.Same(node.Node, session.Current.OpenedNode);
                    nextPositions.Push(0);
                    break;
                case BigModeMediaNavigationEntry media:
                    Assert.True(reachedItems.Add(media.Item));
                    Assert.True(expectedOwners.TryGetValue(media.Item, out var owner));
                    Assert.Same(owner, media.SourceNode);
                    Assert.Same(owner, session.Current.OpenedNode);
                    var activation = Assert.IsType<BigModeNavigationResult.MediaActivationRequested>(session.ActivateSelected());
                    Assert.Same(media, activation.Entry);
                    break;
                default: Assert.Fail("Unexpected navigation entry type."); break;
            }
        }
        Assert.True(expectedNodes.SetEquals(reachedNodes));
        Assert.True(expectedOwners.Keys.ToHashSet().SetEquals(reachedItems));
        Assert.Null(session.Current.OpenedNode);
        Assert.IsType<BigModeNavigationResult.ExitRequested>(session.Back());
        Assert.Equal([empty, hidden, middle], root.Children);
        Assert.Equal([rootItem, protectedRootItem], root.Items);
        Assert.Empty(empty.Children);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public void LargeMixedLevel_RepeatedIndexAndKeySelectionNeverRebuildsOrReadsRootAgain()
    {
        var node = new MediaNode("Large")
        {
            Children = new(Enumerable.Range(0, 1000).Select(i => new MediaNode($"Node {i}"))),
            Items = new(Enumerable.Range(0, 50_000).Select(i => new MediaItem($"Media {i}")))
        };
        var roots = new CountingRoots(node);
        var session = new BigModeNavigationSession(roots);
        session.ActivateSelected();
        var entries = session.Current.Entries;
        Assert.Equal(51_000, entries.Count);
        var rootReads = roots.Reads;
        var changes = 0;
        session.Changed += (_, _) => changes++;
        for (var i = 0; i < 5000; i++)
        {
            Assert.True(session.MoveSelection(1));
            var key = session.Current.SelectedEntry!.Key;
            Assert.True(session.TrySelect(key, indexHint: -100)); // Dictionary fallback, not a linear scan.
            Assert.Same(entries, session.Current.Entries);
        }
        Assert.Equal(5000, changes);
        Assert.Equal(rootReads, roots.Reads);
        Assert.Equal(1, session.LibraryVersion);
        Assert.True(session.TryReveal(Key(node.Items[^1])));
        Assert.Equal(50_999, session.Current.SelectedIndex);
        Assert.Same(entries, session.Current.Entries);
        Assert.Equal(rootReads, roots.Reads);
    }

    private sealed class CountingRoots(MediaNode node) : IReadOnlyList<MediaNode>
    {
        public int Reads { get; private set; }
        public int Count => 1;
        public MediaNode this[int index]
        {
            get
            {
                Reads++;
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                return node;
            }
        }
        public IEnumerator<MediaNode> GetEnumerator() { yield return this[0]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
