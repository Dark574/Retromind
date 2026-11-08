using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SearchAreaViewModelTreeTrackingTests
{
    [Fact]
    public void ScopeDialogFactory_PassesTheExistingErrorServiceAndDoesNotChangeCurrentSelection()
    {
        var root = new MediaNode("Root");
        var child = new MediaNode("Child");
        var sibling = new MediaNode("Sibling");
        root.Children.Add(child);
        root.Children.Add(sibling);
        var errors = new RecordingErrorService();
        using var vm = new SearchAreaViewModel(new[] { root }, errors);
        vm.ApplyScopeSelection([child.Id]);
        var dialog = vm.CreateScopeDialogViewModel();
        Assert.Null(dialog.RootNodes[0].IsChecked);
        dialog.ClearAllCommand.Execute(null);
        Assert.True(vm.GetSelectedScopeIdsSnapshot().SetEquals([child.Id]));
        Assert.Empty(errors.Reports);

        // The dialog must reject a hierarchy that became invalid after the search area was created.
        // An ordinary list avoids unrelated live search/tree tracking during this controlled mutation.
        child.Children.Add(root);
        var invalid = vm.CreateScopeDialogViewModel();
        Assert.True(invalid.HasBuildFailure);
        Assert.True(invalid.GetSelectedNodeIds().SetEquals([child.Id]));
        Assert.Equal("SearchScope.BuildFailed", Assert.Single(errors.Reports).Code);
        Assert.True(vm.GetSelectedScopeIdsSnapshot().SetEquals([child.Id]));
        child.Children.Remove(root);
    }

    [Fact]
    public void ScopeDialogFactory_KeepsTheExistingParentalRootFilter()
    {
        var visible = new MediaNode("Visible") { IsVisibleInTree = true };
        var hidden = new MediaNode("Hidden") { IsVisibleInTree = false };
        using var vm = new SearchAreaViewModel(new[] { visible, hidden }, new RecordingErrorService(), parentalFilterActive: true);
        var dialog = vm.CreateScopeDialogViewModel();
        Assert.Same(visible, Assert.Single(dialog.RootNodes).Node);
    }

    [Fact]
    public void RemovingDeeplyNestedNode_RemovesItFromExplicitSearchScope()
    {
        var root = new MediaNode("Root");
        var child = new MediaNode("Child");
        var grandchild = new MediaNode("Grandchild");
        root.Children.Add(child);
        child.Children.Add(grandchild);

        var roots = new ObservableCollection<MediaNode> { root };
        using var viewModel = new SearchAreaViewModel(roots, new RecordingErrorService());
        viewModel.ApplyScopeSelection([grandchild.Id]);

        Assert.Equal(1, viewModel.SelectedScopeCount);

        child.Children.Remove(grandchild);

        Assert.Equal(0, viewModel.SelectedScopeCount);
    }

    [Fact]
    public void NewlyAddedNestedSubtree_IsObservedRecursively()
    {
        var root = new MediaNode("Root");
        var child = new MediaNode("Child");
        root.Children.Add(child);

        var roots = new ObservableCollection<MediaNode> { root };
        using var viewModel = new SearchAreaViewModel(roots, new RecordingErrorService());

        var grandchild = new MediaNode("Grandchild");
        var greatGrandchild = new MediaNode("Great-grandchild");
        grandchild.Children.Add(greatGrandchild);
        child.Children.Add(grandchild);
        viewModel.ApplyScopeSelection([greatGrandchild.Id]);

        Assert.Equal(1, viewModel.SelectedScopeCount);

        grandchild.Children.Remove(greatGrandchild);

        Assert.Equal(0, viewModel.SelectedScopeCount);
    }
}
