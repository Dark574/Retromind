using System;
using System.Collections.Generic;
using System.Linq;

namespace Retromind.Helpers;

public static class LaunchRuntimeHelper
{
    public static bool ContainsProtonHints(IReadOnlyDictionary<string, string>? env)
        => env != null && env.Keys.Any(k =>
            string.Equals(k, "PROTONPATH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(k, "STEAM_COMPAT_DATA_PATH", StringComparison.OrdinalIgnoreCase));

    public static bool ContainsUmuHints(IReadOnlyDictionary<string, string>? env)
        => env != null && env.Keys.Any(k =>
            k.StartsWith("UMU_", StringComparison.OrdinalIgnoreCase));

    public static bool ContainsUmuToken(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains("umu", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsProtonToken(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains("proton", StringComparison.OrdinalIgnoreCase);
}
