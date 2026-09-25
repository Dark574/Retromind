using System;
using System.IO;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Resolves and validates paths stored by Retromind runner definitions.
/// Free-form PROTONPATH/WINE environment overrides are intentionally outside
/// this contract because UMU also accepts aliases such as "GE-Proton".
/// </summary>
public static class RunnerVersionPathHelper
{
    public static string? ResolveExecutablePath(RunnerVersionKind kind, string? configuredPath)
    {
        var resolvedPath = ResolveConfiguredPath(configuredPath);
        if (resolvedPath == null)
            return null;

        if (File.Exists(resolvedPath))
            return kind == RunnerVersionKind.Wine ? resolvedPath : null;

        if (!Directory.Exists(resolvedPath))
            return null;

        if (kind == RunnerVersionKind.Wine)
        {
            var binWine = Path.Combine(resolvedPath, "bin", "wine");
            if (File.Exists(binWine))
                return binWine;

            var rootWine = Path.Combine(resolvedPath, "wine");
            return File.Exists(rootWine) ? rootWine : null;
        }

        var proton = Path.Combine(resolvedPath, "proton");
        var toolManifest = Path.Combine(resolvedPath, "toolmanifest.vdf");
        return File.Exists(proton) && File.Exists(toolManifest) ? proton : null;
    }

    public static string? ResolveConfiguredPath(string? configuredPath)
    {
        var trimmedPath = configuredPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPath))
            return null;

        try
        {
            if (Path.IsPathRooted(trimmedPath))
                return Path.GetFullPath(trimmedPath);

            return AppPaths.TryResolveDataPathInsideRoot(trimmedPath, out var resolvedPath)
                ? resolvedPath
                : null;
        }
        catch
        {
            return null;
        }
    }
}
