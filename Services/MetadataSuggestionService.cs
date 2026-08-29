using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

public sealed class MetadataSuggestionService
{
    public const string DeveloperField = "developer";
    public const string PublisherField = "publisher";
    public const string GenreField = "genre";
    public const string PlatformField = "platform";
    public const string SeriesField = "series";
    public const string ReleaseTypeField = "release_type";
    public const string PlayModeField = "play_mode";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<SuggestionEntry>> _entriesByField;
    private readonly IReadOnlyList<SuggestionEntry> _customFieldKeys;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<SuggestionEntry>> _customFieldValuesByKey;

    public MetadataSuggestionService(IEnumerable<MediaNode>? rootNodes, MediaNode? customFieldScope = null)
    {
        _entriesByField = BuildIndex(rootNodes ?? Array.Empty<MediaNode>());
        (_customFieldKeys, _customFieldValuesByKey) = BuildCustomFieldIndex(customFieldScope);
    }

    public string? GetBestMatch(string fieldKey, string? input)
    {
        if (string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(input))
            return null;

        if (!_entriesByField.TryGetValue(fieldKey, out var entries))
            return null;

        return GetBestMatch(entries, input);
    }

    public IReadOnlyList<string> GetKnownCustomFieldKeys()
    {
        return _customFieldKeys
            .Select(entry => entry.Value)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? GetBestCustomFieldKeyMatch(string? input)
        => GetBestMatch(_customFieldKeys, input);

    public string? GetBestCustomFieldValueMatch(string? customFieldKey, string? input)
    {
        var key = customFieldKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) ||
            !_customFieldValuesByKey.TryGetValue(key, out var entries))
        {
            return null;
        }

        return GetBestMatch(entries, input);
    }

    private static string? GetBestMatch(IReadOnlyList<SuggestionEntry> entries, string? input)
    {
        if (entries.Count == 0 || string.IsNullOrWhiteSpace(input))
            return null;

        var prefix = input.Trim();

        var exact = entries.FirstOrDefault(entry =>
            string.Equals(entry.Value, prefix, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact.Value;

        return entries.FirstOrDefault(entry =>
            entry.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static (IReadOnlyList<SuggestionEntry> Keys,
        IReadOnlyDictionary<string, IReadOnlyList<SuggestionEntry>> ValuesByKey)
        BuildCustomFieldIndex(MediaNode? scope)
    {
        if (scope == null)
        {
            return (
                Array.Empty<SuggestionEntry>(),
                new Dictionary<string, IReadOnlyList<SuggestionEntry>>(StringComparer.OrdinalIgnoreCase));
        }

        var keyEntries = new Dictionary<string, SuggestionEntry>(StringComparer.OrdinalIgnoreCase);
        var valueEntriesByKey =
            new Dictionary<string, Dictionary<string, SuggestionEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in scope.Items)
        {
            foreach (var pair in item.CustomFields)
            {
                var key = pair.Key?.Trim();
                if (!IsReusableCustomFieldKey(key))
                    continue;

                AddEntry(keyEntries, key!);

                var value = pair.Value?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!valueEntriesByKey.TryGetValue(key!, out var valueEntries))
                {
                    valueEntries = new Dictionary<string, SuggestionEntry>(StringComparer.OrdinalIgnoreCase);
                    valueEntriesByKey[key!] = valueEntries;
                }

                AddEntry(valueEntries, value);
            }
        }

        var orderedKeys = OrderEntries(keyEntries.Values);
        var orderedValues = valueEntriesByKey.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SuggestionEntry>)OrderEntries(pair.Value.Values),
            StringComparer.OrdinalIgnoreCase);

        return (orderedKeys, orderedValues);
    }

    private static bool IsReusableCustomFieldKey(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) &&
               !CustomFieldKeyHelper.IsInternal(key);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SuggestionEntry>> BuildIndex(IEnumerable<MediaNode> rootNodes)
    {
        var buckets = CreateBuckets();

        foreach (var root in rootNodes)
            CollectRecursive(root, buckets);

        return buckets.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SuggestionEntry>)OrderEntries(pair.Value.Values),
            StringComparer.Ordinal);
    }

    private static List<SuggestionEntry> OrderEntries(IEnumerable<SuggestionEntry> entries)
    {
        return entries
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Value.Length)
            .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, Dictionary<string, SuggestionEntry>> CreateBuckets()
    {
        return new Dictionary<string, Dictionary<string, SuggestionEntry>>(StringComparer.Ordinal)
        {
            [DeveloperField] = new(StringComparer.OrdinalIgnoreCase),
            [PublisherField] = new(StringComparer.OrdinalIgnoreCase),
            [GenreField] = new(StringComparer.OrdinalIgnoreCase),
            [PlatformField] = new(StringComparer.OrdinalIgnoreCase),
            [SeriesField] = new(StringComparer.OrdinalIgnoreCase),
            [ReleaseTypeField] = new(StringComparer.OrdinalIgnoreCase),
            [PlayModeField] = new(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static void CollectRecursive(MediaNode node, IDictionary<string, Dictionary<string, SuggestionEntry>> buckets)
    {
        foreach (var item in node.Items)
        {
            AddValue(buckets, DeveloperField, item.Developer);
            AddValue(buckets, PublisherField, item.Publisher);
            AddValue(buckets, GenreField, item.Genre);
            AddValue(buckets, PlatformField, item.Platform);
            AddValue(buckets, SeriesField, item.Series);
            AddValue(buckets, ReleaseTypeField, item.ReleaseType);
            AddValue(buckets, PlayModeField, item.PlayMode);
        }

        foreach (var child in node.Children)
            CollectRecursive(child, buckets);
    }

    private static void AddValue(
        IDictionary<string, Dictionary<string, SuggestionEntry>> buckets,
        string fieldKey,
        string? rawValue)
    {
        var value = rawValue?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;

        var entries = buckets[fieldKey];
        AddEntry(entries, value);
    }

    private static void AddEntry(IDictionary<string, SuggestionEntry> entries, string value)
    {
        if (entries.TryGetValue(value, out var existing))
        {
            existing.Count++;
            return;
        }

        entries[value] = new SuggestionEntry(value);
    }

    private sealed class SuggestionEntry
    {
        public SuggestionEntry(string value)
        {
            Value = value;
            Count = 1;
        }

        public string Value { get; }
        public int Count { get; set; }
    }
}
