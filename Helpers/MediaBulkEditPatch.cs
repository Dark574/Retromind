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
