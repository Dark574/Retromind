using System;
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
            names.Any(name => name is "." or ".." or "retromind-install.log"))
            throw new IOException("The installer package has unsafe or conflicting file names.");
        return names;
    }

    internal static async Task PublishAsync(string directory, GogDownloadedInstallerPackage package, CancellationToken ct)
    {
        EnsureSafeDirectory(directory);
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);

        // Prepare all files before merging. Existing files are never replaced.
        var temporary = Path.Combine(parent, ".incoming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var totalBytes = package.DownloadedFiles.Aggregate(0L,
                (total, path) => StoreDiskSpaceHelper.SaturatingAdd(total, new FileInfo(path).Length));
            GogInstallSpaceService.EnsureDownloadCanFit(Path.Combine(directory, "installer"), totalBytes);
            foreach (var path in package.DownloadedFiles)
            {
                ct.ThrowIfCancellationRequested();
                await CopyFileAsync(path, Path.Combine(temporary, Path.GetFileName(path)), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            EnsureSafeDirectory(directory);
            Directory.CreateDirectory(directory);
            var files = Directory.GetFiles(temporary);
            foreach (var source in files)
            {
                var target = Path.Combine(directory, Path.GetFileName(source));
                EnsureSafeFile(target);
                if (File.Exists(target) && !await FilesEqualAsync(source, target, ct).ConfigureAwait(false))
                    throw new IOException($"An archived installer with different contents already exists: {target}");
            }
            foreach (var source in files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(directory, Path.GetFileName(source));
                EnsureSafeDirectory(directory);
                EnsureSafeFile(target);
                if (File.Exists(target))
                {
                    if (!await FilesEqualAsync(source, target, ct).ConfigureAwait(false))
                        throw new IOException($"An archived installer with different contents already exists: {target}");
                    continue;
                }
                try
                {
                    File.Move(source, target);
                }
                catch (IOException) when (File.Exists(target))
                {
                    EnsureSafeFile(target);
                    if (!await FilesEqualAsync(source, target, ct).ConfigureAwait(false))
                        throw;
                }
            }
        }
        finally
        {
            StoreDirectoryDeletionHelper.DeleteDirectoryTreeWithoutFollowingLinks(temporary);
        }
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
