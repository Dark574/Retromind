using System;
using System.Collections.Generic;
using System.Globalization;
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

        var bySortTitle = CompareNaturalCurrentCulture(
            GetSortKey(left),
            GetSortKey(right));

        if (bySortTitle != 0)
            return bySortTitle;

        var byTitle = CompareNaturalCurrentCulture(left.Title, right.Title);
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

    /// <summary>
    /// Compares strings case-insensitively with numeric ordering for digit runs.
    /// Example: "Tomb Raider 2" sorts before "Tomb Raider 10".
    /// </summary>
    private static int CompareNaturalCurrentCulture(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        var compareInfo = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions options =
            CompareOptions.IgnoreCase |
            CompareOptions.IgnoreNonSpace;

        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftChar = left[leftIndex];
            var rightChar = right[rightIndex];

            var leftIsDigit = IsAsciiDigit(leftChar);
            var rightIsDigit = IsAsciiDigit(rightChar);

            if (leftIsDigit && rightIsDigit)
            {
                var byNumber = CompareDigitRuns(left, ref leftIndex, right, ref rightIndex);
                if (byNumber != 0)
                    return byNumber;

                continue;
            }

            if (leftIsDigit != rightIsDigit)
            {
                var byKind = compareInfo.Compare(left, leftIndex, 1, right, rightIndex, 1, options);
                if (byKind != 0)
                    return byKind;

                leftIndex++;
                rightIndex++;
                continue;
            }

            var leftStart = leftIndex;
            while (leftIndex < left.Length && !IsAsciiDigit(left[leftIndex]))
                leftIndex++;

            var rightStart = rightIndex;
            while (rightIndex < right.Length && !IsAsciiDigit(right[rightIndex]))
                rightIndex++;

            var byText = compareInfo.Compare(
                left, leftStart, leftIndex - leftStart,
                right, rightStart, rightIndex - rightStart,
                options);

            if (byText != 0)
                return byText;
        }

        if (leftIndex < left.Length)
            return 1;

        if (rightIndex < right.Length)
            return -1;

        // Keep ordering deterministic when strings only differ by case/representation.
        return string.Compare(left, right, StringComparison.Ordinal);
    }

    private static int CompareDigitRuns(string left, ref int leftIndex, string right, ref int rightIndex)
    {
        var leftStart = leftIndex;
        while (leftIndex < left.Length && IsAsciiDigit(left[leftIndex]))
            leftIndex++;

        var rightStart = rightIndex;
        while (rightIndex < right.Length && IsAsciiDigit(right[rightIndex]))
            rightIndex++;

        var leftNonZeroStart = leftStart;
        while (leftNonZeroStart < leftIndex && left[leftNonZeroStart] == '0')
            leftNonZeroStart++;

        var rightNonZeroStart = rightStart;
        while (rightNonZeroStart < rightIndex && right[rightNonZeroStart] == '0')
            rightNonZeroStart++;

        var leftSignificantLength = leftIndex - leftNonZeroStart;
        var rightSignificantLength = rightIndex - rightNonZeroStart;
        if (leftSignificantLength != rightSignificantLength)
            return leftSignificantLength < rightSignificantLength ? -1 : 1;

        for (var offset = 0; offset < leftSignificantLength; offset++)
        {
            var leftDigit = left[leftNonZeroStart + offset];
            var rightDigit = right[rightNonZeroStart + offset];
            if (leftDigit != rightDigit)
                return leftDigit < rightDigit ? -1 : 1;
        }

        // Same numeric value (e.g., "2" vs "02"): shorter run first.
        var leftRunLength = leftIndex - leftStart;
        var rightRunLength = rightIndex - rightStart;
        if (leftRunLength != rightRunLength)
            return leftRunLength < rightRunLength ? -1 : 1;

        return 0;
    }

    private static bool IsAsciiDigit(char value)
    {
        return value is >= '0' and <= '9';
    }
}
