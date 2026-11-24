using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

/// <summary>
/// Publishes verified installer files into a shared archive folder for a game and its DLCs.
/// Staging is the only destination for downloads, resume files and installer work files.
/// </summary>
internal static class GogInstallerArchive
{
    private const string FlatPackageMarkerPrefix = ".retromind-gog-";
    private const string VersionsDirectoryName = "Versions";

    internal static string DefaultRoot => Path.Combine(AppPaths.DataRoot, "GogOfflineInstallers");

    internal static string GetPackageDirectory(DownloadSettings settings, GogInstallerPackage package,
        string? baseGameId = null, string? gameTitle = null)
    {
        var root = GetArchiveRoot(settings);
        var id = GetGameDirectoryName(package with { GameId = baseGameId ?? package.GameId });
        var suffix = " (" + id + ")";
        // Keep using the same archive after the library title is edited.
        var existing = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal)
                .FirstOrDefault(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal))
            : null;
        var directory = existing ?? Path.Combine(root,
            PrefixPathHelper.SanitizePrefixFolderName(gameTitle ?? package.InstallerName) + suffix);
        EnsureSafeDirectory(directory);
        return directory;
    }

    internal static string GetLegacyPackageDirectory(DownloadSettings settings, GogInstallerPackage package) =>
        Path.Combine(GetArchiveRoot(settings), GetGameDirectoryName(package),
            GetPlatformName(package.Platform), GetRevision(package));

    internal static string GetConflictDirectory(string gameDirectory, GogInstallerPackage package) =>
        Path.Combine(gameDirectory, VersionsDirectoryName,
            $"{PrefixPathHelper.SanitizePrefixFolderName(GogInstallerFingerprint.NormalizeVersion(package.Version))} ({GetPackageKey(package)})");

    internal static bool HasFlatPackageMarker(string directory, GogInstallerPackage package)
    {
        var marker = GetFlatPackageMarker(directory, package);
        EnsureSafeFile(marker);
        return File.Exists(marker);
    }

    private static string GetFlatPackageMarker(string directory, GogInstallerPackage package) =>
        Path.Combine(directory, FlatPackageMarkerPrefix + GetPackageKey(package));

    private static string GetPackageKey(GogInstallerPackage package) =>
        $"{GetGameDirectoryName(package)}-{GetPlatformName(package.Platform)}-{GetRevision(package)}";

    private static string GetArchiveRoot(DownloadSettings settings)
    {
        var root = string.IsNullOrWhiteSpace(settings.GogArchiveDirectory)
            ? DefaultRoot
            : AppPaths.ResolveDataPath(settings.GogArchiveDirectory);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (StoreInstallPathSafety.IsDangerousInstallPath(normalized) ||
            Overlaps(normalized, Path.Combine(AppPaths.DataRoot, "State")) ||
            Overlaps(normalized, AppPaths.LibraryRoot) ||
            normalized.Split(Path.DirectorySeparatorChar).Contains(".retromind-gog-installers", StringComparer.Ordinal))
            throw new IOException("The installer archive requires a separate, safe directory.");

        EnsureSafeDirectory(normalized);
        return normalized;
    }

    internal static void EnsureSeparateFromInstallation(string archiveDirectory, string? installPath)
    {
        if (installPath != null && Overlaps(archiveDirectory, installPath))
            throw new IOException("The installer archive overlaps the game installation.");
    }

    internal static string GetStagingDirectory(GogInstallerPackage package) =>
        Path.Combine(AppPaths.DataRoot, "State", "Downloads", "Gog",
            GetGameDirectoryName(package), GetPlatformName(package.Platform), GetRevision(package));

    internal static void DeleteQueuedStagingBestEffort(GogInstallerPackage package)
    {
        try
        {
            StoreDirectoryDeletionHelper.DeleteDirectoryTreeWithoutFollowingLinks(GetStagingDirectory(package));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The installation is complete. Leaving staging behind is safe.
        }
    }

    internal static void DeleteArchivedQueuedFilesBestEffort(GogInstallerPackage package,
        GogDownloadedInstallerPackage downloaded)
    {
        var staging = GetStagingDirectory(package);
        if (!string.Equals(staging, downloaded.StagingDirectory, StringComparison.Ordinal))
            return;

        try
        {
            EnsureSafeDirectory(staging);
            foreach (var file in downloaded.DownloadedFiles)
            {
                if (!string.Equals(staging, Path.GetDirectoryName(file), StringComparison.Ordinal))
                    continue;
                EnsureSafeFile(file);
                File.Delete(file);
            }
            if (!Directory.EnumerateFileSystemEntries(staging).Any())
                Directory.Delete(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The archive has been published. Any remaining staging files can be left in place.
        }
    }

    internal static string GetRevision(GogInstallerPackage package) =>
        GetRevision(package.Version, GogInstallerFingerprint.BuildSignature(package));

    internal static string GetRevision(string? version, string? signature) =>
        Hash(GogInstallerFingerprint.NormalizeVersion(version) + "\n" + signature);

    internal static string GetPlatformName(GogInstallPlatform platform) =>
        platform == GogInstallPlatform.Windows ? "windows" : "linux";

    internal static string[] GetSafeFileNames(GogInstallerPackage package)
    {
        var names = package.Files.Select(file => GogInstallerStagingPathHelper.SanitizeFileName(file.FileName)).ToArray();
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Intersect(names.Select(name => name + ".part"), StringComparer.Ordinal).Any() ||
            names.Any(name => name is "." or ".." or "retromind-install.log" ||
                              name.StartsWith(FlatPackageMarkerPrefix, StringComparison.OrdinalIgnoreCase) ||
                              name.Equals(VersionsDirectoryName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The installer package has unsafe or conflicting file names.");
        return names;
    }

    internal static async Task<string> PublishAsync(string directory, GogInstallerPackage package,
        GogDownloadedInstallerPackage downloaded, CancellationToken ct)
    {
        var names = GetSafeFileNames(package);
        if (!names.Order(StringComparer.Ordinal).SequenceEqual(
                downloaded.DownloadedFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new IOException("The downloaded files do not match the installer package.");

        EnsureSafeDirectory(directory);
        var conflictDirectory = GetConflictDirectory(directory, package);
        if (Directory.Exists(conflictDirectory))
        {
            EnsureSafeDirectory(conflictDirectory);
            foreach (var source in downloaded.DownloadedFiles)
            {
                if (!await MatchesExistingFileAsync(source, conflictDirectory, ct).ConfigureAwait(false))
                    throw new IOException($"The archived installer package differs from the downloaded package: {conflictDirectory}");
            }
            return conflictDirectory;
        }

        var missing = new List<string>();
        var destination = directory;
        foreach (var source in downloaded.DownloadedFiles)
        {
            var target = Path.Combine(directory, Path.GetFileName(source));
            EnsureSafeFile(target);
            if (!File.Exists(target))
                missing.Add(source);
            else if (!await FilesEqualAsync(source, target, ct).ConfigureAwait(false))
            {
                // Keep the entire package together, including any identically named multipart files.
                destination = conflictDirectory;
                missing.Clear();
                missing.AddRange(downloaded.DownloadedFiles);
                break;
            }
        }

        EnsureSafeDirectory(destination);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, ".incoming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var totalBytes = missing.Aggregate(0L,
                (total, path) => StoreDiskSpaceHelper.SaturatingAdd(total, new FileInfo(path).Length));
            if (totalBytes > 0)
                GogInstallSpaceService.EnsureDownloadCanFit(Path.Combine(destination, "installer"), totalBytes);
            foreach (var path in missing)
            {
                ct.ThrowIfCancellationRequested();
                await CopyFileAsync(path, Path.Combine(temporary, Path.GetFileName(path)), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            EnsureSafeDirectory(destination);
            if (destination == conflictDirectory)
            {
                Directory.Move(temporary, conflictDirectory);
                return conflictDirectory;
            }

            var marker = GetFlatPackageMarker(directory, package);
            EnsureSafeFile(marker);
            Directory.CreateDirectory(directory);
            foreach (var source in Directory.GetFiles(temporary))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(directory, Path.GetFileName(source));
                EnsureSafeDirectory(directory);
                EnsureSafeFile(target);
                File.Move(source, target);
            }

            // A flat archive is reusable only after every file has been published.
            ct.ThrowIfCancellationRequested();
            EnsureSafeFile(marker);
            if (!File.Exists(marker))
            {
                using var registration = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                registration.Flush(flushToDisk: true);
            }
            return directory;
        }
        finally
        {
            StoreDirectoryDeletionHelper.DeleteDirectoryTreeWithoutFollowingLinks(temporary);
        }
    }

    private static async Task<bool> MatchesExistingFileAsync(string source, string directory, CancellationToken ct)
    {
        var target = Path.Combine(directory, Path.GetFileName(source));
        EnsureSafeFile(target);
        return File.Exists(target) && await FilesEqualAsync(source, target, ct).ConfigureAwait(false);
    }

    private static async Task<bool> FilesEqualAsync(string first, string second, CancellationToken ct)
    {
        EnsureSafeFile(first);
        EnsureSafeFile(second);
        if (new FileInfo(first).Length != new FileInfo(second).Length)
            return false;
        await using var input = File.OpenRead(first);
        await using var other = File.OpenRead(second);
        var hash = await SHA256.HashDataAsync(input, ct).ConfigureAwait(false);
        var otherHash = await SHA256.HashDataAsync(other, ct).ConfigureAwait(false);
        return hash.AsSpan().SequenceEqual(otherHash);
    }

    internal static async Task CopyFileAsync(string source, string target, CancellationToken ct)
    {
        EnsureSafeFile(source);
        EnsureSafeFile(target);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".copy";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void EnsureSafeDirectory(string directory)
    {
        if (StoreDirectoryDeletionHelper.ContainsSymbolicLinkInPath(directory) || File.Exists(directory))
            throw new IOException("The installer directory is unsafe.");
    }

    internal static void EnsureSafeFile(string path)
    {
        if (StoreDirectoryDeletionHelper.ContainsSymbolicLinkInPath(path) || Directory.Exists(path))
            throw new IOException("The installer file path is unsafe.");
    }

    private static string GetGameDirectoryName(GogInstallerPackage package) =>
        !string.IsNullOrWhiteSpace(package.GameId) && package.GameId.All(char.IsAsciiDigit)
            ? package.GameId
            : Hash(package.GameId);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool Overlaps(string first, string second) =>
        IsInside(first, second) || IsInside(second, first);

    private static bool IsInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
