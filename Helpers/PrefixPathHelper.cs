using System;
using System.IO;

namespace Retromind.Helpers;

public static class PrefixPathHelper
{
    public static string SanitizePrefixFolderName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var safe = input.Replace(' ', '_');

        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c.ToString(), string.Empty);

        while (safe.Contains("__", StringComparison.Ordinal))
            safe = safe.Replace("__", "_", StringComparison.Ordinal);

        // Keep it readable, but avoid pathological lengths in folder names.
        const int maxLen = 80;
        if (safe.Length > maxLen)
            safe = safe[..maxLen];

        return safe;
    }

    public static bool IsPfxPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmed), "pfx", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWinePrefixInitialized(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!Directory.Exists(path))
            return false;

        var systemReg = Path.Combine(path, "system.reg");
        if (File.Exists(systemReg))
            return true;

        var driveC = Path.Combine(path, "drive_c");
        return Directory.Exists(driveC);
    }

    public static bool TryMakeLibraryRelativeIfInsideLibraryRoot(
        string absolutePath,
        string libraryRoot,
        out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(absolutePath) || string.IsNullOrWhiteSpace(libraryRoot))
            return false;

        if (!Path.IsPathRooted(absolutePath))
            return false;

        try
        {
            var normalizedLibraryRoot = Path.GetFullPath(libraryRoot);
            var normalizedLibraryRootWithSep = normalizedLibraryRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedLibraryRoot
                : normalizedLibraryRoot + Path.DirectorySeparatorChar;
            var normalizedAbsolute = Path.GetFullPath(absolutePath);

            if (!normalizedAbsolute.StartsWith(normalizedLibraryRootWithSep, StringComparison.Ordinal) &&
                !string.Equals(normalizedAbsolute, normalizedLibraryRoot, StringComparison.Ordinal))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(normalizedLibraryRoot, normalizedAbsolute);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? ConvertPathToLibraryRelativeIfInsideLibraryRoot(string? path, string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
            return trimmed;

        return TryMakeLibraryRelativeIfInsideLibraryRoot(trimmed, libraryRoot, out var relativePath)
            ? relativePath
            : trimmed;
    }
}
