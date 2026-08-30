using System;
using System.Diagnostics;
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

    internal static void EnsureDosDeviceMapping(
        string dosDevicesDir,
        string driveName,
        string relativeTarget)
    {
        if (string.IsNullOrWhiteSpace(driveName))
            throw new ArgumentException("Drive name must not be empty.", nameof(driveName));

        if (!driveName.EndsWith(":", StringComparison.Ordinal))
            throw new ArgumentException("Drive name must end with ':' (e.g. 'd:').", nameof(driveName));

        Directory.CreateDirectory(dosDevicesDir);

        var linkPath = Path.Combine(dosDevicesDir, driveName);
        var targetValue = relativeTarget.Replace('\\', '/');

        // Existing mappings belong to the user/prefix and must never be overwritten.
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
            return;

        try
        {
            File.CreateSymbolicLink(linkPath, targetValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[Prefix] Failed to create dosdevices mapping {driveName} -> {relativeTarget}: {ex.Message}");
        }
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
