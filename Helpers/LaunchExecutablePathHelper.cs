using System;
using System.IO;

namespace Retromind.Helpers;

public readonly record struct LaunchExecutableResolution(
    string LaunchValue,
    string? ResolvedPath,
    bool UsesPathLookup)
{
    public bool IsAvailable => !string.IsNullOrWhiteSpace(ResolvedPath);
}

/// <summary>
/// Applies the executable-path contract shared by launch execution and the
/// settings diagnostics shown for emulator profiles.
/// </summary>
public static class LaunchExecutablePathHelper
{
    public static string ResolveConfiguredPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return string.Empty;

        var trimmed = configuredPath.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        // Keep command tokens (e.g. "wine", "flatpak", "retroarch") PATH-resolved.
        // Relative paths with separators are treated as DataRoot-relative for portability.
        if (IsCommandToken(trimmed))
            return trimmed;

        return AppPaths.ResolveDataPath(trimmed);
    }

    public static LaunchExecutableResolution ResolveForDisplay(
        string? configuredPath,
        string? pathValue = null)
    {
        var launchValue = ResolveConfiguredPath(configuredPath);
        if (string.IsNullOrWhiteSpace(launchValue))
            return new LaunchExecutableResolution(string.Empty, null, UsesPathLookup: false);

        if (IsCommandToken(launchValue))
        {
            var effectivePath = pathValue ?? Environment.GetEnvironmentVariable("PATH");
            var resolved = EnvironmentPathHelper.TryFindExecutableInPath(launchValue, effectivePath);
            return new LaunchExecutableResolution(launchValue, resolved, UsesPathLookup: true);
        }

        string? resolvedPath = null;
        try
        {
            if (File.Exists(launchValue))
                resolvedPath = Path.GetFullPath(launchValue);
        }
        catch
        {
            // Invalid or inaccessible paths are represented as unavailable.
        }

        return new LaunchExecutableResolution(launchValue, resolvedPath, UsesPathLookup: false);
    }

    public static bool IsWineExecutable(string? resolvedPath)
    {
        var fileName = Path.GetFileName(resolvedPath);
        return string.Equals(fileName, "wine", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fileName, "wine64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommandToken(string value)
        => !value.Contains('/') &&
           !value.Contains('\\') &&
           !value.StartsWith(".", StringComparison.Ordinal);
}
