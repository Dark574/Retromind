using System.Collections.ObjectModel;
using System.Text.Json;
using Retromind.Models;
using Retromind.Services.BigMode;

namespace Retromind.Tests.Services.BigMode;

public sealed class BigModeIntegrationStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBookmark_RoundTripsBothKindsIncludingSameId(bool media)
    {
        var child = new MediaNode("Child") { Id = "same" };
        var item = new MediaItem("Direct") { Id = "same" };
        var root = new MediaNode("Root") { Children = [child], Items = [item] };
        var session = new BigModeNavigationSession([root]);
        session.ActivateSelected();
        session.TrySelect(media ? 1 : 0);
        var settings = new AppSettings();
        BigModeNavigationPersistence.Save(session, settings);
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var restored = new BigModeNavigationSession([root]);
        BigModeNavigationPersistence.Restore(restored, copy);
        Assert.Equal(session.CaptureState().SelectedKey, restored.Current.SelectedEntry!.Key);
        Assert.Same(root, restored.Current.OpenedNode);
        Assert.Equal(media ? root.Id : child.Id, copy.LastSelectedNodeId);
        Assert.Equal(media ? item.Id : null, copy.LastSelectedMediaId);
    }

    [Fact]
    public void LegacyMixedMediaState_IsMigratedWithoutLosingDirectItem()
    {
        var item = new MediaItem("Direct");
        var root = new MediaNode("Root") { Children = [new("Child")], Items = [item] };
        var settings = new AppSettings { LastBigModeNavigationPath = [root.Id],
            LastBigModeSelectedNodeId = item.Id, LastBigModeWasItemView = true };
        var session = new BigModeNavigationSession([root]);
        BigModeNavigationPersistence.Restore(session, settings);
        Assert.Same(item, Assert.IsType<BigModeMediaNavigationEntry>(session.Current.SelectedEntry).Item);
        BigModeNavigationPersistence.Save(session, settings);
        Assert.Equal(item.Id, settings.BigModeNavigation!.SelectedMediaId);
    }

    [Fact]
    public void DesktopShortcut_UsesActualOwnerAndOpeningANodeDoesNotSelectItInItsParent()
    {
        var item = new MediaItem("Direct");
        var child = new MediaNode("Child") { Items = [item] };
        var root = new MediaNode("Root") { Children = [child], Items = [new("Other")] };
        var settings = new AppSettings();
        BigModeNavigationPersistence.RecordDesktopSelection(settings, root, item); // supplied owner is stale
        var resolved = new BigModeNavigationSession([root]);
        BigModeNavigationPersistence.Restore(resolved, settings);
        BigModeNavigationPersistence.Save(resolved, settings);
        Assert.Equal([root.Id, child.Id], settings.BigModeNavigation!.EnteredNodeIds);
        Assert.Equal(child.Id, settings.LastSelectedNodeId);
        BigModeNavigationPersistence.RecordDesktopSelection(settings, child, null);
        var session = new BigModeNavigationSession([root]);
        BigModeNavigationPersistence.Restore(session, settings);
        Assert.Same(child, session.Current.OpenedNode);
    }

    [Fact]
    public void ProtectedOrRemovedBookmark_RecoversWithoutExposingIt()
    {
        var item = new MediaItem("Hidden") { IsProtected = true };
        var root = new MediaNode("Root") { Items = [item], Children = [new("Empty")] };
        var settings = new AppSettings { BigModeNavigation = new() {
            EnteredNodeIds = [root.Id], SelectedMediaId = item.Id, SelectedIndexHint = 1 } };
        var session = new BigModeNavigationSession([root], true);
        BigModeNavigationPersistence.Restore(session, settings);
        Assert.IsType<BigModeNodeNavigationEntry>(session.Current.SelectedEntry);
        root.Children.Clear();
        session.Refresh();
        Assert.Empty(session.Current.Entries); // parent now hidden; recovered virtual root
        BigModeNavigationPersistence.Save(session, settings);
        Assert.Null(settings.BigModeNavigation!.SelectedMediaId);
    }

    [Fact]
    public void Observer_CoalescesMoveReplacementIdentityAndProtectionAndUnsubscribes()
    {
        var item = new MediaItem("Item");
        var a = new MediaNode("A") { Items = [item] };
        var b = new MediaNode("B");
        ObservableCollection<MediaNode> roots = [a, b];
        var session = new BigModeNavigationSession(roots);
        var changes = 0;
        using var observer = new BigModeLibraryObserver(roots, () => changes++);
        var original = a.Items;
        a.Items.Remove(item);
        b.Items.Add(item);
        a.Children = [new("Added")];
        a.Items = [];
        b.Id = "renamed";
        item.IsProtected = true;
        Assert.Equal(1, changes);
        session.Refresh(true);
        observer.Rebind();
        original.Add(new("Detached collection edit"));
        Assert.False(observer.IsDirty);
        b.Items = [new("Replacement")];
        Assert.Equal(2, changes);
        observer.Rebind();
        item.IsProtected = false; // detached old item
        Assert.False(observer.IsDirty);
        observer.Dispose();
        roots.Clear();
        Assert.False(observer.IsDirty);
    }

    [Fact]
    public void PresentationChanges_DoNotRebuildSnapshotAndRowsForwardOnlyWhileSubscribed()
    {
        var item = new MediaItem("Before");
        var root = new MediaNode("Root") { Items = [item] };
        var session = new BigModeNavigationSession([root]);
        session.ActivateSelected();
        using var observer = new BigModeLibraryObserver(new[] { root }, () => Assert.Fail("Topology did not change"));
        var row = session.Current.SelectedEntry!;
        var notifications = 0;
        System.ComponentModel.PropertyChangedEventHandler handler = (_, _) => notifications++;
        row.PropertyChanged += handler;
        item.Title = "After";
        Assert.Equal("After", row.Title);
        Assert.True(notifications > 0);
        row.PropertyChanged -= handler;
        var previous = notifications;
        item.Title = "Detached";
        Assert.Equal(previous, notifications);
        Assert.False(observer.IsDirty);
    }
}
