using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class SearchScopeDialogViewModel : ViewModelBase
{
    public ObservableCollection<SearchScopeNode> RootNodes { get; } = new();

    [ObservableProperty]
    private string _filterText = string.Empty;

    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearAllCommand { get; }
    public IRelayCommand ExpandAllCommand { get; }
    public IRelayCommand CollapseAllCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<bool>? RequestClose;

    public SearchScopeDialogViewModel(IEnumerable<MediaNode> rootNodes, IReadOnlyCollection<string> selectedIds)
    {
        var selected = new HashSet<string>(selectedIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        if (rootNodes != null)
        {
            foreach (var root in rootNodes)
            {
                var node = BuildNode(root, null);
                node.IsExpanded = true;
                RootNodes.Add(node);
                node.ApplyInitialSelection(selected);
            }
        }

        SelectAllCommand = new RelayCommand(SelectAll);
        ClearAllCommand = new RelayCommand(ClearAll);
        ExpandAllCommand = new RelayCommand(() => SetExpanded(true));
        CollapseAllCommand = new RelayCommand(() => SetExpanded(false));
        ApplyCommand = new RelayCommand(() => RequestClose?.Invoke(true));
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    public HashSet<string> GetSelectedNodeIds()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in RootNodes)
            CollectSelected(root, result);
        return result;
    }

    private void SelectAll()
    {
        foreach (var root in RootNodes)
            root.IsChecked = true;
    }

    private void ClearAll()
    {
        foreach (var root in RootNodes)
            root.IsChecked = false;
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

    private static SearchScopeNode BuildNode(MediaNode node, SearchScopeNode? parent)
    {
        var scopeNode = new SearchScopeNode(node, parent);
        foreach (var child in node.Children)
            scopeNode.Children.Add(BuildNode(child, scopeNode));

        return scopeNode;
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
