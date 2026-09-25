using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Helpers;

namespace Retromind.Extensions;

/// <summary>
/// Helper to build a wrap-around "circular" window around a selected item.
/// Intended for theme-driven lists that should show the selected item centered,
/// with neighbors above/below and seamless wrap at the list edges.
/// </summary>
public static class CircularWindowHelper
{
    /// <summary>
    /// Updates a circular window while retaining the existing item containers for
    /// ordinary one-step navigation. This avoids rebuilding every visible card in
    /// carousel themes when only the item entering at one edge has changed.
    /// </summary>
    public static void SynchronizeCircularWindow<T>(
        IList<T> source,
        T? selected,
        int windowSize,
        RangeObservableCollection<T> target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var desired = new List<T>();
        BuildCircularWindow(source, selected, windowSize, desired);

        if (target.SequenceEqual(desired))
            return;

        if (target.Count == desired.Count && target.Count > 1)
        {
            var shiftedForward = true;
            for (var index = 0; index < target.Count - 1; index++)
            {
                if (EqualityComparer<T>.Default.Equals(target[index + 1], desired[index]))
                    continue;

                shiftedForward = false;
                break;
            }

            if (shiftedForward)
            {
                target.RemoveAt(0);
                target.Add(desired[^1]);
                return;
            }

            var shiftedBackward = true;
            for (var index = 0; index < target.Count - 1; index++)
            {
                if (EqualityComparer<T>.Default.Equals(target[index], desired[index + 1]))
                    continue;

                shiftedBackward = false;
                break;
            }

            if (shiftedBackward)
            {
                target.RemoveAt(target.Count - 1);
                target.Insert(0, desired[0]);
                return;
            }
        }

        target.ReplaceAll(desired);
    }

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
