using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

internal readonly record struct GogInstallPayloadSnapshot(
    int FileCount,
    long TotalSize,
    DateTimeOffset LatestWriteUtc,
    ulong MetadataFingerprint);

internal static class GogInstallPayloadTracker
{
    public static GogInstallPayloadSnapshot Capture(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return default;

        try
        {
            var fileCount = 0;
            long totalSize = 0;
            var latestWriteUtc = DateTimeOffset.MinValue;
            ulong metadataFingerprint = 0;
            foreach (var file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(installPath, file);
                if (IsInsideInstallerStaging(file) ||
                    relativePath.StartsWith(
                        $".mojosetup{Path.DirectorySeparatorChar}",
                        FileSystemPathIdentity.Comparison))
                {
                    continue;
                }

                var fileInfo = new FileInfo(file);
                fileCount++;
                totalSize += fileInfo.Length;
                var writeUtc = fileInfo.LastWriteTimeUtc;
                if (writeUtc > latestWriteUtc.UtcDateTime)
                    latestWriteUtc = new DateTimeOffset(writeUtc, TimeSpan.Zero);

                metadataFingerprint ^= BuildMetadataFingerprint(relativePath, fileInfo.Length, writeUtc.Ticks);
            }

            return new GogInstallPayloadSnapshot(fileCount, totalSize, latestWriteUtc, metadataFingerprint);
        }
        catch
        {
            return default;
        }
    }

    public static bool HasChanged(string installPath, GogInstallPayloadSnapshot baseline)
    {
        var current = Capture(installPath);
        return current.FileCount != baseline.FileCount ||
               current.TotalSize != baseline.TotalSize ||
               current.LatestWriteUtc != baseline.LatestWriteUtc ||
               current.MetadataFingerprint != baseline.MetadataFingerprint;
    }

    public static async Task<bool> WaitForChangeAsync(
        string installPath,
        GogInstallPayloadSnapshot baseline,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (HasChanged(installPath, baseline))
            return true;

        var timeoutAt = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if (HasChanged(installPath, baseline))
                return true;
        }

        return HasChanged(installPath, baseline);
    }

    private static ulong BuildMetadataFingerprint(string relativePath, long length, long lastWriteTicks)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        unchecked
        {
            var hash = offsetBasis;
            foreach (var ch in relativePath)
            {
                hash ^= (byte)ch;
                hash *= prime;
                hash ^= (byte)(ch >> 8);
                hash *= prime;
            }

            for (var shift = 0; shift < 64; shift += 8)
            {
                hash ^= (byte)(length >> shift);
                hash *= prime;
                hash ^= (byte)(lastWriteTicks >> shift);
                hash *= prime;
            }

            return hash;
        }
    }

    private static bool IsInsideInstallerStaging(string path)
        => path.IndexOf(".retromind-gog-installers", FileSystemPathIdentity.Comparison) >= 0;
}
