using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Models;

namespace Retromind.ViewModels;

public class SearchScopeNode : ObservableObject
{
    public MediaNode Node { get; }
    public SearchScopeNode? Parent { get; }
    public ObservableCollection<SearchScopeNode> Children { get; } = new();

    private bool? _isChecked;
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            var normalized = value;

            // UX tweak: when a partially-selected node is clicked,
            // interpret it as "clear all" instead of "select all".
            if (_isChecked == null && value == true)
                normalized = false;
            // Prevent manual "indeterminate" state via direct clicks.
            else if (_isChecked == true && value == null)
                normalized = false;

            SetIsChecked(normalized, updateChildren: true, updateParent: true);
        }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public string DisplayName => Node.Name;

    public SearchScopeNode(MediaNode node, SearchScopeNode? parent)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        Parent = parent;
    }

    public void SetCheckedRecursive(bool value)
        => SetIsChecked(value, updateChildren: true, updateParent: true);

    public void ApplyInitialSelection(ISet<string> selectedIds)
    {
        foreach (var child in Children)
            child.ApplyInitialSelection(selectedIds);

        if (selectedIds.Contains(Node.Id))
        {
            SetIsChecked(true, updateChildren: true, updateParent: false);
            return;
        }

        if (Children.Count == 0)
        {
            SetIsChecked(false, updateChildren: false, updateParent: false);
            return;
        }

        UpdateCheckStateFromChildren();
    }

    public void UpdateCheckStateFromChildren()
    {
        if (Children.Count == 0)
            return;

        var allTrue = Children.All(c => c.IsChecked == true);
        var allFalse = Children.All(c => c.IsChecked == false);
        var newValue = allTrue ? true : allFalse ? false : (bool?)null;

        SetIsChecked(newValue, updateChildren: false, updateParent: true);
    }

    private void SetIsChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value)
            return;

        SetProperty(ref _isChecked, value, nameof(IsChecked));

        if (updateChildren && value.HasValue)
        {
            foreach (var child in Children)
                child.SetIsChecked(value, updateChildren: true, updateParent: false);
        }

        if (updateParent)
            Parent?.UpdateCheckStateFromChildren();
    }
}
