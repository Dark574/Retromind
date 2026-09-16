using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Retromind.Services.Stores.Gog;

internal static class GogWindowsShortcutPolicy
{
    private const string WineMenuBuilderOverride = "winemenubuilder.exe=d";

    internal static void ApplyInstallerEnvironment(
        ProcessStartInfo startInfo,
        bool createDesktopShortcut,
        bool createStartMenuShortcuts)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // Heroic also disables Wine's menu builder while running GOG setup.
        if (createDesktopShortcut || createStartMenuShortcuts)
            return;

        startInfo.Environment.TryGetValue("WINEDLLOVERRIDES", out var existing);
        if (existing?.Contains("winemenubuilder", StringComparison.OrdinalIgnoreCase) == true)
            return;

        startInfo.Environment["WINEDLLOVERRIDES"] = string.IsNullOrWhiteSpace(existing)
            ? WineMenuBuilderOverride
            : existing.TrimEnd(';') + ";" + WineMenuBuilderOverride;
    }

    internal static int RemoveUnwantedExports(
        string prefixRoot,
        bool createDesktopShortcut,
        bool createStartMenuShortcuts,
        IDictionary<string, string?>? processEnvironment = null)
    {
        if (string.IsNullOrWhiteSpace(prefixRoot) ||
            (createDesktopShortcut && createStartMenuShortcuts))
        {
            return 0;
        }

        string prefix;
        try
        {
            prefix = Path.GetFullPath(prefixRoot);
        }
        catch
        {
            return 0;
        }

        var removed = 0;
        if (!createDesktopShortcut)
        {
            var usersRoot = Path.Combine(prefix, "drive_c", "users");
            foreach (var userDirectory in EnumerateDirectories(usersRoot))
            {
                removed += DeleteDesktopFilesForPrefix(
                    Path.Combine(userDirectory, "Desktop"),
                    prefix,
                    recursive: false);
            }
        }

        if (!createStartMenuShortcuts)
        {
            var xdgDataHome = GetEnvironmentValue(processEnvironment, "XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(xdgDataHome))
            {
                var home = GetEnvironmentValue(processEnvironment, "HOME");
                if (!string.IsNullOrWhiteSpace(home))
                    xdgDataHome = Path.Combine(home, ".local", "share");
            }

            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                removed += DeleteDesktopFilesForPrefix(
                    Path.Combine(xdgDataHome, "applications", "wine"),
                    prefix,
                    recursive: true);
            }
        }

        return removed;
    }

    private static int DeleteDesktopFilesForPrefix(
        string directory,
        string prefix,
        bool recursive)
    {
        try
        {
            if (!Directory.Exists(directory))
                return 0;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var prefixMarker = $"\"WINEPREFIX={prefix}\"";
            var removed = 0;

            foreach (var path in Directory.EnumerateFiles(directory, "*.desktop", options))
            {
                try
                {
                    if (!File.ReadAllText(path).Contains(prefixMarker, StringComparison.Ordinal))
                        continue;

                    File.Delete(path);
                    removed++;
                }
                catch
                {
                    // best-effort compatibility cleanup
                }
            }

            return removed;
        }
        catch
        {
            return 0;
        }
    }

    private static string[] EnumerateDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch
        {
            return [];
        }
    }

    private static string? GetEnvironmentValue(
        IDictionary<string, string?>? processEnvironment,
        string name)
    {
        if (processEnvironment?.TryGetValue(name, out var value) == true &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(name);
    }
}
