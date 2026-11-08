using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.Services.BigMode;

/// <summary>
/// Shared library rules, without Avalonia, theme capabilities or navigation state.
/// An actually empty node remains visible, including when reached through ancestors.
/// </summary>
public sealed class BigModeVisibilityPolicy(bool parentalFilterActive)
{
    public bool ParentalFilterActive { get; } = parentalFilterActive;

    public bool IsItemVisible(MediaItem item) => IsItemVisible(item, ParentalFilterActive);

    public static bool IsItemVisible(MediaItem item, bool parentalFilterActive)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !parentalFilterActive || !item.IsProtected;
    }

    internal bool IsDirectlyVisible(bool isEmpty, bool hasVisibleItems)
        => IsDirectlyVisible(ParentalFilterActive, isEmpty, hasVisibleItems);

    private static bool IsDirectlyVisible(bool parentalFilterActive, bool isEmpty, bool hasVisibleItems)
        => !parentalFilterActive || isEmpty || hasVisibleItems;

    public bool IsNodeVisible(MediaNode node) => IsNodeVisible(node, ParentalFilterActive);

    public static bool IsNodeVisible(MediaNode node, bool parentalFilterActive)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!parentalFilterActive) return true;

        var pending = new Stack<MediaNode>();
        var visited = new HashSet<MediaNode>();
        pending.Push(node);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            var hasVisibleItems = false;
            foreach (var item in current.Items)
            {
                if (IsItemVisible(item, parentalFilterActive)) { hasVisibleItems = true; break; }
            }
            if (IsDirectlyVisible(parentalFilterActive,
                    current.Children.Count == 0 && current.Items.Count == 0, hasVisibleItems))
                return true;

            foreach (var child in current.Children) pending.Push(child);
        }
        return false;
    }
}
