using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Helpers;

internal static class CacheDirectoryMigrationHelper
{
    internal static async Task MigrateAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlySet<string> allowedExtensions,
        string diagnosticCategory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(allowedExtensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCategory);

        sourceDirectory = Path.GetFullPath(sourceDirectory);
        destinationDirectory = Path.GetFullPath(destinationDirectory);
        if (string.Equals(sourceDirectory, destinationDirectory, StringComparison.Ordinal) ||
            !Directory.Exists(sourceDirectory))
        {
            return;
        }

        try
        {
            if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to migrate cache link '{sourceDirectory}'.");
            if (Directory.Exists(destinationDirectory) &&
                (File.GetAttributes(destinationDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"Refusing to migrate into cache link '{destinationDirectory}'.");
            }

            var sourceFiles = GetCacheFileSnapshots(sourceDirectory, allowedExtensions);
            if (sourceFiles.Count == 0)
                return;

            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourceFile in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationPath = Path.Combine(destinationDirectory, sourceFile.RelativePath);
                if (!ShouldReplaceDestination(sourceFile, destinationPath))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await CopyCacheFileAtomicallyAsync(
                        sourceFile,
                        destinationPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // A concurrent refresh may have replaced a source file while it was copied.
            // Retain the complete source cache rather than risk deleting newer data.
            if (sourceFiles.Any(sourceFile => !SourceFileIsUnchanged(sourceFile)))
                return;

            foreach (var sourceFile in sourceFiles)
                File.Delete(sourceFile.Path);

            TryDeleteEmptyParentDirectories(sourceFiles, sourceDirectory);
            TryDeleteEmptyDirectory(sourceDirectory);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache data is reproducible. Keep the source intact on migration failure;
            // successfully copied destination files remain safe to reuse.
            Debug.WriteLine($"[{diagnosticCategory}] Could not migrate cache: {ex.Message}");
        }
    }

    private static List<CacheFileSnapshot> GetCacheFileSnapshots(
        string sourceDirectory,
        IReadOnlySet<string> allowedExtensions)
    {
        var snapshots = new List<CacheFileSnapshot>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceDirectory);

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    pendingDirectories.Push(childDirectory);
            }

            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!allowedExtensions.Contains(Path.GetExtension(path)))
                    continue;

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                var info = new FileInfo(path);
                snapshots.Add(new CacheFileSnapshot(
                    path,
                    Path.GetRelativePath(sourceDirectory, path),
                    info.Length,
                    info.LastWriteTimeUtc));
            }
        }

        return snapshots;
    }

    private static bool ShouldReplaceDestination(
        CacheFileSnapshot sourceFile,
        string destinationPath)
    {
        if (!File.Exists(destinationPath))
            return true;

        var attributes = File.GetAttributes(destinationPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to replace cache link '{destinationPath}'.");

        return sourceFile.LastWriteTimeUtc > File.GetLastWriteTimeUtc(destinationPath);
    }

    private static async Task CopyCacheFileAtomicallyAsync(
        CacheFileSnapshot sourceFile,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(
                             sourceFile.Path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.SetLastWriteTimeUtc(tempPath, sourceFile.LastWriteTimeUtc);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static bool SourceFileIsUnchanged(CacheFileSnapshot snapshot)
    {
        if (!File.Exists(snapshot.Path))
            return false;

        var attributes = File.GetAttributes(snapshot.Path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return false;

        var info = new FileInfo(snapshot.Path);
        return info.Length == snapshot.Length &&
               info.LastWriteTimeUtc == snapshot.LastWriteTimeUtc;
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // best effort; later migrations use unique temporary paths
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory may contain a concurrent refresh or unrelated data.
        }
    }

    private static void TryDeleteEmptyParentDirectories(
        IEnumerable<CacheFileSnapshot> sourceFiles,
        string sourceDirectory)
    {
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceFile in sourceFiles)
        {
            var directory = Path.GetDirectoryName(sourceFile.Path);
            while (directory != null &&
                   !string.Equals(directory, sourceDirectory, StringComparison.Ordinal))
            {
                directories.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        foreach (var directory in directories.OrderByDescending(static path => path.Length))
            TryDeleteEmptyDirectory(directory);
    }

    private sealed record CacheFileSnapshot(
        string Path,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);
}
