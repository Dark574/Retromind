using System;
using System.IO;
using System.Linq;

namespace Retromind.Helpers;

public static class AppPaths
{
    /// <summary>
    /// Writable "portable" root for app data (Library, JSON files, etc.).
    /// - AppImage: directory containing the AppImage file (ENV: APPIMAGE)
    /// - Fallback: AppContext.BaseDirectory (normal publish/run)
    /// </summary>
    public static string DataRoot
    {
        get
        {
            var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImagePath))
            {
                var dir = Path.GetDirectoryName(appImagePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }

            return AppContext.BaseDirectory;
        }
    }

    public static string LibraryRoot => Path.Combine(DataRoot, "Library");
    
    // Themes live in the portable data root so users can edit them next to the AppImage.
    public static string ThemesRoot => Path.Combine(DataRoot, "Themes");
    
    // --- path helpers for portable storage ---

    /// <summary>
    /// Ensures Themes exist in DataRoot (portable).
    /// Copies shipped Themes from AppContext.BaseDirectory ONLY if target is empty.
    /// Never overwrites user themes.
    /// </summary>
    public static void EnsurePortableThemes()
    {
        try
        {
            if (!Directory.Exists(ThemesRoot))
                Directory.CreateDirectory(ThemesRoot);

            // If the target already contains anything, do nothing (user may have customized themes).
            if (Directory.EnumerateFileSystemEntries(ThemesRoot).Any())
                return;

            // Copy defaults shipped with the app (build output).
            var shippedThemesRoot = Path.Combine(AppContext.BaseDirectory, "Themes");
            if (!Directory.Exists(shippedThemesRoot))
                return;

            CopyDirectoryRecursive(shippedThemesRoot, ThemesRoot);
        }
        catch
        {
            // best-effort; themes must never break startup
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSub);
        }
    }
    
    /// <summary>
    /// Resolves a stored path to an absolute path under DataRoot.
    /// If the input is already absolute, it is returned unchanged.
    /// </summary>
    public static string ResolveDataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DataRoot;

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(DataRoot, path));
    }

    /// <summary>
    /// Converts an absolute path to a DataRoot-relative path (portable).
    /// If the path is already relative, it is returned normalized.
    /// </summary>
    public static string MakeDataRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Path.IsPathRooted(path)
            ? Path.GetRelativePath(DataRoot, path)
            : path;
    }
}