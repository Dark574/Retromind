using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Retromind.Helpers;

public static class PrefixPathHelper
{
    private const int MaxPrefixFolderNameUtf8Bytes = 80;

    private static readonly char[] InvalidPortableFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] PortableGamesDriveCandidates =
        ["r:", "q:", "p:", "o:", "n:", "m:", "l:", "k:", "j:", "i:", "h:", "g:", "f:", "e:", "d:"];

    private static readonly HashSet<string> ReservedPortableFileNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Resolves a stored prefix path against the library root and returns one
    /// canonical absolute path without redundant trailing separators.
    /// </summary>
    public static string ResolveAbsolutePrefixPath(string prefixPath, string libraryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);

        var trimmedPath = prefixPath.Trim();
        var absolutePath = Path.IsPathRooted(trimmedPath)
            ? trimmedPath
            : Path.Combine(libraryRoot, trimmedPath);

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(absolutePath));
    }

    public static string SanitizePrefixFolderName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var normalized = input.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (character == ' ' || char.IsControl(character) ||
                Array.IndexOf(InvalidPortableFileNameCharacters, character) >= 0)
            {
                builder.Append('_');
                continue;
            }

            builder.Append(character);
        }

        var safe = builder.ToString();

        while (safe.Contains("__", StringComparison.Ordinal))
            safe = safe.Replace("__", "_", StringComparison.Ordinal);

        safe = safe.TrimEnd(' ', '.', '_');
        if (safe is "." or ".." || string.IsNullOrWhiteSpace(safe))
            return "Unknown";

        var nameStem = safe.Split('.', 2)[0];
        if (ReservedPortableFileNames.Contains(nameStem))
            safe = $"_{safe}";

        safe = TruncateToUtf8ByteLength(safe, MaxPrefixFolderNameUtf8Bytes).TrimEnd(' ', '.', '_');

        return string.IsNullOrWhiteSpace(safe) ? "Unknown" : safe;
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
        string relativeTarget,
        Action<Exception>? reportFailure = null)
    {
        if (string.IsNullOrWhiteSpace(driveName))
            throw new ArgumentException("Drive name must not be empty.", nameof(driveName));

        if (!driveName.EndsWith(":", StringComparison.Ordinal))
            throw new ArgumentException("Drive name must end with ':' (e.g. 'd:').", nameof(driveName));

        Directory.CreateDirectory(dosDevicesDir);

        var linkPath = Path.Combine(dosDevicesDir, driveName);
        var targetValue = relativeTarget.Replace('\\', '/');

        // Existing mappings belong to the user/prefix and must never be overwritten.
        if (FileSystemEntryExists(linkPath))
            return;

        try
        {
            File.CreateSymbolicLink(linkPath, targetValue);
        }
        catch (Exception ex)
        {
            if (reportFailure == null)
                Debug.WriteLine($"[Prefix] Failed to create dosdevices mapping {driveName} -> {relativeTarget}: {ex.Message}");
            else
                TryReportMappingFailure(reportFailure, ex);
        }
    }

    /// <summary>
    /// Exposes Library/Games through a collision-free Wine drive for prefixes managed by Retromind.
    /// Existing mappings are preserved. A Retromind games mapping is removed only when Wine's raw
    /// device mapping for the same drive would shadow it.
    /// </summary>
    internal static string? EnsurePortableGamesDriveMapping(
        string prefixRoot,
        string dosDevicesDirectory,
        string libraryRoot,
        Action<Exception>? reportFailure = null)
    {
        if (string.IsNullOrWhiteSpace(prefixRoot) ||
            string.IsNullOrWhiteSpace(dosDevicesDirectory) ||
            string.IsNullOrWhiteSpace(libraryRoot))
        {
            return null;
        }

        try
        {
            var normalizedLibraryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
            var normalizedPrefixRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(prefixRoot));
            var libraryRootWithSeparator = normalizedLibraryRoot + Path.DirectorySeparatorChar;
            var isManagedPrefix = FileSystemPathIdentity.Equals(normalizedPrefixRoot, normalizedLibraryRoot) ||
                                  normalizedPrefixRoot.StartsWith(
                                      libraryRootWithSeparator,
                                      FileSystemPathIdentity.Comparison);
            if (!isManagedPrefix)
                return null;

            var gamesRoot = Path.Combine(normalizedLibraryRoot, "Games");
            Directory.CreateDirectory(gamesRoot);
            Directory.CreateDirectory(dosDevicesDirectory);

            foreach (var driveName in PortableGamesDriveCandidates)
            {
                var mappingPath = Path.Combine(dosDevicesDirectory, driveName);
                var rawDevicePath = Path.Combine(dosDevicesDirectory, $"{driveName}:");
                if (FileSystemEntryExists(rawDevicePath) && MappingTargets(mappingPath, gamesRoot))
                    DeleteSymbolicLink(mappingPath);
            }

            foreach (var driveName in PortableGamesDriveCandidates)
            {
                var mappingPath = Path.Combine(dosDevicesDirectory, driveName);
                var rawDevicePath = Path.Combine(dosDevicesDirectory, $"{driveName}:");
                if (!FileSystemEntryExists(rawDevicePath) && MappingTargets(mappingPath, gamesRoot))
                    return driveName[..1].ToUpperInvariant();
            }

            var relativeTarget = Path.GetRelativePath(dosDevicesDirectory, gamesRoot);
            foreach (var driveName in PortableGamesDriveCandidates)
            {
                var mappingPath = Path.Combine(dosDevicesDirectory, driveName);
                var rawDevicePath = Path.Combine(dosDevicesDirectory, $"{driveName}:");
                if (FileSystemEntryExists(mappingPath) || FileSystemEntryExists(rawDevicePath))
                    continue;

                EnsureDosDeviceMapping(dosDevicesDirectory, driveName, relativeTarget, reportFailure);
                if (MappingTargets(mappingPath, gamesRoot))
                    return driveName[..1].ToUpperInvariant();
            }
        }
        catch (Exception ex)
        {
            if (reportFailure == null)
                Debug.WriteLine($"[Prefix] Failed to create portable games drive mapping: {ex.Message}");
            else
                TryReportMappingFailure(reportFailure, ex);
        }

        return null;
    }

    private static void TryReportMappingFailure(Action<Exception> reportFailure, Exception exception)
    {
        try { reportFailure(exception); }
        catch { /* Optional diagnostics must not make a best-effort mapping mandatory. */ }
    }

    private static bool MappingTargets(string mappingPath, string expectedTarget)
    {
        var linkTarget = GetSymbolicLinkTarget(mappingPath);
        if (string.IsNullOrWhiteSpace(linkTarget))
            return false;

        try
        {
            var resolvedTarget = Path.IsPathRooted(linkTarget)
                ? Path.GetFullPath(linkTarget)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mappingPath)!, linkTarget));
            return FileSystemPathIdentity.Equals(
                Path.TrimEndingDirectorySeparator(resolvedTarget),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedTarget)));
        }
        catch
        {
            return false;
        }
    }

    private static string? GetSymbolicLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
        }
        catch
        {
            return null;
        }
    }

    private static bool FileSystemEntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || GetSymbolicLinkTarget(path) != null;

    private static void DeleteSymbolicLink(string path)
    {
        if (GetSymbolicLinkTarget(path) == null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path);
        }
    }

    private static string TruncateToUtf8ByteLength(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var builder = new StringBuilder(value.Length);
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maxBytes)
                break;

            builder.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }

        return builder.ToString();
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
