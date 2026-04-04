using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Helpers;

public static class EnvironmentPathHelper
{
    private static readonly HashSet<string> DataRootPathKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROTONPATH",
        "STEAM_COMPAT_DATA_PATH",
        "HOME",
        "DOTNET_CLI_HOME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME"
    };

    public static bool IsDataRootPathKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return DataRootPathKeys.Contains(key);
    }

    public static string NormalizeDataRootPathIfNeeded(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return value ?? string.Empty;

        if (!IsDataRootPathKey(key))
            return value ?? string.Empty;

        var raw = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        if (Path.IsPathRooted(raw))
            return raw;

        return AppPaths.ResolveDataPath(raw);
    }
}
