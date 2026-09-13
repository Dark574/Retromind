using System.IO;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

internal static class GogLaunchConfigurationHelper
{
    public static string? ResolveRunnerExecutablePath(RunnerVersionKind kind, string? configuredPath)
    {
        var trimmedPath = configuredPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedPath))
            return null;

        try
        {
            string resolvedPath;
            if (Path.IsPathRooted(trimmedPath))
            {
                resolvedPath = Path.GetFullPath(trimmedPath);
            }
            else if (!AppPaths.TryResolveDataPathInsideRoot(trimmedPath, out resolvedPath))
            {
                return null;
            }

            if (File.Exists(resolvedPath))
            {
                // Wine may be configured as a direct executable. PROTONPATH,
                // however, must point to the runner directory containing "proton".
                return kind == RunnerVersionKind.Wine ? resolvedPath : null;
            }

            if (!Directory.Exists(resolvedPath))
                return null;

            if (kind == RunnerVersionKind.Wine)
            {
                var binWine = Path.Combine(resolvedPath, "bin", "wine");
                if (File.Exists(binWine))
                    return binWine;

                var rootWine = Path.Combine(resolvedPath, "wine");
                if (File.Exists(rootWine))
                    return rootWine;
            }
            else
            {
                var proton = Path.Combine(resolvedPath, "proton");
                if (File.Exists(proton))
                    return proton;
            }
        }
        catch
        {
            // Invalid or inaccessible runner paths are handled as unavailable.
        }

        return null;
    }

    public static void SetInstalledMediaType(MediaItem item, GogInstallPlatform platform)
    {
        item.MediaType = platform == GogInstallPlatform.Windows
            ? MediaType.Emulator
            : MediaType.Native;
        item.EmulatorId = null;
    }

    public static void ClearAfterUninstall(MediaItem item)
    {
        item.MediaType = MediaType.Native;
        item.EmulatorId = null;
        item.LauncherPath = null;
        item.LauncherArgs = null;
        item.RunnerVersionId = null;
    }
}
