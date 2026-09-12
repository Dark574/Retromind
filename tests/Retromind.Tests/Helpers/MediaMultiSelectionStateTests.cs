using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Tests.Helpers;

public sealed class MediaMultiSelectionStateTests
{
    [Fact]
    public void Toggle_AddsAndRemovesItemAndNotifiesBindings()
    {
        var selection = new MediaMultiSelectionState();
        var item = new MediaItem("Selected item");

        Assert.True(selection.Toggle(item));
        Assert.True(selection.Contains(item));
        Assert.Equal(1, selection.Count);
        Assert.True(selection.HasSelection);
        Assert.Equal(1, selection.Version);

        Assert.False(selection.Toggle(item));
        Assert.False(selection.Contains(item));
        Assert.Equal(0, selection.Count);
        Assert.False(selection.HasSelection);
        Assert.Equal(2, selection.Version);
    }

    [Fact]
    public void ResolveFrom_ReturnsCurrentItemsInSourceOrder()
    {
        var first = new MediaItem("First");
        var second = new MediaItem("Second");
        var third = new MediaItem("Third");
        var selection = new MediaMultiSelectionState();

        selection.Toggle(third);
        selection.Toggle(first);

        var resolved = selection.ResolveFrom([first, second, third]);

        Assert.Equal([first, third], resolved);

        selection.Clear();
        Assert.Empty(selection.ResolveFrom([first, second, third]));
    }

    [Fact]
    public void ReplaceWith_ReplacesSelectionsThatAreNoLongerShown()
    {
        var hidden = new MediaItem("Hidden");
        var firstShown = new MediaItem("First shown");
        var secondShown = new MediaItem("Second shown");
        var selection = new MediaMultiSelectionState();

        selection.Toggle(hidden);
        selection.ReplaceWith([firstShown, secondShown]);

        Assert.False(selection.Contains(hidden));
        Assert.True(selection.Contains(firstShown));
        Assert.True(selection.Contains(secondShown));
        Assert.Equal(2, selection.Count);
    }
}
