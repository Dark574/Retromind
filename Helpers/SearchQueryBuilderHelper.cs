using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Helpers;

public sealed record SearchQueryFieldOption(string Key, string Label);
public sealed record SearchQueryBuilderData(
    IReadOnlyList<SearchQueryFieldOption> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SuggestionsByField);

public static class SearchQueryBuilderHelper
{
    private const int MaxSuggestionsPerField = 250;
    private const string CustomFieldPrefix = "cf.";
    private const string CustomFieldEncodedPrefix = "cfu.";

    private static IReadOnlyList<SearchQueryFieldOption> BaseFields { get; } = new[]
    {
        new SearchQueryFieldOption("title", GetFilterFieldLabel("Search.FilterField.Title", "Title")),
        new SearchQueryFieldOption("sorttitle", GetFilterFieldLabel("Search.FilterField.SortTitle", "Sort Title")),
        new SearchQueryFieldOption("description", GetFilterFieldLabel("Search.FilterField.Description", "Description")),
        new SearchQueryFieldOption("developer", GetFilterFieldLabel("Search.FilterField.Developer", "Developer")),
        new SearchQueryFieldOption("publisher", GetFilterFieldLabel("Search.FilterField.Publisher", "Publisher")),
        new SearchQueryFieldOption("platform", GetFilterFieldLabel("Search.FilterField.Platform", "Platform")),
        new SearchQueryFieldOption("source", GetFilterFieldLabel("Search.FilterField.Source", "Source")),
        new SearchQueryFieldOption("genre", GetFilterFieldLabel("Search.FilterField.Genre", "Genre")),
        new SearchQueryFieldOption("series", GetFilterFieldLabel("Search.FilterField.Series", "Series")),
        new SearchQueryFieldOption("releasetype", GetFilterFieldLabel("Search.FilterField.ReleaseType", "Release Type")),
        new SearchQueryFieldOption("playmode", GetFilterFieldLabel("Search.FilterField.PlayMode", "Play Mode")),
        new SearchQueryFieldOption("maxplayers", GetFilterFieldLabel("Search.FilterField.MaxPlayers", "Max Players")),
        new SearchQueryFieldOption("status", GetFilterFieldLabel("Search.FilterField.Status", "Status")),
        new SearchQueryFieldOption("year", GetFilterFieldLabel("Search.FilterField.Year", "Year")),
        new SearchQueryFieldOption("date", GetFilterFieldLabel("Search.FilterField.ReleaseDate", "Release Date")),
        new SearchQueryFieldOption("tag", GetFilterFieldLabel("Search.FilterField.Tag", "Tag")),
        new SearchQueryFieldOption("id", GetFilterFieldLabel("Search.FilterField.Id", "ID")),
        new SearchQueryFieldOption("favorite", GetFilterFieldLabel("Search.FilterField.Favorite", "Favorite"))
    };

    public static SearchQueryBuilderData BuildData(IEnumerable<MediaItem> items)
    {
        var buckets = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["sorttitle"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["description"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["platform"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["genre"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["developer"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["publisher"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["source"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["series"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["releasetype"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["playmode"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["maxplayers"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["status"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["year"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["date"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["tag"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["id"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ["favorite"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        var customFieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            AddIfNotEmpty(buckets["title"], item.Title);
            AddIfNotEmpty(buckets["sorttitle"], item.SortTitle);
            AddIfNotEmpty(buckets["description"], item.Description);
            AddIfNotEmpty(buckets["platform"], item.Platform);
            AddIfNotEmpty(buckets["genre"], item.Genre);
            AddIfNotEmpty(buckets["developer"], item.Developer);
            AddIfNotEmpty(buckets["publisher"], item.Publisher);
            AddIfNotEmpty(buckets["source"], item.Source);
            AddIfNotEmpty(buckets["series"], item.Series);
            AddIfNotEmpty(buckets["releasetype"], item.ReleaseType);
            AddIfNotEmpty(buckets["playmode"], item.PlayMode);
            AddIfNotEmpty(buckets["maxplayers"], item.MaxPlayers);
            AddIfNotEmpty(buckets["status"], item.Status.ToString().ToLowerInvariant());
            AddIfNotEmpty(buckets["id"], item.Id);

            if (item.ReleaseDate.HasValue)
            {
                AddIfNotEmpty(buckets["year"], item.ReleaseDate.Value.Year.ToString());
                AddIfNotEmpty(buckets["date"], item.ReleaseDate.Value.ToString("yyyy-MM-dd"));
            }

            foreach (var tag in item.Tags)
                AddIfNotEmpty(buckets["tag"], tag);

            foreach (var pair in item.CustomFields)
            {
                customFieldKeys.Add(pair.Key);

                var dynamicFieldKey = BuildDynamicCustomFieldKey(pair.Key);
                var customValuesBucket = GetOrCreateBucket(buckets, dynamicFieldKey);
                AddIfNotEmpty(customValuesBucket, pair.Value);
            }
        }

        foreach (var status in Enum.GetNames<PlayStatus>())
            AddIfNotEmpty(buckets["status"], status.ToLowerInvariant());

        buckets["favorite"].Add("true");
        buckets["favorite"].Add("false");

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in buckets)
        {
            var ordered = pair.Value
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .Take(MaxSuggestionsPerField)
                .ToList();
            result[pair.Key] = ordered;
        }

        var customFieldPrefixLabel = GetFilterFieldLabel("Search.FilterField.CustomNamedPrefix", "Custom Field");
        var dynamicCustomFields = customFieldKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key => new SearchQueryFieldOption(
                BuildDynamicCustomFieldKey(key),
                $"{customFieldPrefixLabel}: {key}"))
            .ToList();

        var fields = BaseFields.Concat(dynamicCustomFields).ToList();
        return new SearchQueryBuilderData(fields, result);
    }

    public static string BuildToken(string fieldKey, string value)
    {
        var safeKey = fieldKey.Trim();
        var safeValue = value.Trim().Replace("\"", string.Empty);
        if (safeValue.IndexOfAny([' ', '\t']) >= 0)
            safeValue = $"\"{safeValue}\"";

        return $"{safeKey}:{safeValue}";
    }

    public static string ApplyTokenToSearch(
        string currentSearchText,
        string token,
        bool replaceSearch,
        string? joinOperator = null)
    {
        if (replaceSearch || string.IsNullOrWhiteSpace(currentSearchText))
            return token;

        var prefix = currentSearchText.TrimEnd();
        if (string.IsNullOrEmpty(prefix))
            return token;

        var normalizedJoin = string.Equals(joinOperator, "OR", StringComparison.OrdinalIgnoreCase)
            ? "OR"
            : "AND";

        return $"{prefix} {normalizedJoin} {token}";
    }

    private static void AddIfNotEmpty(ISet<string> set, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            set.Add(value.Trim());
    }

    private static HashSet<string> GetOrCreateBucket(
        IDictionary<string, HashSet<string>> buckets,
        string fieldKey)
    {
        if (buckets.TryGetValue(fieldKey, out var bucket))
            return bucket;

        bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        buckets[fieldKey] = bucket;
        return bucket;
    }

    private static string BuildDynamicCustomFieldKey(string customFieldKey)
    {
        var trimmed = customFieldKey.Trim();
        if (trimmed.Length == 0)
            return CustomFieldPrefix;

        return IsSimpleCustomFieldKey(trimmed)
            ? $"{CustomFieldPrefix}{trimmed}"
            : $"{CustomFieldEncodedPrefix}{Uri.EscapeDataString(trimmed)}";
    }

    private static bool IsSimpleCustomFieldKey(string value)
    {
        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.'))
                return false;
        }

        return true;
    }

    private static string GetFilterFieldLabel(string resourceKey, string fallback)
    {
        var text = Strings.ResourceManager.GetString(resourceKey, Strings.Culture);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }
}
