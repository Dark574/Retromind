using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.Helpers;

public static class MediaSortHelper
{
    public static IComparer<MediaItem> DisplayOrderComparer { get; } =
        Comparer<MediaItem>.Create(CompareForDisplayOrder);

    public static int CompareForDisplayOrder(MediaItem? left, MediaItem? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var bySortTitle = string.Compare(
            GetSortKey(left),
            GetSortKey(right),
            StringComparison.OrdinalIgnoreCase);

        if (bySortTitle != 0)
            return bySortTitle;

        var byTitle = string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        if (byTitle != 0)
            return byTitle;

        return string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetSortKey(MediaItem item)
    {
        if (item == null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(item.SortTitle))
            return item.SortTitle.Trim();

        return item.Title?.Trim() ?? string.Empty;
    }
}
