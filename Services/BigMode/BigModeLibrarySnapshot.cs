using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Services.BigMode;

/// <summary>
/// Indexed topology and bottom-up visibility. Built only at construction/Refresh,
/// never on a navigation step. All traversal is iterative, including deep trees.
/// </summary>
internal sealed class BigModeLibrarySnapshot
{
    internal sealed class NodeInfo(MediaNode node, NodeInfo? parent)
    {
        public MediaNode Node { get; } = node;
        public string Id { get; } = node.Id;
        public NodeInfo? Parent { get; } = parent;
        public List<NodeInfo> Children { get; } = [];
        public List<MediaInfo> Items { get; } = [];
        public bool Visible { get; set; }
        public int VisibleMediaCount { get; set; }
    }

    internal sealed record MediaInfo(string Id, MediaItem Item, NodeInfo Owner, bool Visible);

    private readonly Dictionary<string, NodeInfo> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MediaInfo> _media = new(StringComparer.Ordinal);
    private readonly List<NodeInfo> _roots = [];

    public BigModeVisibilityPolicy Visibility { get; }

    public BigModeLibrarySnapshot(IReadOnlyList<MediaNode> roots, bool parentalFilterActive)
    {
        Visibility = new BigModeVisibilityPolicy(parentalFilterActive);
        var ordered = new List<NodeInfo>();
        var pending = new Stack<(MediaNode Node, NodeInfo? Parent)>();
        for (var i = roots.Count - 1; i >= 0; i--) pending.Push((roots[i], null));

        var visited = new HashSet<MediaNode>();
        while (pending.TryPop(out var next))
        {
            if (!visited.Add(next.Node))
                throw new ArgumentException("The library must be a tree without repeated node instances.", nameof(roots));
            ArgumentException.ThrowIfNullOrWhiteSpace(next.Node.Id);
            var info = new NodeInfo(next.Node, next.Parent);
            if (!_nodes.TryAdd(info.Id, info))
                throw new ArgumentException("Node IDs must be unique within the library.", nameof(roots));

            if (next.Parent == null) _roots.Add(info);
            else next.Parent.Children.Add(info);
            ordered.Add(info);

            foreach (var item in next.Node.Items)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.Id);
                var media = new MediaInfo(item.Id, item, info, Visibility.IsItemVisible(item));
                if (!_media.TryAdd(media.Id, media))
                    throw new ArgumentException("Media IDs must be unique within the library.", nameof(roots));
                info.Items.Add(media);
            }
            for (var i = next.Node.Children.Count - 1; i >= 0; i--)
                pending.Push((next.Node.Children[i], info));
        }

        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var info = ordered[i];
            info.VisibleMediaCount = info.Items.Count(item => item.Visible) + info.Children.Sum(child => child.VisibleMediaCount);
            info.Visible = Visibility.IsDirectlyVisible(
                info.Children.Count == 0 && info.Items.Count == 0,
                info.Items.Any(item => item.Visible)) || info.Children.Any(child => child.Visible);
        }
    }

    public bool TryGetNode(string? id, out NodeInfo info)
    {
        info = null!;
        return !string.IsNullOrWhiteSpace(id) && _nodes.TryGetValue(id, out info!);
    }

    public bool TryGetMedia(string? id, out MediaInfo info)
    {
        info = null!;
        return !string.IsNullOrWhiteSpace(id) && _media.TryGetValue(id, out info!);
    }

    public List<BigModeNavigationEntry> BuildEntries(NodeInfo? owner)
    {
        var children = owner?.Children ?? _roots;
        var entries = new List<BigModeNavigationEntry>();
        foreach (var child in children)
            if (child.Visible)
                entries.Add(new BigModeNodeNavigationEntry(child.Node, child.Id, owner?.Node));
        if (owner != null)
        {
            foreach (var media in owner.Items)
                if (media.Visible && Visibility.IsItemVisible(media.Item))
                    entries.Add(new BigModeMediaNavigationEntry(media.Item, media.Id, owner.Node));
        }
        return entries;
    }

    public static List<NodeInfo> PathTo(NodeInfo? node)
    {
        var path = new List<NodeInfo>();
        for (var current = node; current != null; current = current.Parent) path.Add(current);
        path.Reverse();
        return path;
    }
}
