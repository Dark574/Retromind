using System;
using System.IO;
using Retromind.Models;
using Retromind.Models.Stores;

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

    internal static bool IsInstalled(MediaItem? item) =>
        TryGetGameId(item) != null && HasPlayableLaunchConfiguration(item!);

    internal static bool ShouldOfferInstall(MediaItem? item) =>
        TryGetGameId(item) != null && !HasPlayableLaunchConfiguration(item!);

    internal static bool IsRetromindManagedInstallation(MediaItem? item) =>
        IsInstalled(item) &&
        item!.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var installPath) &&
        !string.IsNullOrWhiteSpace(installPath);

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
}
