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
/// Publishes verified installer files into an immutable, versioned archive.
/// Staging is the only destination for downloads, resume files and installer work files.
/// </summary>
internal static class GogInstallerArchive
{
    internal static string DefaultRoot => Path.Combine(AppPaths.DataRoot, "GogOfflineInstallers");

    internal static string GetPackageDirectory(DownloadSettings settings, GogInstallerPackage package)
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
        return Path.Combine(normalized, GetGameDirectoryName(package), GetPlatformName(package.Platform), GetRevision(package));
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

        // Never replace another archive. Only the temporary directory created here can be removed.
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
            Directory.Move(temporary, directory);
        }
        finally
        {
            StoreDirectoryDeletionHelper.DeleteDirectoryTreeWithoutFollowingLinks(temporary);
        }
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
