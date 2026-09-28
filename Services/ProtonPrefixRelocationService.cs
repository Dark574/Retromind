using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

internal sealed record ProtonPrefixRepairResult(
    bool IsProtonManagedPrefix,
    string? PrefixPath,
    string? ProtonRootPath,
    int RepairedLinks,
    int UnresolvedLinks,
    int FailedLinks);

/// <summary>
/// Repairs Proton-managed prefix links after a portable Retromind data root was moved.
/// Proton stores absolute links to files in its runtime, so moving both the runtime and
/// prefix together does not update those link targets automatically.
/// </summary>
public sealed class ProtonPrefixRelocationService
{
    private readonly string _libraryRootPath;
    private readonly AppSettings _settings;

    public ProtonPrefixRelocationService(string libraryRootPath, AppSettings settings)
    {
        _libraryRootPath = Path.GetFullPath(
            libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath)));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    internal ProtonPrefixRepairResult Repair(
        MediaItem item,
        string? effectiveProtonPath = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var prefixPath = ResolveExistingWinePrefixPath(item.PrefixPath);
        if (prefixPath == null || !IsProtonManagedPrefix(prefixPath))
        {
            return new ProtonPrefixRepairResult(
                false,
                prefixPath,
                null,
                0,
                0,
                0);
        }

        var protonRoot = ResolveProtonRoot(item, effectiveProtonPath);
        if (protonRoot == null)
        {
            return new ProtonPrefixRepairResult(
                true,
                prefixPath,
                null,
                0,
                CountBrokenProtonLinks(prefixPath),
                0);
        }

        var protonFilesRoot = Path.Combine(protonRoot, "files");
        var repaired = 0;
        var unresolved = 0;
        var failed = 0;

        foreach (var linkPath in EnumerateSymbolicLinks(prefixPath))
        {
            string? oldTarget;
            try
            {
                oldTarget = new FileInfo(linkPath).LinkTarget;
            }
            catch
            {
                continue;
            }

            string? replacementTarget;
            try
            {
                if (!IsBrokenAbsoluteProtonLink(linkPath, oldTarget, out var relativeRuntimePath))
                    continue;

                replacementTarget = ResolveReplacementTarget(protonFilesRoot, relativeRuntimePath);
            }
            catch
            {
                unresolved++;
                continue;
            }

            if (replacementTarget == null)
            {
                unresolved++;
                continue;
            }

            try
            {
                ReplaceSymbolicLink(linkPath, oldTarget!, replacementTarget);
                repaired++;
            }
            catch (Exception ex)
            {
                failed++;
                Debug.WriteLine($"[ProtonPrefix] Could not repair '{linkPath}': {ex.Message}");
            }
        }

        return new ProtonPrefixRepairResult(
            true,
            prefixPath,
            protonRoot,
            repaired,
            unresolved,
            failed);
    }

    internal string? ResolveExistingWinePrefixPath(string? storedPrefixPath)
    {
        if (string.IsNullOrWhiteSpace(storedPrefixPath))
            return null;

        string prefixRoot;
        try
        {
            prefixRoot = PrefixPathHelper.ResolveAbsolutePrefixPath(storedPrefixPath, _libraryRootPath);
        }
        catch
        {
            return null;
        }

        if (PrefixPathHelper.IsWinePrefixInitialized(prefixRoot))
            return prefixRoot;

        var nestedPfx = Path.Combine(prefixRoot, "pfx");
        return PrefixPathHelper.IsWinePrefixInitialized(nestedPfx) ? nestedPfx : prefixRoot;
    }

    private string? ResolveProtonRoot(MediaItem item, string? effectiveProtonPath)
    {
        var explicitRoot = ResolveProtonRootPath(effectiveProtonPath);
        if (explicitRoot != null)
            return explicitRoot;

        var runnerId = item.RunnerVersionId;
        if (string.IsNullOrWhiteSpace(runnerId) &&
            item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallRunnerVersionId, out var installedRunnerId))
        {
            runnerId = installedRunnerId;
        }

        var runner = RunnerVersionEnvironmentHelper.FindRunnerVersionById(_settings, runnerId);
        return runner?.Kind == RunnerVersionKind.Proton
            ? ResolveProtonRootPath(runner.Path)
            : null;
    }

    private static string? ResolveProtonRootPath(string? configuredPath)
    {
        var resolvedPath = RunnerVersionPathHelper.ResolveConfiguredPath(configuredPath);
        if (resolvedPath == null)
            return null;

        if (File.Exists(resolvedPath) &&
            string.Equals(Path.GetFileName(resolvedPath), "proton", StringComparison.OrdinalIgnoreCase))
        {
            resolvedPath = Path.GetDirectoryName(resolvedPath);
        }

        if (string.IsNullOrWhiteSpace(resolvedPath) ||
            !Directory.Exists(Path.Combine(resolvedPath, "files")))
        {
            return null;
        }

        return Path.GetFullPath(resolvedPath);
    }

    private static bool IsProtonManagedPrefix(string prefixPath) =>
        File.Exists(Path.Combine(prefixPath, "tracked_files")) &&
        File.Exists(Path.Combine(prefixPath, "version"));

    private static int CountBrokenProtonLinks(string prefixPath)
    {
        var count = 0;
        foreach (var linkPath in EnumerateSymbolicLinks(prefixPath))
        {
            try
            {
                if (IsBrokenAbsoluteProtonLink(
                        linkPath,
                        new FileInfo(linkPath).LinkTarget,
                        out _))
                {
                    count++;
                }
            }
            catch
            {
                // Unreadable links are ignored and remain untouched.
            }
        }

        return count;
    }

    private static IEnumerable<string> EnumerateSymbolicLinks(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            yield break;

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch
            {
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    yield return entry;
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                    pendingDirectories.Push(entry);
            }
        }
    }

    private static bool IsBrokenAbsoluteProtonLink(
        string linkPath,
        string? target,
        out string relativeRuntimePath)
    {
        relativeRuntimePath = string.Empty;
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target))
            return false;

        var resolvedTarget = Path.GetFullPath(target);
        if (File.Exists(resolvedTarget) || Directory.Exists(resolvedTarget))
            return false;

        var separatorNormalized = resolvedTarget.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var marker = $"{Path.DirectorySeparatorChar}files{Path.DirectorySeparatorChar}";
        var markerIndex = separatorNormalized.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        relativeRuntimePath = separatorNormalized[(markerIndex + marker.Length)..];
        return !string.IsNullOrWhiteSpace(relativeRuntimePath) &&
               IsInsideProtonManagedPrefixArea(linkPath);
    }

    private static bool IsInsideProtonManagedPrefixArea(string linkPath)
    {
        var normalized = linkPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var driveCMarker = $"{Path.DirectorySeparatorChar}drive_c{Path.DirectorySeparatorChar}";
        return normalized.Contains(driveCMarker, StringComparison.Ordinal);
    }

    private static string? ResolveReplacementTarget(string protonFilesRoot, string relativeRuntimePath)
    {
        var normalizedFilesRoot = Path.GetFullPath(protonFilesRoot);
        var candidate = Path.GetFullPath(Path.Combine(normalizedFilesRoot, relativeRuntimePath));
        var rootWithSeparator = normalizedFilesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedFilesRoot
            : normalizedFilesRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            return null;

        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : null;
    }

    private static void ReplaceSymbolicLink(string linkPath, string oldTarget, string replacementTarget)
    {
        var temporaryLink = linkPath + $".retromind-{Guid.NewGuid():N}.tmp";
        try
        {
            File.CreateSymbolicLink(temporaryLink, replacementTarget);
            File.Move(temporaryLink, linkPath, overwrite: true);
        }
        catch
        {
            TryDeleteLink(temporaryLink);

            // File.Move may not replace a directory-style symlink on every runtime.
            // Fall back to delete/create while restoring the old target on failure.
            TryDeleteLink(linkPath);
            try
            {
                File.CreateSymbolicLink(linkPath, replacementTarget);
            }
            catch
            {
                if (!SymbolicLinkExists(linkPath))
                    File.CreateSymbolicLink(linkPath, oldTarget);
                throw;
            }
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            try
            {
                Directory.Delete(path);
            }
            catch
            {
                // The caller handles a remaining link as a failed repair.
            }
        }
    }

    private static bool SymbolicLinkExists(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget != null;
        }
        catch
        {
            return false;
        }
    }
}
