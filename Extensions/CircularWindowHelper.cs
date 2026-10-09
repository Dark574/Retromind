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
    /// Updates a selected-centered window which repeats its source at the edges.
    /// Sources smaller than <paramref name="minimumSourceCount"/> remain finite so
    /// very small collections do not appear to contain duplicate entries.
    /// Returns the selected index inside the resulting window.
    /// </summary>
    public static int SynchronizeRepeatingCircularWindow<T>(
        IList<T> source,
        T? selected,
        int windowSize,
        int minimumSourceCount,
        RangeObservableCollection<T> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var selectedSourceIndex = selected == null ? -1 : source.IndexOf(selected);
        if (selectedSourceIndex < 0 && source.Count > 0)
            selectedSourceIndex = 0;

        if (source.Count == 0)
        {
            SynchronizeTarget(target, Array.Empty<T>());
            return -1;
        }

        if (source.Count < minimumSourceCount || windowSize <= 0)
        {
            SynchronizeTarget(target, source);
            return selectedSourceIndex;
        }

        if (windowSize % 2 == 0)
            windowSize -= 1;

        if (windowSize <= 0)
        {
            SynchronizeTarget(target, source);
            return selectedSourceIndex;
        }

        var desired = new List<T>(windowSize);
        var half = windowSize / 2;
        for (var offset = -half; offset <= half; offset++)
        {
            var sourceIndex = (selectedSourceIndex + offset) % source.Count;
            if (sourceIndex < 0)
                sourceIndex += source.Count;

            desired.Add(source[sourceIndex]);
        }

        SynchronizeTarget(target, desired);
        return half;
    }

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

        SynchronizeTarget(target, desired);
    }

    private static void SynchronizeTarget<T>(RangeObservableCollection<T> target, IEnumerable<T> desiredItems)
    {
        var desired = desiredItems as IList<T> ?? desiredItems.ToList();

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

    /// <summary>
    /// Builds a small look-ahead set containing the items immediately beyond
    /// both edges of a circular window. Items already inside the window are not
    /// returned, including when a short source wraps around.
    /// </summary>
    public static void BuildCircularLookahead<T>(
        IList<T> source,
        T? selected,
        int windowSize,
        int lookaheadCount,
        ICollection<T> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        target.Clear();
        if (source.Count == 0 || lookaheadCount <= 0 || windowSize <= 0)
            return;

        if (windowSize % 2 == 0)
            windowSize -= 1;

        if (windowSize <= 0 || windowSize >= source.Count)
            return;

        var selectedIndex = selected == null ? -1 : source.IndexOf(selected);
        if (selectedIndex < 0)
            selectedIndex = 0;

        var half = windowSize / 2;
        var includedIndices = new HashSet<int>();
        for (var offset = -half; offset <= half; offset++)
            includedIndices.Add(WrapIndex(selectedIndex + offset, source.Count));

        for (var distance = 1; distance <= lookaheadCount; distance++)
        {
            AddIfOutsideWindow(selectedIndex + half + distance);
            AddIfOutsideWindow(selectedIndex - half - distance);
        }

        void AddIfOutsideWindow(int index)
        {
            var wrappedIndex = WrapIndex(index, source.Count);
            if (includedIndices.Add(wrappedIndex))
                target.Add(source[wrappedIndex]);
        }
    }

    private static int WrapIndex(int index, int count)
    {
        var wrappedIndex = index % count;
        return wrappedIndex < 0 ? wrappedIndex + count : wrappedIndex;
    }
}
