using System.Collections.Specialized;
using Retromind.Extensions;
using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed class CircularWindowHelperTests
{
    [Fact]
    public void SynchronizeCircularWindow_ForwardStepRetainsOverlappingItems()
    {
        string[] source = ["A", "B", "C", "D", "E", "F", "G"];
        var target = new RangeObservableCollection<string>();
        CircularWindowHelper.SynchronizeCircularWindow(source, "C", 5, target);
        var changes = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => changes.Add(args.Action);

        CircularWindowHelper.SynchronizeCircularWindow(source, "D", 5, target);

        Assert.Equal(["B", "C", "D", "E", "F"], target);
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add],
            changes);
    }

    [Fact]
    public void SynchronizeCircularWindow_BackwardStepRetainsOverlappingItems()
    {
        string[] source = ["A", "B", "C", "D", "E", "F", "G"];
        var target = new RangeObservableCollection<string>();
        CircularWindowHelper.SynchronizeCircularWindow(source, "D", 5, target);
        var changes = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => changes.Add(args.Action);

        CircularWindowHelper.SynchronizeCircularWindow(source, "C", 5, target);

        Assert.Equal(["A", "B", "C", "D", "E"], target);
        Assert.Equal(
            [NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add],
            changes);
    }

    [Fact]
    public void SynchronizeCircularWindow_UnchangedWindowDoesNotNotify()
    {
        string[] source = ["A", "B", "C"];
        var target = new RangeObservableCollection<string>();
        CircularWindowHelper.SynchronizeCircularWindow(source, "A", 5, target);
        var notificationCount = 0;
        target.CollectionChanged += (_, _) => notificationCount++;

        CircularWindowHelper.SynchronizeCircularWindow(source, "C", 5, target);

        Assert.Equal(source, target);
        Assert.Equal(0, notificationCount);
    }
}
