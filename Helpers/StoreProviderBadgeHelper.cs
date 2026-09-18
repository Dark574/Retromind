using System;
using Retromind.Models;

namespace Retromind.Helpers;

internal static class StoreProviderBadgeHelper
{
    public const string GogProviderId = "gog";
    public const string SteamProviderId = "steam";
    public const string EpicProviderId = "epic";

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
        var launchPath = item.GetPrimaryLaunchPath();
        if (IsCommand(launchPath, SteamProviderId) || StartsWithProtocol(item.LauncherArgs, "steam://"))
            return SteamProviderId;

        if (IsCommand(launchPath, "heroic") ||
            StartsWithProtocol(item.LauncherArgs, "epic://") ||
            StartsWithProtocol(item.LauncherArgs, "heroic://"))
        {
            return EpicProviderId;
        }

        return null;
    }

    public static string? GetBadgeText(MediaItem? item) => GetProviderId(item) switch
    {
        GogProviderId => "GOG",
        SteamProviderId => "STEAM",
        EpicProviderId => "EPIC",
        _ => null
    };

    public static string? GetToolTip(MediaItem? item) => GetProviderId(item) switch
    {
        GogProviderId => "GOG",
        SteamProviderId => "Steam",
        EpicProviderId => "Epic Games (via Heroic)",
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
