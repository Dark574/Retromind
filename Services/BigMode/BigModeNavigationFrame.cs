using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Retromind.Models;

namespace Retromind.Services.BigMode;

public readonly record struct BigModeNavigationSelection(BigModeNavigationKey? Key, int Index);

/// <summary>
/// One opened library level. Only the session can mutate its selection or entries.
/// An entry list and its index map remain stable throughout ordinary selection changes.
/// </summary>
public sealed class BigModeNavigationFrame
{
    private readonly Dictionary<BigModeNavigationKey, int> _indices = [];
    private ReadOnlyCollection<BigModeNavigationEntry> _entries = new List<BigModeNavigationEntry>().AsReadOnly();

    internal BigModeNavigationFrame(BigModeLibrarySnapshot snapshot,
        BigModeLibrarySnapshot.NodeInfo? owner, BigModeNavigationSelection selection)
    {
        OpenedNodeKey = owner == null ? null : new(BigModeNavigationEntryKind.Node, owner.Id);
        Update(snapshot, owner, selection);
    }

    public BigModeNavigationKey? OpenedNodeKey { get; }
    public MediaNode? OpenedNode { get; private set; }
    public IReadOnlyList<BigModeNavigationEntry> Entries => _entries;
    public int SelectedIndex { get; private set; } = -1;
    public BigModeNavigationEntry? SelectedEntry => SelectedIndex >= 0 ? _entries[SelectedIndex] : null;
    public BigModeNavigationSelection Selection => new(SelectedEntry?.Key, SelectedIndex);

    internal void Update(BigModeLibrarySnapshot snapshot, BigModeLibrarySnapshot.NodeInfo? owner,
        BigModeNavigationSelection selection)
    {
        OpenedNode = owner?.Node;
        var entries = snapshot.BuildEntries(owner);
        var unchanged = entries.Count == _entries.Count;
        for (var i = 0; unchanged && i < entries.Count; i++)
        {
            unchanged = (entries[i], _entries[i]) switch
            {
                (BigModeNodeNavigationEntry a, BigModeNodeNavigationEntry b) =>
                    a.Key == b.Key && ReferenceEquals(a.Node, b.Node) && ReferenceEquals(a.ParentNode, b.ParentNode),
                (BigModeMediaNavigationEntry a, BigModeMediaNavigationEntry b) =>
                    a.Key == b.Key && ReferenceEquals(a.Item, b.Item) && ReferenceEquals(a.SourceNode, b.SourceNode),
                _ => false
            };
        }
        if (!unchanged)
        {
            _entries = entries.AsReadOnly();
            _indices.Clear();
            for (var i = 0; i < entries.Count; i++) _indices.Add(entries[i].Key, i);
        }

        RestoreSelection(selection);
    }

    internal void RestoreSelection(BigModeNavigationSelection selection)
    {
        if (selection.Key is not { } key || !TrySelect(key, selection.Index))
            SelectedIndex = _entries.Count == 0 ? -1 : Math.Clamp(selection.Index, 0, _entries.Count - 1);
    }

    internal bool TrySelect(BigModeNavigationKey key, int indexHint)
    {
        if (indexHint >= 0 && indexHint < _entries.Count && _entries[indexHint].Key == key)
        {
            SelectedIndex = indexHint;
            return true;
        }
        if (!_indices.TryGetValue(key, out var index)) return false;
        SelectedIndex = index;
        return true;
    }

    internal bool TrySelect(int index)
    {
        if (index < 0 || index >= _entries.Count) return false;
        SelectedIndex = index;
        return true;
    }
}
