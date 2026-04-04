using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// One-time helper to migrate launcher file paths for portable setups.
/// Converts absolute launch-related paths that reside under AppPaths.DataRoot
/// into portable relative paths, so the library becomes robust when the
/// Retromind folder is moved together with media and launcher files.
/// </summary>
public static class LibraryMigrationHelper
{
    private static readonly HashSet<string> EnvironmentPathKeys = new(StringComparer.OrdinalIgnoreCase)
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

    /// <summary>
    /// Scans the entire media tree and converts absolute launch-related paths
    /// that live inside AppPaths.DataRoot into portable relative paths.
    ///
    /// This is safe to call multiple times; already-migrated entries are skipped
    /// Returns the number of modified fields/entries.
    /// </summary>
    public static int MigrateLaunchFilePathsToLibraryRelative(ObservableCollection<MediaNode> rootNodes)
    {
        if (rootNodes == null) throw new ArgumentNullException(nameof(rootNodes));

        var migratedCount = 0;
        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;
        var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
        var libraryRootWithSep = libraryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? libraryRoot
            : libraryRoot + Path.DirectorySeparatorChar;

        foreach (var root in rootNodes)
        {
            migratedCount += MigrateNodeRecursive(
                root,
                dataRoot,
                dataRootWithSep,
                libraryRoot,
                libraryRootWithSep);
        }

        return migratedCount;
    }

    private static int MigrateNodeRecursive(
        MediaNode node,
        string dataRoot,
        string dataRootWithSep,
        string libraryRoot,
        string libraryRootWithSep)
    {
        var migrated = 0;

        migrated += MigrateEnvironmentOverrides(node.EnvironmentOverrides, dataRoot, dataRootWithSep);
        migrated += MigrateWrappers(node.NativeWrappersOverride, dataRoot, dataRootWithSep);

        // Migrate launch-related data for this node's items.
        foreach (var item in node.Items)
        {
            if (item.Files is { Count: > 0 })
            {
                foreach (var fileRef in item.Files)
                {
                    if (fileRef == null ||
                        fileRef.Kind != MediaFileKind.Absolute ||
                        string.IsNullOrWhiteSpace(fileRef.Path) ||
                        !Path.IsPathRooted(fileRef.Path))
                        continue;

                    if (!TryMakeDataRootRelative(fileRef.Path, dataRoot, dataRootWithSep, out var relative))
                        continue;

                    fileRef.Kind = MediaFileKind.LibraryRelative;
                    fileRef.Path = relative;
                    migrated++;
                }
            }

            migrated += MigrateItemLaunchSettings(
                item,
                dataRoot,
                dataRootWithSep,
                libraryRoot,
                libraryRootWithSep);
        }

        // Recurse into children
        foreach (var child in node.Children)
        {
            migrated += MigrateNodeRecursive(
                child,
                dataRoot,
                dataRootWithSep,
                libraryRoot,
                libraryRootWithSep);
        }

        return migrated;
    }

    private static int MigrateItemLaunchSettings(
        MediaItem item,
        string dataRoot,
        string dataRootWithSep,
        string libraryRoot,
        string libraryRootWithSep)
    {
        var migrated = 0;

        if (TryMakeDataRootRelative(item.LauncherPath, dataRoot, dataRootWithSep, out var launcherPath))
        {
            item.LauncherPath = launcherPath;
            migrated++;
        }

        if (TryMakeDataRootRelative(item.WorkingDirectory, dataRoot, dataRootWithSep, out var workingDirectory))
        {
            item.WorkingDirectory = workingDirectory;
            migrated++;
        }

        if (TryMakeDataRootRelative(item.XdgConfigPath, dataRoot, dataRootWithSep, out var xdgConfig))
        {
            item.XdgConfigPath = xdgConfig;
            migrated++;
        }

        if (TryMakeDataRootRelative(item.XdgDataPath, dataRoot, dataRootWithSep, out var xdgData))
        {
            item.XdgDataPath = xdgData;
            migrated++;
        }

        if (TryMakeDataRootRelative(item.XdgCachePath, dataRoot, dataRootWithSep, out var xdgCache))
        {
            item.XdgCachePath = xdgCache;
            migrated++;
        }

        if (TryMakeDataRootRelative(item.XdgStatePath, dataRoot, dataRootWithSep, out var xdgState))
        {
            item.XdgStatePath = xdgState;
            migrated++;
        }

        if (TryMakeDataRootRelative(item.XdgBasePath, dataRoot, dataRootWithSep, out var xdgBase))
        {
            item.XdgBasePath = xdgBase;
            migrated++;
        }

        if (TryMakeLibraryRootRelative(item.PrefixPath, libraryRoot, libraryRootWithSep, out var prefixPath))
        {
            item.PrefixPath = prefixPath;
            migrated++;
        }

        migrated += MigrateWrappers(item.NativeWrappersOverride, dataRoot, dataRootWithSep);
        migrated += MigrateEnvironmentOverrides(item.EnvironmentOverrides, dataRoot, dataRootWithSep);
        return migrated;
    }

    private static bool TryMakeLibraryRootRelative(
        string? absolutePath,
        string libraryRoot,
        string libraryRootWithSep,
        out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        if (!Path.IsPathRooted(absolutePath))
            return false;

        try
        {
            var normalizedAbsolute = Path.GetFullPath(absolutePath);
            if (!normalizedAbsolute.StartsWith(libraryRootWithSep, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedAbsolute, libraryRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(libraryRoot, normalizedAbsolute);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int MigrateWrappers(
        List<LaunchWrapper>? wrappers,
        string dataRoot,
        string dataRootWithSep)
    {
        if (wrappers == null || wrappers.Count == 0)
            return 0;

        var migrated = 0;
        foreach (var wrapper in wrappers)
        {
            if (wrapper == null || string.IsNullOrWhiteSpace(wrapper.Path))
                continue;

            if (!TryMakeDataRootRelative(wrapper.Path, dataRoot, dataRootWithSep, out var relative))
                continue;

            wrapper.Path = relative;
            migrated++;
        }

        return migrated;
    }

    private static int MigrateEnvironmentOverrides(
        Dictionary<string, string>? overrides,
        string dataRoot,
        string dataRootWithSep)
    {
        if (overrides == null || overrides.Count == 0)
            return 0;

        var migrated = 0;
        var keys = overrides.Keys.ToList();
        foreach (var key in keys)
        {
            if (!EnvironmentPathKeys.Contains(key))
                continue;

            if (!overrides.TryGetValue(key, out var value))
                continue;

            if (!TryMakeDataRootRelative(value, dataRoot, dataRootWithSep, out var relative))
                continue;

            overrides[key] = relative;
            migrated++;
        }

        return migrated;
    }

    private static bool TryMakeDataRootRelative(
        string? absolutePath,
        string dataRoot,
        string dataRootWithSep,
        out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        if (!Path.IsPathRooted(absolutePath))
            return false;

        try
        {
            var normalizedAbsolute = Path.GetFullPath(absolutePath);
            if (!normalizedAbsolute.StartsWith(dataRootWithSep, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedAbsolute, dataRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(dataRoot, normalizedAbsolute);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
