using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores;

internal enum StoreLinkChangeBlockReason
{
    None,
    UnsupportedCurrentProvider,
    AlreadyLinked,
    ManagedInstallation
}

internal static class StoreLinkChangeService
{
    internal static StoreLinkChangeBlockReason GetBlockReason(
        MediaItem item,
        string targetProviderId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProviderId);

        var currentProviderId = StoreProviderBadgeHelper.GetProviderId(item);
        if (currentProviderId != null &&
            !string.Equals(currentProviderId, StoreProviderIds.Gog, StringComparison.Ordinal) &&
            !string.Equals(currentProviderId, StoreProviderIds.Epic, StringComparison.Ordinal))
        {
            return StoreLinkChangeBlockReason.UnsupportedCurrentProvider;
        }

        if (string.Equals(currentProviderId, targetProviderId, StringComparison.Ordinal) &&
            StoreGameIdentityHelper.HasStoredIdentity(item))
        {
            return StoreLinkChangeBlockReason.AlreadyLinked;
        }

        return item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var installPath) &&
               !string.IsNullOrWhiteSpace(installPath)
            ? StoreLinkChangeBlockReason.ManagedInstallation
            : StoreLinkChangeBlockReason.None;
    }

    internal static Dictionary<string, string> BuildChangedFields(
        IReadOnlyDictionary<string, string> previousFields,
        string providerId,
        string gameId)
    {
        ArgumentNullException.ThrowIfNull(previousFields);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);

        var updatedFields = previousFields
            .Where(entry => !CustomFieldKeyHelper.IsInternal(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        updatedFields[CustomFieldKeyHelper.StoreProviderId] = providerId;
        updatedFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        return updatedFields;
    }

    internal static async Task ApplyAndSaveAsync(
        MediaItem item,
        Dictionary<string, string> updatedFields,
        Func<Task<bool>> saveAsync,
        Action markDirty,
        string saveFailureMessage,
        Action? beforeSave = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(updatedFields);
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentNullException.ThrowIfNull(markDirty);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFailureMessage);

        var previousFields = item.CustomFields;
        var previousGogDlcInstallations = item.GogDlcInstallations;
        item.CustomFields = updatedFields;
        item.GogDlcInstallations = null;

        try
        {
            beforeSave?.Invoke();
            markDirty();
            if (!await saveAsync())
                throw new IOException(saveFailureMessage);
        }
        catch
        {
            item.CustomFields = previousFields;
            item.GogDlcInstallations = previousGogDlcInstallations;
            markDirty();
            throw;
        }
    }
}
