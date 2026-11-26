using System;
using System.IO;
using Retromind.Models;
using Retromind.Models.Stores;

namespace Retromind.Helpers;

internal static class StoreProviderBadgeHelper
{
    public const string GogProviderId = StoreProviderIds.Gog;
    public const string SteamProviderId = StoreProviderIds.Steam;
    public const string EpicProviderId = StoreProviderIds.Epic;

    public static string? GetProviderId(MediaItem? item)
    {
        if (item == null)
            return null;

        if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreProviderId, out var storedProviderId))
        {
            var normalizedProviderId = NormalizeProviderId(storedProviderId);
            if (normalizedProviderId != null)
                return normalizedProviderId;
        }

        // Older Steam and Heroic imports predate the structured store fields.
        // Keep them recognizable without mutating the user's library on load.
        return GetFallbackProviderId(item.GetPrimaryLaunchPath(), item.LauncherArgs);
    }

    internal static string? GetProviderId(MediaSearchDocument item)
    {
        var normalized = NormalizeProviderId(item.StoredStoreProviderId);
        return normalized ?? GetFallbackProviderId(GetFrozenLaunchPath(item), item.StoreLauncherArgs);
    }

    private static string? GetFallbackProviderId(string? launchPath, string? launcherArgs)
    {
        if (IsCommand(launchPath, SteamProviderId) || StartsWithProtocol(launcherArgs, "steam://"))
            return SteamProviderId;

        if (IsCommand(launchPath, "heroic") ||
            StartsWithProtocol(launcherArgs, "epic://") ||
            StartsWithProtocol(launcherArgs, "heroic://"))
        {
            return EpicProviderId;
        }

        return null;
    }

    private static string? GetFrozenLaunchPath(MediaSearchDocument item)
    {
        if (item.StoreFileCount == 0) return null;
        // Same stable ordering as MediaItem.GetPrimaryFile: first Index=1, otherwise
        // smallest positive index, otherwise first file. Only frozen value records are read.
        var primary = item.StoreFileAt(0);
        int? index = primary.Index is > 0 ? primary.Index : null;
        for (var i = 1; i < item.StoreFileCount && index != 1; i++)
        {
            var file = item.StoreFileAt(i);
            if (file.Index is > 0 && (!index.HasValue || file.Index < index.Value))
            {
                primary = file;
                index = file.Index;
            }
        }

        if (string.IsNullOrWhiteSpace(primary.Path)) return null;
        if (primary.Kind != MediaFileKind.LibraryRelative) return primary.Path;
        if (item.StoreDataRoot == null) return null;
        try
        {
            // Match AppPaths.TryResolveDataPathInsideRoot, using the root frozen by Capture.
            // No environment, working-directory or live-model lookups on the worker.
            var candidate = Path.IsPathFullyQualified(primary.Path)
                ? Path.GetFullPath(primary.Path)
                : Path.GetFullPath(Path.Combine(item.StoreDataRoot, primary.Path));
            return FileSystemPathIdentity.Equals(candidate, item.StoreDataRoot) ||
                   candidate.StartsWith(item.StoreDataRootPrefix!, FileSystemPathIdentity.Comparison)
                ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetBadgeText(MediaItem? item) =>
        GetBadgeText(GetProviderId(item));

    public static string? GetBadgeText(string? providerId) =>
        NormalizeProviderId(providerId) switch
        {
            GogProviderId => "GOG",
            SteamProviderId => "STEAM",
            EpicProviderId => "EPIC",
            _ => null
        };

    public static string? GetToolTip(MediaItem? item) =>
        GetToolTip(GetProviderId(item));

    public static string? GetToolTip(string? providerId) =>
        NormalizeProviderId(providerId) switch
        {
            GogProviderId => "GOG",
            SteamProviderId => "Steam",
            EpicProviderId => "Epic Games",
            _ => null
        };

    private static string? NormalizeProviderId(string? providerId)
    {
        var trimmed = providerId?.Trim();
        if (string.Equals(trimmed, GogProviderId, StringComparison.OrdinalIgnoreCase))
            return GogProviderId;
        if (string.Equals(trimmed, SteamProviderId, StringComparison.OrdinalIgnoreCase))
            return SteamProviderId;
        if (string.Equals(trimmed, EpicProviderId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "heroic", StringComparison.OrdinalIgnoreCase))
        {
            return EpicProviderId;
        }

        return null;
    }

    private static bool IsCommand(string? value, string command)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim().Trim('"', '\'');
        return string.Equals(trimmed, command, StringComparison.OrdinalIgnoreCase) ||
               trimmed.EndsWith('/' + command, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithProtocol(string? value, string protocol) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.TrimStart().StartsWith(protocol, StringComparison.OrdinalIgnoreCase);
}
