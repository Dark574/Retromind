using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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

        if (File.Exists(fullPath))
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.InvalidPath, fullPath);

        if (!Directory.Exists(fullPath))
            return new GogInstallDirectoryAssessment(GogInstallDirectoryStatus.NewDirectory, fullPath);

        try
        {
            if (IsSymbolicLink(fullPath) ||
                (rejectSymbolicLinks && ContainsSymbolicLink(fullPath)))
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

        if (!item.CustomFields.TryGetValue("Store.GameId", out var gameId) ||
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

        if (!item.CustomFields.TryGetValue("Store.GameId", out var gameId) ||
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
