using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Resolves rcheevos hashes against the RetroAchievements game catalog and
/// aggressively caches the large, system-specific API responses.
/// </summary>
public sealed class RetroAchievementsGameCatalogService
{
    private const int CacheSchemaVersion = 1;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly RetroAchievementsApiClient _apiClient;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _consoleGates = new();
    private readonly ConcurrentDictionary<uint, CatalogState> _memoryCache = new();
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _refreshRetryAfter = new();

    public RetroAchievementsGameCatalogService(RetroAchievementsApiClient apiClient)
        : this(
            apiClient,
            GetDefaultCacheDirectory(),
            TimeProvider.System)
    {
    }

    private static string GetDefaultCacheDirectory()
    {
        var portableCacheRoot = PortableEnvironment.GetConfiguredPortableCacheRoot();
        return portableCacheRoot != null
            ? Path.Combine(portableCacheRoot, "retromind", "RetroAchievements")
            : Path.Combine(AppPaths.DataRoot, "Cache", "RetroAchievements");
    }

    internal static string GetCacheDirectory(bool usePortableHome)
    {
        var portableCacheRoot = PortableEnvironment.GetPortableCacheRoot(usePortableHome);
        return portableCacheRoot != null
            ? Path.Combine(portableCacheRoot, "retromind", "RetroAchievements")
            : Path.Combine(AppPaths.DataRoot, "Cache", "RetroAchievements");
    }

    internal static async Task MigrateCacheLocationAsync(
        bool wasPortableHomeEnabled,
        bool isPortableHomeEnabled,
        CancellationToken cancellationToken = default)
    {
        if (wasPortableHomeEnabled == isPortableHomeEnabled)
            return;

        var sourceDirectory = GetCacheDirectory(wasPortableHomeEnabled);
        var destinationDirectory = GetCacheDirectory(isPortableHomeEnabled);
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

            var sourceFiles = GetCacheFileSnapshots(sourceDirectory);
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
            // Keep the complete source cache in that case so no newer data is lost.
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
            // the destination can safely reuse any files that were copied successfully.
            Debug.WriteLine($"[RetroAchievements] Could not migrate game catalog cache: {ex.Message}");
        }
    }

    internal RetroAchievementsGameCatalogService(
        RetroAchievementsApiClient apiClient,
        string cacheDirectory,
        TimeProvider timeProvider)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<RetroAchievementsGameCatalogEntry?> FindByHashAsync(
        uint consoleId,
        string hash,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (consoleId == 0)
            throw new ArgumentOutOfRangeException(nameof(consoleId));
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var normalizedHash = NormalizeHash(hash);
        var gate = _consoleGates.GetOrAdd(consoleId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_memoryCache.TryGetValue(consoleId, out var memoryCache) &&
                (IsFresh(memoryCache.Document, now) ||
                 (_refreshRetryAfter.TryGetValue(consoleId, out var retryAfter) && retryAfter > now)))
            {
                return FindMatch(memoryCache, normalizedHash);
            }

            var diskCache = await TryLoadCacheAsync(consoleId, cancellationToken)
                .ConfigureAwait(false);
            if (diskCache != null && IsFresh(diskCache.Document, now))
            {
                _memoryCache[consoleId] = diskCache;
                return FindMatch(diskCache, normalizedHash);
            }

            try
            {
                var games = await _apiClient
                    .GetGameCatalogAsync(consoleId, apiKey, cancellationToken)
                    .ConfigureAwait(false);
                var refreshedDocument = new CacheDocument
                {
                    SchemaVersion = CacheSchemaVersion,
                    ConsoleId = consoleId,
                    FetchedAtUtc = now,
                    Games = games.ToList()
                };

                var refreshed = CreateState(refreshedDocument, consoleId);
                _memoryCache[consoleId] = refreshed;
                _refreshRetryAfter.TryRemove(consoleId, out _);
                await TrySaveCacheAsync(refreshedDocument, cancellationToken).ConfigureAwait(false);
                return FindMatch(refreshed, normalizedHash);
            }
            catch (RetroAchievementsApiException) when (diskCache != null)
            {
                // The official API recommends aggressive caching. A valid stale
                // catalog is preferable to losing identification while offline.
                _memoryCache[consoleId] = diskCache;
                _refreshRetryAfter[consoleId] = now + RefreshFailureBackoff;
                return FindMatch(diskCache, normalizedHash);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CatalogState?> TryLoadCacheAsync(
        uint consoleId,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(consoleId);
        if (!File.Exists(cachePath))
            return null;

        try
        {
            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var cache = await JsonSerializer
                .DeserializeAsync<CacheDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (cache == null)
                return null;

            return CreateState(cache, consoleId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   JsonException or
                                   InvalidDataException)
        {
            Debug.WriteLine($"[RetroAchievements] Ignoring invalid game catalog cache: {ex.Message}");
            return null;
        }
    }

    private async Task TrySaveCacheAsync(
        CacheDocument cache,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(cache.ConsoleId);
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer
                    .SerializeAsync(stream, cache, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTempFile(tempPath);
            Debug.WriteLine($"[RetroAchievements] Could not persist game catalog cache: {ex.Message}");
        }
    }

    private string GetCachePath(uint consoleId) =>
        Path.Combine(_cacheDirectory, $"games-{consoleId}.json");

    private static bool IsFresh(CacheDocument cache, DateTimeOffset now) =>
        cache.FetchedAtUtc <= now && now - cache.FetchedAtUtc < CacheLifetime;

    private static RetroAchievementsGameCatalogEntry? FindMatch(
        CatalogState state,
        string normalizedHash)
    {
        return state.HashIndex.TryGetValue(normalizedHash, out var match) ? match : null;
    }

    private static CatalogState CreateState(CacheDocument cache, uint expectedConsoleId)
    {
        if (cache.SchemaVersion != CacheSchemaVersion ||
            cache.ConsoleId != expectedConsoleId ||
            cache.FetchedAtUtc == default ||
            cache.Games == null)
        {
            throw new InvalidDataException("The RetroAchievements game catalog cache is invalid.");
        }

        foreach (var game in cache.Games)
        {
            if (game.GameId <= 0 ||
                game.ConsoleId != expectedConsoleId ||
                string.IsNullOrWhiteSpace(game.Title) ||
                game.Hashes == null ||
                game.Hashes.Any(value => !IsValidHash(value)))
            {
                throw new InvalidDataException("The RetroAchievements game catalog cache contains invalid data.");
            }
        }

        var hashIndex = new Dictionary<string, RetroAchievementsGameCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in cache.Games)
        {
            foreach (var hash in game.Hashes)
            {
                if (hashIndex.TryGetValue(hash, out var existing) && existing.GameId != game.GameId)
                {
                    throw new InvalidDataException(
                        $"RetroAchievements catalog contains hash '{hash}' for multiple games.");
                }

                hashIndex[hash] = game;
            }
        }

        return new CatalogState(cache, hashIndex);
    }

    private static string NormalizeHash(string hash)
    {
        var normalized = hash.Trim();
        if (!IsValidHash(normalized))
            throw new ArgumentException("A RetroAchievements hash must contain 32 hexadecimal characters.", nameof(hash));

        return normalized.ToLowerInvariant();
    }

    private static bool IsValidHash(string? hash)
    {
        if (hash is not { Length: 32 })
            return false;

        foreach (var character in hash)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
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
            // best effort; a later cache refresh overwrites the same temp file
        }
    }

    private static List<CacheFileSnapshot> GetCacheFileSnapshots(string sourceDirectory)
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
                var extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory may contain a concurrent refresh or an unrelated file.
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

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public uint ConsoleId { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
        public List<RetroAchievementsGameCatalogEntry> Games { get; set; } = new();
    }

    private sealed record CatalogState(
        CacheDocument Document,
        IReadOnlyDictionary<string, RetroAchievementsGameCatalogEntry> HashIndex);

    private sealed record CacheFileSnapshot(
        string Path,
        string RelativePath,
        long Length,
        DateTime LastWriteTimeUtc);
}
