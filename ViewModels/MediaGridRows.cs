using System;
using System.Collections.Generic;
using Retromind.Models;

namespace Retromind.ViewModels;

public sealed class MediaItemRow
{
    public MediaItemRow(IReadOnlyList<MediaItem> items)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public IReadOnlyList<MediaItem> Items { get; }
}

internal static class MediaGridRows
{
    public static IReadOnlyList<MediaItemRow> Build(IList<MediaItem> items, int columnCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return Array.Empty<MediaItemRow>();

        var rows = new List<MediaItemRow>();
        for (var i = 0; i < items.Count; i += columnCount)
        {
            var rowCount = Math.Min(columnCount, items.Count - i);
            var row = new List<MediaItem>(rowCount);
            for (var j = 0; j < rowCount; j++)
                row.Add(items[i + j]);

            rows.Add(new MediaItemRow(row));
        }

        return rows;
    }

    public static bool Contains(IList<MediaItem> items, MediaItem selected)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ReferenceEquals(item, selected) ||
                string.Equals(item.Id, selected.Id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HaveSameItemOrder(
        IList<MediaItem> current,
        IReadOnlyList<MediaItem> updated)
    {
        if (current.Count != updated.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], updated[i]))
                return false;
        }

        return true;
    }
}
