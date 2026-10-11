using System;
using System.Globalization;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.SteamAchievements;

public static class SteamAppIdResolver
{
    private const string RunGameIdPrefix = "steam://rungameid/";

    public static bool TryGetAppId(MediaItem? item, out uint appId)
    {
        appId = 0;
        if (!string.Equals(
                StoreProviderBadgeHelper.GetProviderId(item),
                StoreProviderBadgeHelper.SteamProviderId,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (item!.CustomFields.TryGetValue(
                CustomFieldKeyHelper.StoreGameId,
                out var storedAppId) &&
            TryParseAppId(storedAppId, out appId))
        {
            return true;
        }

        var arguments = item.LauncherArgs?.Trim();
        if (string.IsNullOrWhiteSpace(arguments))
            return false;

        var prefixIndex = arguments.IndexOf(
            RunGameIdPrefix,
            StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
            return false;

        var valueStart = prefixIndex + RunGameIdPrefix.Length;
        var valueEnd = valueStart;
        while (valueEnd < arguments.Length && char.IsAsciiDigit(arguments[valueEnd]))
            valueEnd++;

        return TryParseAppId(
            arguments[valueStart..valueEnd],
            out appId);
    }

    private static bool TryParseAppId(string? value, out uint appId)
    {
        return uint.TryParse(
                   value?.Trim(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out appId) &&
               appId > 0;
    }
}
