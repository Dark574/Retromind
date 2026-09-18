using System;
using Retromind.Models;

namespace Retromind.Helpers;

internal static class StoreGameIdentityHelper
{
    private const string SteamLaunchPrefix = "steam://rungameid/";
    private const string EpicLaunchPrefix = "epic://";

    public static bool IsSameGame(MediaItem left, MediaItem right)
    {
        if (!TryGetIdentity(left, out var leftProviderId, out var leftGameId) ||
            !TryGetIdentity(right, out var rightProviderId, out var rightGameId))
        {
            return false;
        }

        return string.Equals(leftProviderId, rightProviderId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(leftGameId, rightGameId, StringComparison.Ordinal);
    }

    private static bool TryGetIdentity(
        MediaItem item,
        out string providerId,
        out string gameId)
    {
        providerId = StoreProviderBadgeHelper.GetProviderId(item) ?? string.Empty;
        gameId = GetStoredGameId(item) ?? GetLegacyGameId(item, providerId) ?? string.Empty;
        return providerId.Length > 0 && gameId.Length > 0;
    }

    private static string? GetStoredGameId(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreGameId, out var gameId))
            return null;

        var trimmed = gameId?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? GetLegacyGameId(MediaItem item, string providerId)
    {
        var prefix = providerId switch
        {
            StoreProviderBadgeHelper.SteamProviderId => SteamLaunchPrefix,
            StoreProviderBadgeHelper.EpicProviderId => EpicLaunchPrefix,
            _ => null
        };

        if (prefix == null || string.IsNullOrWhiteSpace(item.LauncherArgs))
            return null;

        var launchArguments = item.LauncherArgs.Trim();
        if (!launchArguments.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var gameId = launchArguments[prefix.Length..].Trim();
        return gameId.Length == 0 ? null : gameId;
    }
}
