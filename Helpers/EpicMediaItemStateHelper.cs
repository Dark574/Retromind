using System;
using System.IO;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores;

namespace Retromind.Helpers;

internal static class EpicMediaItemStateHelper
{
    internal static string? TryGetGameId(MediaItem? item)
    {
        if (item == null ||
            !StoreGameIdentityHelper.TryGetIdentity(item, out var providerId, out var gameId) ||
            !string.Equals(providerId, StoreProviderIds.Epic, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return gameId;
    }

    internal static bool IsLaunchReady(MediaItem? item) =>
        TryGetGameId(item) != null && HasPlayableLaunchConfiguration(item!);

    internal static bool ShouldOfferInstall(MediaItem? item) =>
        TryGetGameId(item) != null &&
        !IsRetromindManagedInstallation(item) &&
        !HasPlayableLaunchConfiguration(item!);

    internal static bool IsRetromindManagedInstallation(MediaItem? item) =>
        TryResolveManagedInstallPath(item, out var installPath) &&
        Directory.Exists(installPath);

    internal static bool HasRetromindManagedInstallMetadata(MediaItem? item) =>
        TryGetGameId(item) != null &&
        item!.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var installPath) &&
        !string.IsNullOrWhiteSpace(installPath);

    internal static bool CanCheckUpdates(MediaItem? item) =>
        IsRetromindManagedInstallation(item);

    internal static bool HasUpdateAvailable(MediaItem? item) =>
        IsRetromindManagedInstallation(item) &&
        item!.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreUpdateAvailable, out var raw) &&
        IsTruthyCustomField(raw);

    internal static string BuildDefaultPrefixPath(string appName, string? gameTitle)
    {
        var safeGameId = PrefixPathHelper.SanitizePrefixFolderName(appName);
        var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(gameTitle ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = "Unknown";

        return Path.Combine("Prefixes", $"epic_{safeGameId}_{safeTitle}");
    }

    private static bool HasPlayableLaunchConfiguration(MediaItem item)
    {
        var launchPath = item.GetPrimaryLaunchPath();
        if (!string.IsNullOrWhiteSpace(launchPath))
        {
            if (File.Exists(launchPath))
                return true;

            var resolvedCommand = EnvironmentPathHelper.ResolveExecutablePathForExistenceCheck(launchPath);
            if (string.IsNullOrWhiteSpace(resolvedCommand) || File.Exists(resolvedCommand))
                return true;
        }

        var launcherPath = item.LauncherPath?.Trim();
        if (string.IsNullOrWhiteSpace(launcherPath))
            return false;

        var resolvedLauncher = EnvironmentPathHelper.ResolveExecutablePathForExistenceCheck(launcherPath);
        return string.IsNullOrWhiteSpace(resolvedLauncher) || File.Exists(resolvedLauncher);
    }

    private static bool TryResolveManagedInstallPath(MediaItem? item, out string installPath)
    {
        installPath = string.Empty;
        return HasRetromindManagedInstallMetadata(item) &&
               item!.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedPath) &&
               StoreInstallPathHelper.TryResolveStoredPath(storedPath, out installPath);
    }

    private static bool IsTruthyCustomField(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return bool.TryParse(raw, out var parsed)
            ? parsed
            : string.Equals(raw.Trim(), "1", StringComparison.OrdinalIgnoreCase);
    }
}
