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
    private static readonly IReadOnlySet<string> CacheExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json", ".png" };
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly RetroAchievementsApiClient _apiClient;
    private readonly Func<string> _cacheDirectoryProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<uint, SemaphoreSlim> _consoleGates = new();
    private readonly ConcurrentDictionary<uint, CatalogState> _memoryCache = new();
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _refreshRetryAfter = new();

    public RetroAchievementsGameCatalogService(
        RetroAchievementsApiClient apiClient,
        RetroAchievementsCachePathProvider cachePathProvider)
        : this(
            apiClient,
            cachePathProvider == null
                ? throw new ArgumentNullException(nameof(cachePathProvider))
                : cachePathProvider.GetCacheDirectory,
            TimeProvider.System)
    {
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

        await CacheDirectoryMigrationHelper.MigrateAsync(
                GetCacheDirectory(wasPortableHomeEnabled),
                GetCacheDirectory(isPortableHomeEnabled),
                CacheExtensions,
                "RetroAchievements",
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal RetroAchievementsGameCatalogService(
        RetroAchievementsApiClient apiClient,
        string cacheDirectory,
        TimeProvider timeProvider)
        : this(
            apiClient,
            () => Path.GetFullPath(cacheDirectory),
            timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
    }

    private RetroAchievementsGameCatalogService(
        RetroAchievementsApiClient apiClient,
        Func<string> cacheDirectoryProvider,
        TimeProvider timeProvider)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _cacheDirectoryProvider = cacheDirectoryProvider ??
                                  throw new ArgumentNullException(nameof(cacheDirectoryProvider));
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
        var cacheDirectory = _cacheDirectoryProvider();
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

            var diskCache = await TryLoadCacheAsync(consoleId, cacheDirectory, cancellationToken)
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
                await TrySaveCacheAsync(refreshedDocument, cacheDirectory, cancellationToken)
                    .ConfigureAwait(false);
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
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(cacheDirectory, consoleId);
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
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(cacheDirectory, cache.ConsoleId);
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(cacheDirectory);
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

    private static string GetCachePath(string cacheDirectory, uint consoleId) =>
        Path.Combine(cacheDirectory, $"games-{consoleId}.json");

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

}
