using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// Provides a non-persisted, shallow view into the real library tree for the
/// BigMode Home row. It never changes nodes or the regular BigMode navigation.
/// </summary>
public sealed class BigModeHomeLibraryNavigator
{
    private readonly IReadOnlyList<MediaNode> _roots;
    private readonly List<MediaNode> _path = [];
    private readonly bool _parentalFilterActive;
    private readonly string _libraryTitle;
    private readonly string _gameCountLabel;
    private readonly string _backLabel;
    private readonly string _openLabel;
    private readonly string _overviewLabel;

    public BigModeHomeLibraryNavigator(
        IReadOnlyList<MediaNode> visibleRoots,
        bool parentalFilterActive,
        string libraryTitle,
        string gameCountLabel,
        string backLabel,
        string openLabel,
        string overviewLabel,
        MediaNode? initialNode = null)
    {
        ArgumentNullException.ThrowIfNull(visibleRoots);

        _roots = visibleRoots;
        _parentalFilterActive = parentalFilterActive;
        _libraryTitle = libraryTitle;
        _gameCountLabel = gameCountLabel;
        _backLabel = backLabel;
        _openLabel = openLabel;
        _overviewLabel = overviewLabel;

        if (initialNode != null && TryFindPath(initialNode, out var initialPath))
            _path.AddRange(initialPath);
        else if (_roots.Count == 1)
            _path.Add(_roots[0]);
    }

    public MediaNode? CurrentNode => _path.Count > 0 ? _path[^1] : null;

    public bool CanGoBack => _roots.Count == 1 ? _path.Count > 1 : _path.Count > 0;

    public string Breadcrumb
    {
        get
        {
            if (_path.Count == 0)
                return _libraryTitle;

            var names = _path.Select(node => node.Name).ToList();
            if (!string.Equals(names[0], _libraryTitle, StringComparison.CurrentCultureIgnoreCase))
                names.Insert(0, _libraryTitle);

            return string.Join("  ›  ", names);
        }
    }

    public ObservableCollection<BigModeHomeEntryViewModel> BuildEntries()
    {
        var entries = new ObservableCollection<BigModeHomeEntryViewModel>();

        var current = CurrentNode;
        entries.Add(new BigModeHomeEntryViewModel
        {
            Title = current?.Name ?? _libraryTitle,
            Subtitle = current == null
                ? _overviewLabel
                : $"{CountVisibleItems(current):N0} {_gameCountLabel}",
            CoverPath = current?.PrimaryCoverAbsolutePath,
            Node = current,
            SourceNode = current,
            Kind = BigModeHomeEntryKind.LibraryCurrent,
            HasChildren = GetVisibleChildren(current).Count > 0,
            NavigationBadge = current == null ? null : _openLabel
        });

        foreach (var child in GetVisibleChildren(current))
        {
            var hasChildren = GetVisibleChildren(child).Count > 0;
            entries.Add(new BigModeHomeEntryViewModel
            {
                Title = child.Name,
                Subtitle = $"{CountVisibleItems(child):N0} {_gameCountLabel}",
                CoverPath = child.PrimaryCoverAbsolutePath,
                Node = child,
                SourceNode = child,
                Kind = BigModeHomeEntryKind.LibraryChild,
                HasChildren = hasChildren,
                NavigationBadge = hasChildren ? "›" : null
            });
        }

        // Keep the current node as the stable left anchor. The Back card lives
        // at the end, but is still one Left press away thanks to row wrapping.
        if (CanGoBack)
        {
            entries.Add(new BigModeHomeEntryViewModel
            {
                Title = _backLabel,
                Subtitle = GetParentTitle(),
                Kind = BigModeHomeEntryKind.LibraryBack,
                NavigationBadge = "←"
            });
        }

        return entries;
    }

    public bool DrillInto(MediaNode node)
    {
        if (!GetVisibleChildren(CurrentNode).Contains(node) || GetVisibleChildren(node).Count == 0)
            return false;

        _path.Add(node);
        return true;
    }

    public bool GoBack()
    {
        if (!CanGoBack)
            return false;

        _path.RemoveAt(_path.Count - 1);
        return true;
    }

    private IReadOnlyList<MediaNode> GetVisibleChildren(MediaNode? node) =>
        (node?.Children ?? _roots)
        .Where(child => !_parentalFilterActive || ShouldShowNode(child))
        .ToArray();

    private string GetParentTitle()
    {
        if (_path.Count <= 1)
            return _libraryTitle;

        return _path[^2].Name;
    }

    private int CountVisibleItems(MediaNode node)
    {
        var directCount = node.Items.Count(item =>
            !_parentalFilterActive || !item.IsProtected);
        return directCount + GetVisibleChildren(node).Sum(CountVisibleItems);
    }

    private bool TryFindPath(MediaNode target, out IReadOnlyList<MediaNode> path)
    {
        foreach (var root in _roots)
        {
            var candidate = new List<MediaNode>();
            if (TryFindPath(root, target, candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = [];
        return false;
    }

    private bool TryFindPath(MediaNode node, MediaNode target, ICollection<MediaNode> path)
    {
        if (_parentalFilterActive && !ShouldShowNode(node))
            return false;

        path.Add(node);
        if (ReferenceEquals(node, target))
            return true;

        foreach (var child in node.Children)
        {
            if (TryFindPath(child, target, path))
                return true;
        }

        path.Remove(node);
        return false;
    }

    private static bool ShouldShowNode(MediaNode node)
    {
        if (node.Children.Count == 0 && node.Items.Count == 0)
            return true;

        if (node.Items.Any(item => !item.IsProtected))
            return true;

        return node.Children.Any(ShouldShowNode);
    }
}
