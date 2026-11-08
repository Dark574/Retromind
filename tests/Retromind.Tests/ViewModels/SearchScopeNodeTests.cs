using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SearchScopeNodeTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    [InlineData(null, true, null)]
    [InlineData(true, null, null)]
    [InlineData(null, false, null)]
    [InlineData(false, null, null)]
    [InlineData(null, null, null)]
    public void Aggregation_PreservesPartialChildrenDuringRestorationAndLiveChanges(bool? left, bool? right, bool? expected)
    {
        var root = new MediaNode("Root");
        var first = Branch("First");
        var second = Branch("Second");
        root.Children.Add(first);
        root.Children.Add(second);
        var selected = new HashSet<string>();
        AddSelection(first, left);
        AddSelection(second, right);
        var errors = new RecordingErrorService();
        var vm = new SearchScopeDialogViewModel([root], selected, errors);
        var scope = Assert.Single(vm.RootNodes);

        AssertStates();
        var restoredIds = vm.GetSelectedNodeIds();
        scope.SetCheckedRecursive(false);
        SetState(scope.Children[0], left);
        SetState(scope.Children[1], right);
        AssertStates();
        Assert.True(restoredIds.SetEquals(vm.GetSelectedNodeIds()));
        Assert.Empty(errors.Reports);

        void AddSelection(MediaNode node, bool? state)
        {
            if (state == true) selected.Add(node.Id);
            else if (state == null) selected.Add(node.Children[0].Id);
        }
        void AssertStates()
        {
            Assert.Equal(left, scope.Children[0].IsChecked);
            Assert.Equal(right, scope.Children[1].IsChecked);
            Assert.Equal(expected, scope.IsChecked);
            var ids = new HashSet<string>();
            if (expected == true) ids.Add(root.Id);
            else if (expected == null)
            {
                if (left == true) ids.Add(first.Id);
                else if (left == null) ids.Add(first.Children[0].Id);
                if (right == true) ids.Add(second.Id);
                else if (right == null) ids.Add(second.Children[0].Id);
            }
            Assert.True(ids.SetEquals(vm.GetSelectedNodeIds()));
        }
    }

    [Fact]
    public void InitialSelection_CanBeReappliedToAnAlreadySelectedSubtree()
    {
        var root = Branch("Root");
        var vm = new SearchScopeDialogViewModel([root], [root.Id], new RecordingErrorService());
        var scope = Assert.Single(vm.RootNodes);
        scope.ApplyInitialSelection(new HashSet<string> { root.Children[0].Id });
        Assert.Null(scope.IsChecked);
        Assert.True(scope.Children[0].IsChecked);
        Assert.False(scope.Children[1].IsChecked);
        scope.ApplyInitialSelection(new HashSet<string> { root.Id });
        Assert.True(scope.IsChecked);
        Assert.All(scope.Children, child => Assert.True(child.IsChecked));
        scope.ApplyInitialSelection(new HashSet<string>());
        Assert.False(scope.IsChecked);
        Assert.All(scope.Children, child => Assert.False(child.IsChecked));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursiveSelection_SynchronizesDescendantsEvenWhenParentAlreadyHasRequestedValue(bool value)
    {
        var scope = new SearchScopeNode(new MediaNode("Root"), null);
        scope.SetCheckedRecursive(value);
        var added = new SearchScopeNode(new MediaNode("New child"), scope);
        added.SetCheckedRecursive(!value);
        scope.Children.Add(added);
        var notifications = 0;
        scope.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(scope.IsChecked)) notifications++; };

        scope.SetCheckedRecursive(value);
        scope.SetCheckedRecursive(value);

        Assert.Equal(value, scope.IsChecked);
        Assert.Equal(value, added.IsChecked);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void PartialCheckboxClick_ClearsItsSubtreeAndKeepsUnrelatedSelection()
    {
        var root = new MediaNode("Root");
        var branch = Branch("Branch");
        var sibling = new MediaNode("Sibling");
        root.Children.Add(branch);
        root.Children.Add(sibling);
        var vm = new SearchScopeDialogViewModel([root], [branch.Children[0].Id, sibling.Id], new RecordingErrorService());
        var scope = Assert.Single(vm.RootNodes);
        Assert.Null(scope.Children[0].IsChecked);

        scope.Children[0].IsChecked = true; // Avalonia click transition from indeterminate.

        Assert.False(scope.Children[0].IsChecked);
        Assert.All(scope.Children[0].Children, child => Assert.False(child.IsChecked));
        Assert.True(scope.Children[1].IsChecked);
        Assert.Null(scope.IsChecked);
        Assert.True(vm.GetSelectedNodeIds().SetEquals([sibling.Id]));
        scope.IsChecked = true; // The same click on the partially selected root clears everything.
        Assert.False(scope.IsChecked);
        Assert.Empty(vm.GetSelectedNodeIds());
        scope.IsChecked = true; // An unchecked checkbox still selects the full subtree.
        Assert.True(scope.IsChecked);
        scope.IsChecked = null; // Existing checked -> indeterminate click means clear.
        Assert.False(scope.IsChecked);
        Assert.Empty(vm.GetSelectedNodeIds());
    }

    private static MediaNode Branch(string name)
    {
        var node = new MediaNode(name);
        node.Children.Add(new MediaNode(name + " 1"));
        node.Children.Add(new MediaNode(name + " 2"));
        return node;
    }

    private static void SetState(SearchScopeNode node, bool? state)
    {
        node.SetCheckedRecursive(state == true);
        if (state == null) node.Children[0].SetCheckedRecursive(true);
    }
}
