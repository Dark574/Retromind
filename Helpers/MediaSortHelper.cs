using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public static class MediaSortHelper
{
    private static bool _ignoreLeadingArticlesInTitleSort;

    private static readonly Dictionary<string, string[]> LeadingArticlesByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["the", "an", "a"],
        ["de"] = ["der", "die", "das", "einer", "einem", "einen", "eine", "ein"]
    };

    private static readonly StringComparer ArticleComparer = StringComparer.OrdinalIgnoreCase;

    public static IComparer<MediaItem> DisplayOrderComparer { get; } =
        Comparer<MediaItem>.Create(CompareForDisplayOrder);

    public static void SetIgnoreLeadingArticlesInTitleSort(bool enabled)
    {
        _ignoreLeadingArticlesInTitleSort = enabled;
    }

    public static int CompareForDisplayOrder(MediaItem? left, MediaItem? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var bySortTitle = CompareNaturalCurrentCulture(
            GetPrimarySortKey(left),
            GetPrimarySortKey(right));

        if (bySortTitle != 0)
            return bySortTitle;

        var byTitle = CompareNaturalCurrentCulture(
            GetTitleSortKey(left.Title),
            GetTitleSortKey(right.Title));
        if (byTitle != 0)
            return byTitle;

        return string.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPrimarySortKey(MediaItem item)
    {
        if (item == null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(item.SortTitle))
            return item.SortTitle.Trim();

        return GetTitleSortKey(item.Title);
    }

    private static string GetTitleSortKey(string? title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (!_ignoreLeadingArticlesInTitleSort || string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        var stripped = StripLeadingArticle(trimmed);
        return string.IsNullOrWhiteSpace(stripped) ? trimmed : stripped;
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

    private static string StripLeadingArticle(string value)
    {
        var culture = CultureInfo.CurrentCulture;
        var articles = GetApplicableArticles(culture);
        if (articles.Count == 0)
            return value;

        var compareInfo = culture.CompareInfo;
        const CompareOptions options =
            CompareOptions.IgnoreCase |
            CompareOptions.IgnoreNonSpace;

        foreach (var article in articles)
        {
            if (string.IsNullOrWhiteSpace(article))
                continue;

            if (value.Length <= article.Length)
                continue;

            if (!compareInfo.IsPrefix(value, article, options))
                continue;

            // Articles should only match as standalone first word.
            var boundary = value[article.Length];
            if (!char.IsWhiteSpace(boundary))
                continue;

            return value.Substring(article.Length).TrimStart();
        }

        return value;
    }

    private static IReadOnlyList<string> GetApplicableArticles(CultureInfo culture)
    {
        var language = culture.TwoLetterISOLanguageName;
        var merged = new List<string>();

        if (LeadingArticlesByLanguage.TryGetValue(language, out var languageArticles))
            AppendUnique(merged, languageArticles);

        // Always include English fallback so common library titles ("The ...")
        // behave naturally even on non-English system locales.
        if (!string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) &&
            LeadingArticlesByLanguage.TryGetValue("en", out var englishArticles))
        {
            AppendUnique(merged, englishArticles);
        }

        merged.Sort((left, right) => right.Length.CompareTo(left.Length));
        return merged;
    }

    private static void AppendUnique(List<string> destination, IEnumerable<string> source)
    {
        foreach (var article in source)
        {
            if (string.IsNullOrWhiteSpace(article))
                continue;

            if (destination.Any(existing => ArticleComparer.Equals(existing, article)))
                continue;

            destination.Add(article);
        }
    }
}
