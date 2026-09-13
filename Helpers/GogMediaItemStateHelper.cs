using System;
using System.IO;
using Retromind.Models;

namespace Retromind.Helpers;

internal static class GogMediaItemStateHelper
{
    private const string ProviderId = "gog";

    public static string? TryGetGameId(MediaItem? item)
    {
        if (item == null ||
            !item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreProviderId, out var providerId) ||
            !string.Equals(providerId, ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreGameId, out var gameId) &&
               !string.IsNullOrWhiteSpace(gameId)
            ? gameId
            : null;
    }

    public static bool IsInstalled(MediaItem? item) =>
        TryGetGameId(item) != null && HasPlayableLaunchConfiguration(item!);

    public static bool ShouldOfferInstall(MediaItem? item) =>
        TryGetGameId(item) != null && !HasPlayableLaunchConfiguration(item!);

    public static bool CanUninstall(MediaItem? item) =>
        TryGetGameId(item) != null &&
        item!.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var installPath) &&
        !string.IsNullOrWhiteSpace(installPath);

    public static bool HasUpdateAvailable(MediaItem? item) =>
        IsInstalled(item) &&
        item!.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreUpdateAvailable, out var raw) &&
        IsTruthyCustomField(raw);

    public static bool IsTruthyCustomField(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return string.Equals(raw.Trim(), "1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPlayableLaunchConfiguration(MediaItem item)
    {
        var primaryLaunchPath = item.GetPrimaryLaunchPath();
        if (!string.IsNullOrWhiteSpace(primaryLaunchPath) && File.Exists(primaryLaunchPath))
            return true;

        var launcherPath = item.LauncherPath?.Trim();
        if (string.IsNullOrWhiteSpace(launcherPath))
            return false;

        var resolvedLauncherPath = EnvironmentPathHelper.ResolveExecutablePathForExistenceCheck(launcherPath);
        return string.IsNullOrWhiteSpace(resolvedLauncherPath) || File.Exists(resolvedLauncherPath);
    }
}
