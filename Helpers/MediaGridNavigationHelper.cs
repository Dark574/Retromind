using System;
using System.Collections.Generic;
using Avalonia.Input;
using Retromind.Models;

namespace Retromind.Helpers;

internal static class MediaGridNavigationHelper
{
    public static int FindSelectedIndex(IList<MediaItem> items, MediaItem? selected)
    {
        if (selected == null)
            return -1;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ReferenceEquals(item, selected) ||
                string.Equals(item.Id, selected.Id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public static bool TryGetTargetIndex(
        Key key,
        int selectedIndex,
        int itemCount,
        int columnCount,
        out int targetIndex)
    {
        targetIndex = 0;
        if (itemCount <= 0)
            return false;

        columnCount = Math.Max(1, columnCount);
        switch (key)
        {
            case Key.Left:
                targetIndex = selectedIndex <= 0 ? 0 : selectedIndex - 1;
                return true;
            case Key.Right:
                targetIndex = selectedIndex < 0 ? 0 : Math.Min(selectedIndex + 1, itemCount - 1);
                return true;
            case Key.Up:
                targetIndex = selectedIndex < 0 ? 0 : Math.Max(selectedIndex - columnCount, 0);
                return true;
            case Key.Down:
                targetIndex = selectedIndex < 0 ? 0 : Math.Min(selectedIndex + columnCount, itemCount - 1);
                return true;
            case Key.Home:
                targetIndex = 0;
                return true;
            case Key.End:
                targetIndex = itemCount - 1;
                return true;
            default:
                return false;
        }
    }
}
