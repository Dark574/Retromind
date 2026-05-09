using System;

namespace Retromind.Helpers;

public static class LaunchRuntimeHelper
{
    public static bool ContainsUmuToken(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains("umu", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsProtonToken(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains("proton", StringComparison.OrdinalIgnoreCase);
}
