using System;
using System.IO;

namespace Retromind.Helpers;

/// <summary>
/// Resolves existing theme files without allowing a theme path to escape its
/// assigned directory through rooted paths, parent traversal, or symbolic links.
/// </summary>
public static class ThemePathResolver
{
    public static bool TryResolveThemeFilePath(
        string themesRoot,
        string? themePath,
        out string fullPath)
    {
        return TryResolveExistingFileInsideRoot(
            themesRoot,
            themePath,
            allowRootedPath: true,
            out fullPath);
    }

    public static bool TryResolveThemeAssetPath(
        string themeDirectory,
        string? relativePath,
        out string fullPath)
    {
        return TryResolveExistingFileInsideRoot(
            themeDirectory,
            relativePath,
            allowRootedPath: false,
            out fullPath);
    }

    private static bool TryResolveExistingFileInsideRoot(
        string rootPath,
        string? path,
        bool allowRootedPath,
        out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (!allowRootedPath && Path.IsPathRooted(path))
                return false;
            if (!Path.IsPathRooted(path) && ContainsParentTraversal(path))
                return false;

            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            if (!Directory.Exists(normalizedRoot) || IsSymbolicLink(normalizedRoot))
                return false;

            var candidate = Path.GetFullPath(
                Path.IsPathRooted(path) ? path : Path.Combine(normalizedRoot, path));
            if (!IsStrictlyInsideRoot(normalizedRoot, candidate) || !File.Exists(candidate))
                return false;

            var relative = Path.GetRelativePath(normalizedRoot, candidate);
            var current = normalizedRoot;
            foreach (var component in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                if (IsSymbolicLink(current))
                    return false;
            }

            if ((File.GetAttributes(candidate) & FileAttributes.Directory) != 0)
                return false;

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsStrictlyInsideRoot(string rootPath, string candidatePath)
    {
        var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(rootWithSeparator, FileSystemPathIdentity.Comparison);
    }

    private static bool IsSymbolicLink(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ContainsParentTraversal(string path)
    {
        foreach (var component in path.Split(
                     ['/', '\\'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (component == "..")
                return true;
        }

        return false;
    }
}
