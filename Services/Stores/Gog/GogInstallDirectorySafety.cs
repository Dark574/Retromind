using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

internal enum GogInstallDirectoryStatus
{
    NewDirectory,
    EmptyDirectory,
    OwnedDirectory,
    InvalidPath,
    DangerousPath,
    SymbolicLink,
    UnreadableDirectory,
    UnownedDirectory
}

internal readonly record struct GogInstallDirectoryAssessment(
    GogInstallDirectoryStatus Status,
    string FullPath)
{
    public bool IsAllowed => Status is
        GogInstallDirectoryStatus.NewDirectory or
        GogInstallDirectoryStatus.EmptyDirectory or
        GogInstallDirectoryStatus.OwnedDirectory;
}

/// <summary>
/// Owns the safety contract for GOG install directories. A non-empty directory
/// is considered Retromind-managed only when its marker matches the current item.
/// </summary>
internal static class GogInstallDirectorySafety
{
    public const string MarkerFileName = ".retromind-install.json";

    public static GogInstallDirectoryAssessment Assess(
        string? installPath,
        MediaItem item,
        bool rejectSymbolicLinks)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.InvalidPath, string.Empty);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(installPath);
        }
        catch
        {
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.InvalidPath, installPath);
        }

        if (IsDangerousPath(fullPath))
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.DangerousPath, fullPath);

        try
        {
            // Path.GetFullPath is only a lexical normalization. Check every existing
            // component as well so an ancestor link cannot redirect the operation.
            if (ContainsSymbolicLinkInPath(fullPath))
                return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.SymbolicLink, fullPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Could not inspect install path '{fullPath}': {ex.Message}");
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.UnreadableDirectory, fullPath);
        }

        if (File.Exists(fullPath))
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.InvalidPath, fullPath);

        if (!Directory.Exists(fullPath))
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.NewDirectory, fullPath);

        try
        {
            if (rejectSymbolicLinks && ContainsSymbolicLink(fullPath))
            {
                return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.SymbolicLink, fullPath);
            }

            using var entries = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
            if (!entries.MoveNext())
                return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.EmptyDirectory, fullPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Could not inspect install directory '{fullPath}': {ex.Message}");
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.UnreadableDirectory, fullPath);
        }

        return HasValidMarker(fullPath, item)
            ? new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.OwnedDirectory, fullPath)
            : new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.UnownedDirectory, fullPath);
    }

    public static bool HasValidMarker(string fullPath, MediaItem item)
    {
        var markerPath = Path.Combine(fullPath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            Debug.WriteLine($"[Warning] No install marker found at '{markerPath}'. Refusing to delete path '{fullPath}'.");
            return false;
        }

        InstallMarker? marker;
        try
        {
            var json = File.ReadAllText(markerPath);
            marker = JsonSerializer.Deserialize<InstallMarker>(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Warning] Failed to read install marker at '{markerPath}': {ex.Message}");
            return false;
        }

        if (marker == null ||
            !string.Equals(marker.ProviderId, "gog", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreGameId, out var gameId) ||
            !string.Equals(marker.StoreGameId, gameId, StringComparison.Ordinal))
        {
            Debug.WriteLine($"[Warning] Install marker StoreGameId mismatch: expected '{gameId}', got '{marker.StoreGameId}'.");
            return false;
        }

        if (!string.Equals(marker.MediaItemId, item.Id, StringComparison.Ordinal))
        {
            Debug.WriteLine($"[Warning] Install marker MediaItemId mismatch: expected '{item.Id}', got '{marker.MediaItemId}'.");
            return false;
        }

        return true;
    }

    public static void WriteMarker(string installPath, MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            throw new ArgumentException("Install path is required.", nameof(installPath));

        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreGameId, out var gameId) ||
            string.IsNullOrWhiteSpace(gameId))
        {
            throw new InvalidOperationException("The media item has no GOG game ID.");
        }

        var fullPath = Path.GetFullPath(installPath);
        Directory.CreateDirectory(fullPath);

        var marker = new InstallMarker(
            ProviderId: "gog",
            StoreGameId: gameId,
            MediaItemId: item.Id);
        var markerPath = Path.Combine(fullPath, MarkerFileName);
        var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(markerPath, json, Encoding.UTF8);
    }

    public static bool IsDangerousPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return true;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || PathsEqual(fullPath, root))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && PathsEqual(fullPath, Path.GetFullPath(home)))
            return true;

        if (PathsEqual(fullPath, AppPaths.DataRoot) || PathsEqual(fullPath, AppPaths.LibraryRoot))
            return true;

        var blockedPaths = new[] { "/usr", "/bin", "/sbin", "/etc", "/var", "/boot", "/dev", "/proc", "/sys" };
        foreach (var blocked in blockedPaths)
        {
            if (IsPathEqualToOrBelow(fullPath, blocked))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks the target and every existing ancestor component for symbolic links.
    /// This closes the gap left by lexical path normalization, which does not resolve links.
    /// </summary>
    public static bool ContainsSymbolicLinkInPath(string path)
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

    /// <summary>
    /// Deletes a directory tree without following symbolic links contained in it.
    /// The root and its ancestor path must not contain symbolic links.
    /// </summary>
    public static void DeleteDirectoryTreeWithoutFollowingLinks(
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

    private static bool ContainsSymbolicLink(string rootPath)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsSymbolicLink(entry))
                    return true;

                if (Directory.Exists(entry))
                    pending.Push(entry);
            }
        }

        return false;
    }

    private static bool IsSymbolicLink(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsPathEqualToOrBelow(string path, string potentialParent)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(potentialParent);
        if (string.Equals(fullPath, fullParent, comparison))
            return true;

        var parentWithSeparator = fullParent.EndsWith(Path.DirectorySeparatorChar)
            ? fullParent
            : fullParent + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(parentWithSeparator, comparison);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private sealed record InstallMarker(
        string ProviderId,
        string StoreGameId,
        string MediaItemId);
}
