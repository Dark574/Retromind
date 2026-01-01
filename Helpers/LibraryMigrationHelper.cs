using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// One-time helper to migrate launcher file paths for portable setups.
/// Converts absolute file paths that reside under AppPaths.DataRoot into
/// LibraryRelative paths, so the library becomes robust when the Retromind
/// folder is moved together with the games
/// </summary>
public static class LibraryMigrationHelper
{
    /// <summary>
    /// Scans the entire media tree and converts absolute launch file paths
    /// that live inside AppPaths.DataRoot into LibraryRelative paths
    ///
    /// This is safe to call multiple times; already-migrated entries are skipped
    /// Returns the number of MediaFileRef entries that were modified
    /// </summary>
    public static int MigrateLaunchFilePathsToLibraryRelative(ObservableCollection<MediaNode> rootNodes)
    {
        if (rootNodes == null) throw new ArgumentNullException(nameof(rootNodes));

        var migratedCount = 0;
        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;

        foreach (var root in rootNodes)
        {
            migratedCount += MigrateNodeRecursive(root, dataRoot, dataRootWithSep);
        }

        return migratedCount;
    }

    private static int MigrateNodeRecursive(MediaNode node, string dataRoot, string dataRootWithSep)
    {
        var migrated = 0;

        // Migrate all launch files for this node's items
        foreach (var item in node.Items)
        {
            if (item.Files == null || item.Files.Count == 0)
                continue;

            foreach (var fileRef in item.Files)
            {
                if (fileRef == null)
                    continue;

                if (fileRef.Kind != MediaFileKind.Absolute)
                    continue;

                if (string.IsNullOrWhiteSpace(fileRef.Path))
                    continue;

                var path = fileRef.Path;
                if (!Path.IsPathRooted(path))
                    continue;

                try
                {
                    var normalizedAbsolute = Path.GetFullPath(path);

                    // Only migrate if the file is actually under DataRoot
                    if (!normalizedAbsolute.StartsWith(dataRootWithSep, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(normalizedAbsolute, dataRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Compute path relative to DataRoot and switch kind to LibraryRelative
                    var relative = Path.GetRelativePath(dataRoot, normalizedAbsolute);

                    fileRef.Kind = MediaFileKind.LibraryRelative;
                    fileRef.Path = relative;

                    migrated++;
                }
                catch
                {
                    // Best-effort migration: if anything fails, keep the original absolute path
                }
            }
        }

        // Recurse into children
        foreach (var child in node.Children)
        {
            migrated += MigrateNodeRecursive(child, dataRoot, dataRootWithSep);
        }

        return migrated;
    }
}