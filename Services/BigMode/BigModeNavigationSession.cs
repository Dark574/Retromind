using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.Services.BigMode;

/// <summary>
/// Theme-independent navigation over a validated library snapshot. Calls must be
/// serialized by the consumer. Refresh after topology, identity or protection edits;
/// ordinary selection/navigation never rescans the tree. Not wired into BigMode yet.
/// </summary>
public sealed class BigModeNavigationSession
{
    private readonly IReadOnlyList<MediaNode> _roots;
    private BigModeLibrarySnapshot _snapshot;
    private List<BigModeNavigationFrame> _frames;
    // Empty IDs are rejected by the snapshot, so this key unambiguously denotes
    // the virtual root, without creating or persisting a fake library node.
    private readonly Dictionary<string, BigModeNavigationSelection> _remembered = new(StringComparer.Ordinal);

    public BigModeNavigationSession(IReadOnlyList<MediaNode> roots, bool parentalFilterActive = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots;
        _snapshot = new BigModeLibrarySnapshot(roots, parentalFilterActive);
        _frames = [CreateFrame(null)];
    }

    public BigModeNavigationFrame Current => _frames[^1];
    public bool CanGoBack => _frames.Count > 1;
    public bool ParentalFilterActive => _snapshot.Visibility.ParentalFilterActive;
    public long LibraryVersion { get; private set; } = 1;
    public long Revision { get; private set; }
    public event EventHandler? Changed;

    public bool TrySelect(int index)
    {
        if (index < 0 || index >= Current.Entries.Count) return false;
        if (!CanSelect(Current.Entries[index])) { Refresh(); return false; }
        var previous = Current.SelectedIndex;
        Current.TrySelect(index);
        if (previous != Current.SelectedIndex) NotifyChanged();
        return true;
    }

    public bool TrySelect(BigModeNavigationKey key, int indexHint = -1)
    {
        var previous = Current.Selection;
        if (!Current.TrySelect(key, indexHint)) return false;
        if (!CanSelect(Current.SelectedEntry!))
        {
            Current.RestoreSelection(previous);
            Refresh();
            return false;
        }
        if (previous != Current.Selection) NotifyChanged();
        return true;
    }

    public bool MoveSelection(int offset)
    {
        var count = Current.Entries.Count;
        if (count == 0 || offset == 0) return false;
        var index = (int)(((long)Current.SelectedIndex + offset) % count);
        if (index < 0) index += count;
        return TrySelect(index);
    }

    public BigModeNavigationResult ActivateSelected()
    {
        if (Current.SelectedEntry is not { } entry) return new BigModeNavigationResult.NoAction();
        if (!CanSelect(entry))
        {
            // A newly protected stale selection must not activate its replacement.
            Refresh();
            return new BigModeNavigationResult.NoAction();
        }
        if (entry is BigModeMediaNavigationEntry media)
            return new BigModeNavigationResult.MediaActivationRequested(media);

        var node = (BigModeNodeNavigationEntry)entry;
        if (!_snapshot.TryGetNode(node.Key.Id, out var info) || !info.Visible)
            return new BigModeNavigationResult.NoAction();
        Remember(Current);
        _frames.Add(CreateFrame(info));
        NotifyChanged();
        return new BigModeNavigationResult.NodeOpened(Current);
    }

    public BigModeNavigationResult Back()
    {
        if (!CanGoBack) return RequestExit();
        Remember(Current);
        _frames.RemoveAt(_frames.Count - 1);
        NotifyChanged();
        return new BigModeNavigationResult.NodeOpened(Current);
    }

    public BigModeNavigationResult RequestExit() => new BigModeNavigationResult.ExitRequested();

    /// <summary>Direct jump for a future Home shortcut. Only indexed, visible nodes can be opened.</summary>
    public bool TryOpenNode(BigModeNavigationKey key)
    {
        if (key.Kind != BigModeNavigationEntryKind.Node ||
            !_snapshot.TryGetNode(key.Id, out var info) || !info.Visible) return false;
        if (Current.OpenedNodeKey == key) return true;
        MoveToPath(BigModeLibrarySnapshot.PathTo(info));
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// Reveals a real entry in its containing level without activating it. Source
    /// ownership comes from the library index, never from a caller-supplied node.
    /// </summary>
    public bool TryReveal(BigModeNavigationKey key)
    {
        BigModeLibrarySnapshot.NodeInfo? owner;
        if (key.Kind == BigModeNavigationEntryKind.Node)
        {
            if (!_snapshot.TryGetNode(key.Id, out var node) || !node.Visible) return false;
            owner = node.Parent;
        }
        else if (key.Kind == BigModeNavigationEntryKind.MediaItem)
        {
            if (!_snapshot.TryGetMedia(key.Id, out var media) || !media.Visible ||
                !_snapshot.Visibility.IsItemVisible(media.Item)) return false;
            owner = media.Owner;
        }
        else return false;

        if (Current.OpenedNodeKey?.Id == owner?.Id) return TrySelect(key);
        MoveToPath(BigModeLibrarySnapshot.PathTo(owner));
        var selected = Current.TrySelect(key, -1);
        NotifyChanged();
        return selected;
    }

    public BigModeNavigationState CaptureState()
    {
        var path = new List<string>(_frames.Count - 1);
        for (var i = 1; i < _frames.Count; i++) path.Add(_frames[i].OpenedNodeKey!.Value.Id);
        return new BigModeNavigationState(path, Current.Selection.Key, Current.SelectedIndex);
    }

    /// <summary>
    /// Restores the deepest valid visible prefix. False means recovery was needed,
    /// not that the session was left in an inconsistent or partially applied state.
    /// </summary>
    public bool RestoreState(BigModeNavigationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = new List<BigModeLibrarySnapshot.NodeInfo>();
        BigModeLibrarySnapshot.NodeInfo? parent = null;
        foreach (var id in state.EnteredNodeIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !_snapshot.TryGetNode(id, out var node) ||
                !node.Visible || !ReferenceEquals(node.Parent, parent)) break;
            path.Add(node);
            parent = node;
        }
        var exactPath = path.Count == state.EnteredNodeIds.Count;
        MoveToPath(path);
        // A selection belongs to its recorded location, not to a recovered ancestor.
        if (exactPath)
            Current.RestoreSelection(new(state.SelectedKey, state.SelectedIndexHint));
        if (Current.SelectedEntry is { } entry && !CanSelect(entry)) Refresh();
        else NotifyChanged();
        return exactPath && Current.Selection.Key == state.SelectedKey;
    }

    /// <summary>
    /// Explicit edit boundary. Reindexes once, refreshes retained frames, preserves
    /// keys across reordering/replacement and returns to a valid ancestor after moves.
    /// Collection/protection observers will call this from the Stage B adapter.
    /// </summary>
    public void Refresh(bool? parentalFilterActive = null)
    {
        // Validate first: malformed edits must not partially replace a live session.
        var snapshot = new BigModeLibrarySnapshot(_roots, parentalFilterActive ?? ParentalFilterActive);
        Remember(Current);
        _snapshot = snapshot;
        LibraryVersion++;
        BigModeLibrarySnapshot.NodeInfo? parent = null;
        for (var i = 0; i < _frames.Count; i++)
        {
            BigModeLibrarySnapshot.NodeInfo? owner = null;
            if (i > 0 && (!_snapshot.TryGetNode(_frames[i].OpenedNodeKey!.Value.Id, out owner!) ||
                          !owner.Visible || !ReferenceEquals(owner.Parent, parent)))
            {
                _frames.RemoveRange(i, _frames.Count - i);
                break;
            }
            _frames[i].Update(_snapshot, owner, _frames[i].Selection);
            parent = owner;
        }
        foreach (var id in new List<string>(_remembered.Keys))
            if (id.Length > 0 && !_snapshot.TryGetNode(id, out _)) _remembered.Remove(id);
        NotifyChanged();
    }

    private bool CanSelect(BigModeNavigationEntry entry)
        => entry is not BigModeMediaNavigationEntry media || _snapshot.Visibility.IsItemVisible(media.Item);

    private BigModeNavigationFrame CreateFrame(BigModeLibrarySnapshot.NodeInfo? owner)
        => new(_snapshot, owner, _remembered.GetValueOrDefault(owner?.Id ?? string.Empty, new(null, 0)));

    private void Remember(BigModeNavigationFrame frame)
        => _remembered[frame.OpenedNodeKey?.Id ?? string.Empty] = frame.Selection;

    private void MoveToPath(IReadOnlyList<BigModeLibrarySnapshot.NodeInfo> path)
    {
        Remember(Current);
        var frames = new List<BigModeNavigationFrame>(path.Count + 1) { _frames[0] };
        for (var i = 0; i < path.Count; i++)
        {
            var node = path[i];
            frames[^1].TrySelect(new(BigModeNavigationEntryKind.Node, node.Id), -1);
            Remember(frames[^1]);
            frames.Add(i + 1 < _frames.Count && _frames[i + 1].OpenedNodeKey?.Id == node.Id
                ? _frames[i + 1] : CreateFrame(node));
        }
        _frames = frames;
    }

    private void NotifyChanged()
    {
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
