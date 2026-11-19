using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

internal static class ScrapedMetadataApplier
{
    internal static bool Apply(
        MediaItem item,
        ScraperSearchResult result,
        ScraperImportSettings settings)
    {
        var changed = false;
        var mode = settings.ExistingDataMode;

        if (settings.ImportDescription &&
            TryApplyStringField(
                item.Description,
                result.Description,
                value => item.Description = value,
                mode,
                StringComparison.Ordinal))
        {
            changed = true;
        }

        if (settings.ImportReleaseDate &&
            TryApplyDateField(
                item.ReleaseDate,
                result.ReleaseDate,
                value => item.ReleaseDate = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportRating &&
            TryApplyRatingField(
                item.Rating,
                result.Rating,
                value => item.Rating = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportDeveloper &&
            TryApplyStringField(
                item.Developer,
                result.Developer,
                value => item.Developer = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportGenre &&
            TryApplyStringField(
                item.Genre,
                result.Genre,
                value => item.Genre = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportPlatform)
        {
            if (TryApplyStringField(
                    item.Platform,
                    result.Platform,
                    value => item.Platform = value,
                    mode))
            {
                changed = true;
            }

            var platform = result.Platform?.Trim();
            if (!string.IsNullOrWhiteSpace(platform) &&
                string.Equals(item.Platform?.Trim(), platform, StringComparison.OrdinalIgnoreCase) &&
                !item.Tags.Any(t => string.Equals(t, platform, StringComparison.OrdinalIgnoreCase)))
            {
                item.Tags.Add(platform);
                changed = true;
            }
        }

        if (settings.ImportPublisher &&
            TryApplyStringField(
                item.Publisher,
                result.Publisher,
                value => item.Publisher = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSeries &&
            TryApplyStringField(
                item.Series,
                result.Series,
                value => item.Series = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportReleaseType &&
            TryApplyStringField(
                item.ReleaseType,
                result.ReleaseType,
                value => item.ReleaseType = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSortTitle &&
            TryApplyStringField(
                item.SortTitle,
                result.SortTitle,
                value => item.SortTitle = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportPlayMode &&
            TryApplyStringField(
                item.PlayMode,
                result.PlayMode,
                value => item.PlayMode = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportMaxPlayers &&
            TryApplyStringField(
                item.MaxPlayers,
                result.MaxPlayers,
                value => item.MaxPlayers = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportSource &&
            TryApplyStringField(
                item.Source,
                result.Source,
                value => item.Source = value,
                mode))
        {
            changed = true;
        }

        if (settings.ImportCustomFields &&
            MergeCustomFields(item, result.CustomFields, mode))
        {
            changed = true;
        }

        return changed;
    }

    private static bool TryApplyStringField(
        string? currentValue,
        string? incomingValue,
        Action<string> applyValue,
        ScraperExistingDataMode mode,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var incoming = incomingValue?.Trim();
        if (string.IsNullOrWhiteSpace(incoming))
            return false;

        var current = currentValue?.Trim();
        if (string.IsNullOrWhiteSpace(current))
        {
            applyValue(incoming);
            return true;
        }

        if (string.Equals(current, incoming, comparison))
            return false;

        if (mode != ScraperExistingDataMode.OverwriteAlways)
            return false;

        applyValue(incoming);
        return true;
    }

    private static bool TryApplyDateField(
        DateTime? currentValue,
        DateTime? incomingValue,
        Action<DateTime?> applyValue,
        ScraperExistingDataMode mode)
    {
        if (!incomingValue.HasValue)
            return false;

        var incoming = incomingValue.Value.Date;
        if (!currentValue.HasValue)
        {
            applyValue(incoming);
            return true;
        }

        var current = currentValue.Value.Date;
        if (current == incoming)
            return false;

        if (mode != ScraperExistingDataMode.OverwriteAlways)
            return false;

        applyValue(incoming);
        return true;
    }

    private static bool TryApplyRatingField(
        double currentValue,
        double? incomingValue,
        Action<double> applyValue,
        ScraperExistingDataMode mode)
    {
        if (!incomingValue.HasValue)
            return false;

        var incoming = Math.Clamp(incomingValue.Value, 0d, 100d);
        var hasCurrent = currentValue > 0d;
        if (!hasCurrent)
        {
            applyValue(incoming);
            return true;
        }

        if (Math.Abs(currentValue - incoming) < 0.0001d)
            return false;

        if (mode != ScraperExistingDataMode.OverwriteAlways)
            return false;

        applyValue(incoming);
        return true;
    }

    private static bool MergeCustomFields(
        MediaItem item,
        Dictionary<string, string>? incoming,
        ScraperExistingDataMode mode)
    {
        if (incoming == null || incoming.Count == 0)
            return false;

        var merged = new Dictionary<string, string>(
            item.CustomFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var changed = false;

        foreach (var kv in incoming)
        {
            var key = kv.Key?.Trim();
            var value = kv.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            if (!merged.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing))
            {
                merged[key] = value;
                changed = true;
                continue;
            }

            if (string.Equals(existing, value, StringComparison.Ordinal))
                continue;

            if (mode != ScraperExistingDataMode.OverwriteAlways)
                continue;

            merged[key] = value;
            changed = true;
        }

        if (!changed)
            return false;

        item.CustomFields = merged;
        return true;
    }
}
