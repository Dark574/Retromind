using System;
using System.Collections.Generic;
using System.IO;

namespace Retromind.Helpers;

public static class EnvironmentPathHelper
{
    private static readonly HashSet<string> DataRootPathKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROTONPATH",
        "STEAM_COMPAT_DATA_PATH"
    };

    public static string NormalizeDataRootPathIfNeeded(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return value ?? string.Empty;

        if (!DataRootPathKeys.Contains(key))
            return value ?? string.Empty;

        var raw = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        if (Path.IsPathRooted(raw))
            return raw;

        return AppPaths.ResolveDataPath(raw);
    }
}
