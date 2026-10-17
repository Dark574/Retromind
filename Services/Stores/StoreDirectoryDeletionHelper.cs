using System;
using System.IO;
using System.Threading;

namespace Retromind.Services.Stores;

/// <summary>
/// Deletes store-managed directory trees without following symbolic links.
/// Callers remain responsible for provider-specific containment and ownership checks.
/// </summary>
internal static class StoreDirectoryDeletionHelper
{
    internal static bool ContainsSymbolicLinkInPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
            return IsSymbolicLink(fullPath);

        var currentPath = root;
        foreach (var component in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);

            try
            {
                if (IsSymbolicLink(currentPath))
                    return true;
            }
            catch (FileNotFoundException)
            {
                // Once a component is missing, no deeper component can exist.
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    internal static void DeleteDirectoryTreeWithoutFollowingLinks(
        string directoryPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            return;

        var fullPath = Path.GetFullPath(directoryPath);
        if (ContainsSymbolicLinkInPath(fullPath))
        {
            throw new IOException(
                $"Refusing to delete directory '{fullPath}' because its path contains a symbolic link.");
        }

        if (!Directory.Exists(fullPath))
            return;

        DeleteDirectoryEntryWithoutFollowingLinks(fullPath, isDeletionRoot: true, ct);
    }

    private static void DeleteDirectoryEntryWithoutFollowingLinks(
        string path,
        bool isDeletionRoot,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var attributes = File.GetAttributes(path);
        var isSymbolicLink = (attributes & FileAttributes.ReparsePoint) != 0;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;

        if (isSymbolicLink)
        {
            if (isDeletionRoot)
                throw new IOException($"Refusing to delete symbolic-link root '{path}'.");

            DeleteLink(path, isDirectory);
            return;
        }

        if (!isDirectory)
        {
            File.Delete(path);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            DeleteDirectoryEntryWithoutFollowingLinks(entry, isDeletionRoot: false, ct);

        Directory.Delete(path, recursive: false);
    }

    private static void DeleteLink(string path, bool isDirectory)
    {
        if (isDirectory)
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }

    private static bool IsSymbolicLink(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
