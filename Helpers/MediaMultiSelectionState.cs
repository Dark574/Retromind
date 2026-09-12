using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Holds transient item selections for bulk actions without modifying persisted media data.
/// </summary>
public sealed class MediaMultiSelectionState : ObservableObject
{
    private readonly HashSet<string> _selectedIds = new(StringComparer.Ordinal);
    private int _version;

    public int Count => _selectedIds.Count;
    public bool HasSelection => _selectedIds.Count > 0;

    /// <summary>
    /// Changes whenever the selection changes so tile bindings can refresh efficiently.
    /// </summary>
    public int Version => _version;

    public bool Contains(MediaItem item) => _selectedIds.Contains(item.Id);

    public bool Toggle(MediaItem item)
    {
        var isSelected = _selectedIds.Add(item.Id);
        if (!isSelected)
            _selectedIds.Remove(item.Id);

        NotifySelectionChanged();
        return isSelected;
    }

    public void Clear()
    {
        if (_selectedIds.Count == 0)
            return;

        _selectedIds.Clear();
        NotifySelectionChanged();
    }

    public void ReplaceWith(IEnumerable<MediaItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var selectedIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (_selectedIds.SetEquals(selectedIds))
            return;

        _selectedIds.Clear();
        _selectedIds.UnionWith(selectedIds);
        NotifySelectionChanged();
    }

    public IReadOnlyList<MediaItem> ResolveFrom(IEnumerable<MediaItem> items) =>
        items.Where(Contains).ToList();

    private void NotifySelectionChanged()
    {
        _version++;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Version));
    }
}
