using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SearchAreaViewModelTreeTrackingTests
{
    [Fact]
    public void RemovingDeeplyNestedNode_RemovesItFromExplicitSearchScope()
    {
        var root = new MediaNode("Root", NodeType.Area);
        var child = new MediaNode("Child", NodeType.Group);
        var grandchild = new MediaNode("Grandchild", NodeType.Group);
        root.Children.Add(child);
        child.Children.Add(grandchild);

        var roots = new ObservableCollection<MediaNode> { root };
        using var viewModel = new SearchAreaViewModel(roots);
        viewModel.ApplyScopeSelection([grandchild.Id]);

        Assert.Equal(1, viewModel.SelectedScopeCount);

        child.Children.Remove(grandchild);

        Assert.Equal(0, viewModel.SelectedScopeCount);
    }

    [Fact]
    public void NewlyAddedNestedSubtree_IsObservedRecursively()
    {
        var root = new MediaNode("Root", NodeType.Area);
        var child = new MediaNode("Child", NodeType.Group);
        root.Children.Add(child);

        var roots = new ObservableCollection<MediaNode> { root };
        using var viewModel = new SearchAreaViewModel(roots);

        var grandchild = new MediaNode("Grandchild", NodeType.Group);
        var greatGrandchild = new MediaNode("Great-grandchild", NodeType.Group);
        grandchild.Children.Add(greatGrandchild);
        child.Children.Add(grandchild);
        viewModel.ApplyScopeSelection([greatGrandchild.Id]);

        Assert.Equal(1, viewModel.SelectedScopeCount);

        grandchild.Children.Remove(greatGrandchild);

        Assert.Equal(0, viewModel.SelectedScopeCount);
    }
}
