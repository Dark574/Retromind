using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Retromind.Models;
using Retromind.Services.BigMode;

namespace Retromind.ViewModels;

/// <summary>
/// Provides a non-persisted, shallow view into the real library tree for the
/// BigMode Home row. It never changes nodes or the regular BigMode navigation.
/// </summary>
public sealed class BigModeHomeLibraryNavigator
{
    private readonly IReadOnlyList<MediaNode> _roots;
    private readonly BigModeNavigationSession _session;
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
        _session = new BigModeNavigationSession(visibleRoots, parentalFilterActive);
        _libraryTitle = libraryTitle;
        _gameCountLabel = gameCountLabel;
        _backLabel = backLabel;
        _openLabel = openLabel;
        _overviewLabel = overviewLabel;

        if (initialNode != null)
            _session.TryOpenNode(new(BigModeNavigationEntryKind.Node, initialNode.Id));
        else if (_roots.Count == 1)
            _session.TryOpenNode(new(BigModeNavigationEntryKind.Node, _roots[0].Id));
    }

    public MediaNode? CurrentNode => _session.Current.OpenedNode;
    public bool CanGoBack => _session.Depth > (_roots.Count == 1 ? 1 : 0);

    public string Breadcrumb
    {
        get
        {
            if (_session.Depth == 0)
                return _libraryTitle;

            var names = _session.EnteredNodes.Select(node => node.Name).ToList();
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
                : $"{_session.GetVisibleMediaCount(current):N0} {_gameCountLabel}",
            CoverPath = current?.PrimaryCoverAbsolutePath,
            Node = current,
            SourceNode = current,
            Kind = BigModeHomeEntryKind.LibraryCurrent,
            HasChildren = _session.Current.Entries.Any(entry => entry.Kind == BigModeNavigationEntryKind.Node),
            NavigationBadge = current == null ? null : _openLabel
        });

        foreach (var entry in _session.Current.Entries)
        {
            if (entry is BigModeNodeNavigationEntry child)
            {
                entries.Add(new BigModeHomeEntryViewModel
                {
                    Title = child.Title,
                    Subtitle = $"{_session.GetVisibleMediaCount(child.Node):N0} {_gameCountLabel}",
                    CoverPath = child.CoverPath,
                    Node = child.Node,
                    SourceNode = child.Node,
                    Kind = BigModeHomeEntryKind.LibraryChild,
                    HasChildren = child.Node.Children.Count > 0,
                    NavigationBadge = "›"
                });
            }
            else if (entry is BigModeMediaNavigationEntry media)
            {
                entries.Add(new BigModeHomeEntryViewModel
                {
                    Title = media.Title,
                    Subtitle = media.SourceNode.Name,
                    CoverPath = media.CoverPath,
                    Item = media.Item,
                    SourceNode = media.SourceNode
                });
            }
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

    public bool DrillInto(MediaNode node) =>
        _session.TrySelect(new BigModeNavigationKey(BigModeNavigationEntryKind.Node, node.Id)) &&
        _session.ActivateSelected() is BigModeNavigationResult.NodeOpened;

    public bool GoBack() => CanGoBack && _session.Back() is BigModeNavigationResult.NodeOpened;

    private string GetParentTitle() => _session.Depth < 2
        ? _libraryTitle : _session.EnteredNodes[^2].Name;
}
