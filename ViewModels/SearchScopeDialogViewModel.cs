using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class SearchScopeDialogViewModel : ViewModelBase
{
    private const int MaximumTreeDepth = 256;
    private readonly HashSet<string> _initialSelectedIds;
    public ObservableCollection<SearchScopeNode> RootNodes { get; } = new();
    public string? ErrorMessage { get; private set; }
    public bool HasBuildFailure => ErrorMessage != null;

    [ObservableProperty]
    private string _filterText = string.Empty;

    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearAllCommand { get; }
    public IRelayCommand ExpandAllCommand { get; }
    public IRelayCommand CollapseAllCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<bool>? RequestClose;

    public SearchScopeDialogViewModel(IEnumerable<MediaNode>? rootNodes, IReadOnlyCollection<string>? selectedIds,
        IErrorService errorService)
    {
        ArgumentNullException.ThrowIfNull(errorService);
        _initialSelectedIds = new HashSet<string>(selectedIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        var failureReason = "UnexpectedFailure";
        try
        {
            var roots = BuildNodes(rootNodes, out failureReason);
            foreach (var node in roots)
            {
                node.IsExpanded = true;
                node.ApplyInitialSelection(_initialSelectedIds);
            }
            foreach (var node in roots) RootNodes.Add(node);
        }
        catch (Exception ex)
        {
            RootNodes.Clear();
            ErrorMessage = T("SearchScope.BuildFailed",
                "The search categories could not be loaded. Your previous selection has been preserved. Close this dialog and check the diagnostic log.");
            try
            {
                errorService.Report(new ErrorReport("SearchScope.BuildFailed", ErrorSeverity.Error,
                    nameof(SearchScopeDialogViewModel), "The search scope tree could not be built.",
                    Context: new Dictionary<string, string>
                    {
                        ["Operation"] = "BuildTree", ["Reason"] = failureReason,
                        ["FailureType"] = ex.GetType().Name, ["MaximumDepth"] = "256"
                    }));
            }
            catch { /* Diagnostics must not prevent closing an unusable scope dialog. */ }
        }

        SelectAllCommand = new RelayCommand(SelectAll, () => !HasBuildFailure);
        ClearAllCommand = new RelayCommand(ClearAll, () => !HasBuildFailure);
        ExpandAllCommand = new RelayCommand(() => SetExpanded(true), () => !HasBuildFailure);
        CollapseAllCommand = new RelayCommand(() => SetExpanded(false), () => !HasBuildFailure);
        ApplyCommand = new RelayCommand(() =>
        {
            if (!HasBuildFailure) RequestClose?.Invoke(true);
        }, () => !HasBuildFailure);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public HashSet<string> GetSelectedNodeIds()
    {
        if (HasBuildFailure) return new HashSet<string>(_initialSelectedIds, StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in RootNodes)
            CollectSelected(root, result);
        return result;
    }

    private void SelectAll()
    {
        foreach (var root in RootNodes)
            root.SetCheckedRecursive(true);
    }

    private void ClearAll()
    {
        foreach (var root in RootNodes)
            root.SetCheckedRecursive(false);
    }

    private void SetExpanded(bool expanded)
    {
        foreach (var root in RootNodes)
            SetExpandedRecursive(root, expanded);
    }

    private static void SetExpandedRecursive(SearchScopeNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children)
            SetExpandedRecursive(child, expanded);
    }

    private static List<SearchScopeNode> BuildNodes(IEnumerable<MediaNode>? rootNodes, out string failureReason)
    {
        failureReason = "UnexpectedFailure";
        var roots = new List<SearchScopeNode>();
        var pending = new Stack<(MediaNode Node, SearchScopeNode? Parent, int Depth)>();
        var sourceRoots = rootNodes?.ToArray() ?? Array.Empty<MediaNode>();
        for (var i = sourceRoots.Length - 1; i >= 0; i--)
            pending.Push((sourceRoots[i], null, 0));

        var visited = new HashSet<MediaNode>(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out var entry))
        {
            if (entry.Node == null || entry.Depth > MaximumTreeDepth || !visited.Add(entry.Node))
            {
                failureReason = entry.Node == null ? "MissingNode"
                    : entry.Depth > MaximumTreeDepth ? "MaximumDepthExceeded" : "CycleOrSharedReference";
                throw new InvalidOperationException("Invalid search scope hierarchy.");
            }
            var node = new SearchScopeNode(entry.Node, entry.Parent);
            if (entry.Parent == null) roots.Add(node);
            else entry.Parent.Children.Add(node);
            for (var i = entry.Node.Children.Count - 1; i >= 0; i--)
                pending.Push((entry.Node.Children[i], node, entry.Depth + 1));
        }
        return roots;
    }

    private void ApplyFilter()
    {
        var filter = FilterText?.Trim();
        if (string.IsNullOrEmpty(filter))
        {
            foreach (var root in RootNodes)
                SetVisibilityRecursive(root, true);
            return;
        }

        foreach (var root in RootNodes)
            ApplyFilterRecursive(root, filter);
    }

    private static void SetVisibilityRecursive(SearchScopeNode node, bool isVisible)
    {
        node.IsVisible = isVisible;
        foreach (var child in node.Children)
            SetVisibilityRecursive(child, isVisible);
    }

    private static bool ApplyFilterRecursive(SearchScopeNode node, string filter)
    {
        var selfMatch = node.DisplayName?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        var childMatch = false;

        foreach (var child in node.Children)
        {
            if (ApplyFilterRecursive(child, filter))
                childMatch = true;
        }

        var visible = selfMatch || childMatch;
        node.IsVisible = visible;

        if (childMatch)
            node.IsExpanded = true;

        return visible;
    }

    private static void CollectSelected(SearchScopeNode node, HashSet<string> result)
    {
        if (node.IsChecked == true)
        {
            result.Add(node.Node.Id);
            return;
        }

        foreach (var child in node.Children)
            CollectSelected(child, result);
    }
}
