using System;

namespace Retromind.Helpers;

/// <summary>
/// Compares paths according to the host filesystem contract used by Retromind.
/// Windows paths are case-insensitive; Linux paths retain case identity.
/// This is not intended for extensions, identifiers, URLs, or Windows metadata
/// stored inside Wine/Proton prefixes.
/// </summary>
public static class FileSystemPathIdentity
{
    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static bool Equals(string? left, string? right) =>
        string.Equals(left, right, Comparison);
}
