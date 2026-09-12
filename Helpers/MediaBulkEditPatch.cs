using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public enum CustomFieldPatchOperation
{
    Set,
    Remove
}

public enum TextMetadataField
{
    Developer,
    Publisher,
    Platform,
    Source,
    Genre,
    Series,
    ReleaseType,
    PlayMode,
    MaxPlayers
}

public enum MetadataPatchOperation
{
    Set,
    Clear
}

public sealed record TextMetadataPatch(
    TextMetadataField Field,
    MetadataPatchOperation Operation,
    string? Value = null);

public sealed record CustomFieldPatch(
    CustomFieldPatchOperation Operation,
    string Key,
    string? Value = null);

/// <summary>
/// Describes explicit changes that can be applied to several media items.
/// Properties that are not part of the patch remain untouched.
/// </summary>
public sealed class MediaBulkEditPatch
{
    public bool ChangeStatus { get; init; }
    public PlayStatus Status { get; init; }
    public IReadOnlyList<TextMetadataPatch> TextMetadata { get; init; } = [];
    public IReadOnlyList<CustomFieldPatch> CustomFields { get; init; } = [];

    public bool ApplyTo(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var changed = false;
        if (ChangeStatus && item.Status != Status)
        {
            item.Status = Status;
            changed = true;
        }

        foreach (var patch in TextMetadata)
            changed = ApplyTextMetadataPatch(item, patch) || changed;

        if (CustomFields.Count == 0)
            return changed;

        var updatedFields = new Dictionary<string, string>(
            item.CustomFields,
            item.CustomFields.Comparer);

        foreach (var patch in CustomFields)
        {
            var key = patch.Key.Trim();
            if (string.IsNullOrWhiteSpace(key) || CustomFieldKeyHelper.IsInternal(key))
                continue;

            var matchingKeys = updatedFields.Keys
                .Where(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (patch.Operation == CustomFieldPatchOperation.Remove)
            {
                foreach (var matchingKey in matchingKeys)
                    updatedFields.Remove(matchingKey);

                continue;
            }

            var value = patch.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            // Keep an existing spelling of the key, but collapse accidental
            // case-only duplicates while applying the bulk change.
            var targetKey = matchingKeys.FirstOrDefault() ?? key;
            foreach (var duplicateKey in matchingKeys.Skip(1))
                updatedFields.Remove(duplicateKey);

            updatedFields[targetKey] = value;
        }

        if (HaveSameFields(item.CustomFields, updatedFields))
            return changed;

        // Replacing the dictionary also raises the derived custom-field UI properties.
        item.CustomFields = updatedFields;
        return true;
    }

    private static bool ApplyTextMetadataPatch(MediaItem item, TextMetadataPatch patch)
    {
        var value = patch.Operation == MetadataPatchOperation.Clear
            ? null
            : NormalizeOptionalText(patch.Value);

        return patch.Field switch
        {
            TextMetadataField.Developer => SetText(item.Developer, value, updated => item.Developer = updated),
            TextMetadataField.Publisher => SetText(item.Publisher, value, updated => item.Publisher = updated),
            TextMetadataField.Platform => SetText(item.Platform, value, updated => item.Platform = updated),
            TextMetadataField.Source => SetText(item.Source, value, updated => item.Source = updated),
            TextMetadataField.Genre => SetText(item.Genre, value, updated => item.Genre = updated),
            TextMetadataField.Series => SetText(item.Series, value, updated => item.Series = updated),
            TextMetadataField.ReleaseType => SetText(item.ReleaseType, value, updated => item.ReleaseType = updated),
            TextMetadataField.PlayMode => SetText(item.PlayMode, value, updated => item.PlayMode = updated),
            TextMetadataField.MaxPlayers => SetText(item.MaxPlayers, value, updated => item.MaxPlayers = updated),
            _ => false
        };
    }

    private static bool SetText(string? current, string? value, Action<string?> setter)
    {
        if (string.Equals(current, value, StringComparison.Ordinal))
            return false;

        setter(value);
        return true;
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool HaveSameFields(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        if (first.Count != second.Count)
            return false;

        return first.All(pair =>
            second.TryGetValue(pair.Key, out var value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));
    }
}
