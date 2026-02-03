using System;
using System.Collections.Generic;

namespace Retromind.Extensions;

/// <summary>
/// Helper to build a wrap-around "circular" window around a selected item.
/// Intended for theme-driven lists that should show the selected item centered,
/// with neighbors above/below and seamless wrap at the list edges.
/// </summary>
public static class CircularWindowHelper
{
    public static void BuildCircularWindow<T>(
        IList<T> source,
        T? selected,
        int windowSize,
        ICollection<T> target)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (target == null) throw new ArgumentNullException(nameof(target));

        target.Clear();
        if (source.Count == 0)
            return;

        if (windowSize <= 0 || windowSize >= source.Count)
        {
            foreach (var item in source)
                target.Add(item);
            return;
        }

        // Ensure odd window size so selection can sit in the center.
        if (windowSize % 2 == 0)
            windowSize -= 1;

        if (windowSize <= 0)
        {
            foreach (var item in source)
                target.Add(item);
            return;
        }

        var selectedIndex = 0;
        if (selected != null)
        {
            var idx = source.IndexOf(selected);
            if (idx >= 0)
                selectedIndex = idx;
        }

        var count = source.Count;
        var half = windowSize / 2;

        for (int i = -half; i <= half; i++)
        {
            var idx = (selectedIndex + i) % count;
            if (idx < 0)
                idx += count;

            target.Add(source[idx]);
        }
    }
}
