using System.Text.Json;
using Retromind.Models;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SearchScopeDialogViewModelTests
{
    [Fact]
    public void DeepMixedSelection_RoundTripsWithoutIncludingUnselectedAncestorsOrSiblings()
    {
        var root = new MediaNode("Root");
        var child = new MediaNode("Child");
        var grandchild = new MediaNode("Grandchild");
        var leaf = new MediaNode("Leaf");
        root.Children.Add(child);
        root.Children.Add(new MediaNode("Root sibling"));
        child.Children.Add(grandchild);
        child.Children.Add(new MediaNode("Child sibling"));
        grandchild.Children.Add(leaf);
        grandchild.Children.Add(new MediaNode("Leaf sibling"));
        var errors = new RecordingErrorService();
        var vm = new SearchScopeDialogViewModel([root], [leaf.Id, "removed-node-id"], errors);
        var scope = Assert.Single(vm.RootNodes);

        Assert.Null(scope.IsChecked);
        Assert.Null(scope.Children[0].IsChecked);
        Assert.Null(scope.Children[0].Children[0].IsChecked);
        Assert.True(vm.GetSelectedNodeIds().SetEquals([leaf.Id]));
        var reopened = new SearchScopeDialogViewModel([root], vm.GetSelectedNodeIds(), errors);
        Assert.True(reopened.GetSelectedNodeIds().SetEquals([leaf.Id]));
        Assert.Null(reopened.RootNodes[0].IsChecked);
        var exposedIds = reopened.GetSelectedNodeIds();
        exposedIds.Clear();
        Assert.True(reopened.GetSelectedNodeIds().SetEquals([leaf.Id]));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public void FullSubtreeSelection_DoesNotChangeItsSiblingAndReturnsTheSelectedBranchId()
    {
        var root = new MediaNode("Root");
        var branch = new MediaNode("Branch");
        branch.Children.Add(new MediaNode("Leaf"));
        root.Children.Add(branch);
        root.Children.Add(new MediaNode("Sibling"));
        var vm = new SearchScopeDialogViewModel([root], [], new RecordingErrorService());
        var scope = vm.RootNodes[0];
        scope.Children[0].SetCheckedRecursive(true);
        Assert.Null(scope.IsChecked);
        Assert.True(scope.Children[0].Children[0].IsChecked);
        Assert.False(scope.Children[1].IsChecked);
        Assert.True(vm.GetSelectedNodeIds().SetEquals([branch.Id]));
        scope.Children[0].SetCheckedRecursive(false);
        Assert.False(scope.IsChecked);
        Assert.Empty(vm.GetSelectedNodeIds());
    }

    [Fact]
    public void FilterAndCommands_KeepHiddenSelectionsAndExistingOrder()
    {
        var first = new MediaNode("First");
        var shown = new MediaNode("Visible match");
        var hidden = new MediaNode("Hidden selection");
        first.Children.Add(shown);
        first.Children.Add(hidden);
        var second = new MediaNode("Second");
        var errors = new RecordingErrorService();
        var vm = new SearchScopeDialogViewModel([first, second], [hidden.Id, second.Id], errors);
        var before = vm.GetSelectedNodeIds();
        vm.FilterText = "  MATCH  ";
        Assert.True(vm.RootNodes[0].IsVisible);
        Assert.True(vm.RootNodes[0].Children[0].IsVisible);
        Assert.False(vm.RootNodes[0].Children[1].IsVisible);
        Assert.False(vm.RootNodes[1].IsVisible);
        Assert.True(before.SetEquals(vm.GetSelectedNodeIds()));

        vm.CollapseAllCommand.Execute(null);
        Assert.All(Nodes(vm), node => Assert.False(node.IsExpanded));
        vm.ExpandAllCommand.Execute(null);
        Assert.All(Nodes(vm), node => Assert.True(node.IsExpanded));
        Assert.True(before.SetEquals(vm.GetSelectedNodeIds()));
        vm.SelectAllCommand.Execute(null);
        Assert.All(Nodes(vm), node => Assert.True(node.IsChecked));
        Assert.True(vm.GetSelectedNodeIds().SetEquals([first.Id, second.Id]));
        vm.ClearAllCommand.Execute(null);
        Assert.All(Nodes(vm), node => Assert.False(node.IsChecked));
        Assert.Empty(vm.GetSelectedNodeIds());
        vm.FilterText = "no match";
        Assert.All(Nodes(vm), node => Assert.False(node.IsVisible));
        vm.FilterText = "";
        Assert.All(Nodes(vm), node => Assert.True(node.IsVisible));
        Assert.Equal([first, second], vm.RootNodes.Select(node => node.Node));
        Assert.Equal([shown, hidden], vm.RootNodes[0].Children.Select(node => node.Node));
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData("cycle", "CycleOrSharedReference")]
    [InlineData("shared", "CycleOrSharedReference")]
    [InlineData("shared-root", "CycleOrSharedReference")]
    [InlineData("depth", "MaximumDepthExceeded")]
    [InlineData("missing-child", "MissingNode")]
    [InlineData("missing-root", "MissingNode")]
    public void InvalidHierarchy_IsRejectedWithoutPartialTreeOrSelectionLossAndReportsOnce(string invalid, string reason)
    {
        var validRoot = new MediaNode("Valid root");
        var invalidRoot = new MediaNode("token=do-not-log-node-names");
        var roots = new List<MediaNode> { validRoot, invalidRoot };
        if (invalid == "cycle") invalidRoot.Children.Add(invalidRoot);
        else if (invalid == "shared")
        {
            var child = new MediaNode("Shared");
            invalidRoot.Children.Add(child);
            invalidRoot.Children.Add(child);
        }
        else if (invalid == "shared-root") roots.Add(validRoot);
        else if (invalid == "depth")
        {
            var current = invalidRoot;
            for (var i = 0; i < 257; i++)
            {
                var child = new MediaNode("Child");
                current.Children.Add(child);
                current = child;
            }
        }
        else if (invalid == "missing-child") invalidRoot.Children.Add(null!);
        else roots.Add(null!);
        var errors = new RecordingErrorService();
        var selected = new[] { validRoot.Id, invalidRoot.Id, "previous-selection" };
        var vm = new SearchScopeDialogViewModel(roots, selected, errors);

        Assert.True(vm.HasBuildFailure);
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage));
        Assert.Empty(vm.RootNodes);
        Assert.True(vm.GetSelectedNodeIds().SetEquals(selected));
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.False(vm.SelectAllCommand.CanExecute(null));
        Assert.False(vm.ClearAllCommand.CanExecute(null));
        bool? closed = null;
        vm.RequestClose += result => closed = result;
        vm.ApplyCommand.Execute(null);
        Assert.Null(closed);
        vm.FilterText = "anything";
        vm.ClearAllCommand.Execute(null);
        vm.GetSelectedNodeIds().Clear();
        Assert.True(vm.GetSelectedNodeIds().SetEquals(selected));
        vm.CancelCommand.Execute(null);
        Assert.False(closed);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("SearchScope.BuildFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Equal(nameof(SearchScopeDialogViewModel), report.Component);
        Assert.Equal(reason, report.Context!["Reason"]);
        Assert.Equal("BuildTree", report.Context["Operation"]);
        Assert.Null(report.Exception);
        Assert.DoesNotContain("do-not-log", JsonSerializer.Serialize(report));
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public void UnexpectedEnumerationFailureAndFailingDiagnostics_StillAllowCancellationWithoutApplyingPartialTree()
    {
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var vm = new SearchScopeDialogViewModel(FailingRoots(), ["previous-selection"], errors);
        Assert.True(vm.HasBuildFailure);
        Assert.Empty(vm.RootNodes);
        Assert.True(vm.GetSelectedNodeIds().SetEquals(["previous-selection"]));
        Assert.False(vm.ApplyCommand.CanExecute(null));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("UnexpectedFailure", report.Context!["Reason"]);
        Assert.Equal(nameof(IOException), report.Context["FailureType"]);
        Assert.DoesNotContain("do-not-log", JsonSerializer.Serialize(report));
        bool? closed = null;
        vm.RequestClose += result => closed = result;
        vm.CancelCommand.Execute(null);
        Assert.False(closed);

        static IEnumerable<MediaNode> FailingRoots()
        {
            yield return new MediaNode("Valid first root");
            throw new IOException("token=do-not-log-exception-messages");
        }
    }

    [Fact]
    public void Depth256_RemainsUsableForAllDialogTraversals()
    {
        var root = new MediaNode("Root");
        var current = root;
        for (var i = 0; i < 256; i++)
        {
            var child = new MediaNode("Child");
            current.Children.Add(child);
            current = child;
        }
        var errors = new RecordingErrorService();
        var vm = new SearchScopeDialogViewModel([root], [current.Id], errors);
        Assert.False(vm.HasBuildFailure);
        Assert.True(vm.GetSelectedNodeIds().SetEquals([root.Id])); // Every branch has only one child.
        vm.FilterText = "Child";
        vm.ExpandAllCommand.Execute(null);
        vm.CollapseAllCommand.Execute(null);
        vm.ClearAllCommand.Execute(null);
        Assert.Empty(vm.GetSelectedNodeIds());
        vm.SelectAllCommand.Execute(null);
        Assert.All(Nodes(vm), node => Assert.True(node.IsChecked));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public void EmptyTree_AllowsExplicitEmptySelectionAndNormalApplyOrCancel()
    {
        var errors = new RecordingErrorService();
        var vm = new SearchScopeDialogViewModel(null, null, errors);
        vm.SelectAllCommand.Execute(null);
        vm.ClearAllCommand.Execute(null);
        Assert.Empty(vm.GetSelectedNodeIds());
        Assert.True(vm.ApplyCommand.CanExecute(null));
        var results = new List<bool>();
        vm.RequestClose += results.Add;
        vm.ApplyCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        Assert.Equal([true, false], results);
        Assert.Empty(errors.Reports);
    }

    private static IEnumerable<SearchScopeNode> Nodes(SearchScopeDialogViewModel vm)
    {
        var pending = new Stack<SearchScopeNode>(vm.RootNodes);
        while (pending.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children) pending.Push(child);
        }
    }
}
