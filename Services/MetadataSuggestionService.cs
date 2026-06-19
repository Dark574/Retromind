using System;
using System.Collections.Generic;
using System.Linq;
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

    public MetadataSuggestionService(IEnumerable<MediaNode>? rootNodes)
    {
        _entriesByField = BuildIndex(rootNodes ?? Array.Empty<MediaNode>());
    }

    public string? GetBestMatch(string fieldKey, string? input)
    {
        if (string.IsNullOrWhiteSpace(fieldKey) || string.IsNullOrWhiteSpace(input))
            return null;

        if (!_entriesByField.TryGetValue(fieldKey, out var entries) || entries.Count == 0)
            return null;

        var prefix = input.Trim();
        if (prefix.Length == 0)
            return null;

        var exact = entries.FirstOrDefault(entry =>
            string.Equals(entry.Value, prefix, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact.Value;

        return entries.FirstOrDefault(entry =>
            entry.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<SuggestionEntry>> BuildIndex(IEnumerable<MediaNode> rootNodes)
    {
        var buckets = CreateBuckets();

        foreach (var root in rootNodes)
            CollectRecursive(root, buckets);

        return buckets.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SuggestionEntry>)pair.Value.Values
                .OrderByDescending(entry => entry.Count)
                .ThenBy(entry => entry.Value.Length)
                .ThenBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.Ordinal);
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
