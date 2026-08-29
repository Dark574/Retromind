using System;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

internal enum MediaItemMoveTargetStatus
{
    Allowed,
    CurrentNode,
    StoreProviderMismatch,
    MissingStoreIdentity,
    DuplicateStoreItem
}

internal sealed record MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus Status)
{
    public bool IsAllowed => Status == MediaItemMoveTargetStatus.Allowed;
}

internal static class MediaItemMovePolicy
{
    private const string StoreProviderIdField = "Store.ProviderId";
    private const string StoreGameIdField = "Store.GameId";

    public static MediaItemMoveTargetAssessment Assess(
        MediaItem item,
        MediaNode sourceNode,
        MediaNode targetNode)
    {
        if (ReferenceEquals(sourceNode, targetNode) || sourceNode.Id == targetNode.Id)
            return new MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus.CurrentNode);

        var targetProvider = targetNode.StoreProviderId?.Trim();
        if (string.IsNullOrWhiteSpace(targetProvider))
            return new MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus.Allowed);

        var itemProvider = GetStoreValue(item, StoreProviderIdField);
        if (!string.Equals(itemProvider, targetProvider, StringComparison.OrdinalIgnoreCase))
            return new MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus.StoreProviderMismatch);

        var itemGameId = GetStoreValue(item, StoreGameIdField);
        if (string.IsNullOrWhiteSpace(itemGameId))
            return new MediaItemMoveTargetAssessment(MediaItemMoveTargetStatus.MissingStoreIdentity);

        var duplicateExists = targetNode.Items.Any(candidate =>
            !ReferenceEquals(candidate, item) &&
            string.Equals(
                GetStoreValue(candidate, StoreProviderIdField),
                itemProvider,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                GetStoreValue(candidate, StoreGameIdField),
                itemGameId,
                StringComparison.Ordinal));

        return new MediaItemMoveTargetAssessment(
            duplicateExists
                ? MediaItemMoveTargetStatus.DuplicateStoreItem
                : MediaItemMoveTargetStatus.Allowed);
    }

    public static bool IsLeavingSynchronizedNode(MediaNode sourceNode, MediaNode targetNode)
    {
        var sourceProvider = sourceNode.StoreProviderId?.Trim();
        if (string.IsNullOrWhiteSpace(sourceProvider))
            return false;

        return !string.Equals(
            sourceProvider,
            targetNode.StoreProviderId?.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetStoreValue(MediaItem item, string key)
    {
        return item.CustomFields.TryGetValue(key, out var value)
            ? value?.Trim()
            : null;
    }
}
