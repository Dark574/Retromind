using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Loads user-specific game progress and keeps the last successful response in
/// a short-lived memory cache and a user-separated persistent cache.
/// </summary>
public sealed class RetroAchievementsProgressService : IRetroAchievementsProgressService
{
    private const int CacheSchemaVersion = 1;
    private const int DefaultMaximumMemoryCacheEntries = 128;
    private const int SynchronizationGateCount = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly AppSettings _settings;
    private readonly RetroAchievementsAccountService _accountService;
    private readonly RetroAchievementsApiClient _apiClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _cacheDirectoryProvider;
    private readonly int _maximumMemoryCacheEntries;
    private readonly ConcurrentDictionary<ProgressCacheKey, CacheEntry> _cache = new();
    private readonly object _cacheTrimLock = new();
    private readonly SemaphoreSlim[] _gates = CreateSynchronizationGates();
    private readonly ConcurrentDictionary<ProgressCacheKey, byte> _invalidated = new();
    private long _cacheAccessOrder;

    public RetroAchievementsProgressService(
        AppSettings settings,
        RetroAchievementsAccountService accountService,
        RetroAchievementsApiClient apiClient,
        RetroAchievementsCachePathProvider cachePathProvider)
        : this(
            settings,
            accountService,
            apiClient,
            TimeProvider.System,
            cachePathProvider == null
                ? throw new ArgumentNullException(nameof(cachePathProvider))
                : cachePathProvider.GetProgressCacheDirectory)
    {
    }

    internal RetroAchievementsProgressService(
        AppSettings settings,
        RetroAchievementsAccountService accountService,
        RetroAchievementsApiClient apiClient,
        TimeProvider timeProvider,
        string cacheDirectory,
        int maximumMemoryCacheEntries = DefaultMaximumMemoryCacheEntries)
        : this(
            settings,
            accountService,
            apiClient,
            timeProvider,
            () => Path.GetFullPath(cacheDirectory),
            maximumMemoryCacheEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
    }

    private RetroAchievementsProgressService(
        AppSettings settings,
        RetroAchievementsAccountService accountService,
        RetroAchievementsApiClient apiClient,
        TimeProvider timeProvider,
        Func<string> cacheDirectoryProvider,
        int maximumMemoryCacheEntries = DefaultMaximumMemoryCacheEntries)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _cacheDirectoryProvider = cacheDirectoryProvider ??
                                  throw new ArgumentNullException(nameof(cacheDirectoryProvider));
        if (maximumMemoryCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumMemoryCacheEntries));

        _maximumMemoryCacheEntries = maximumMemoryCacheEntries;
    }

    public async Task<RetroAchievementsProgressSnapshot> GetProgressAsync(
        int gameId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
            throw new ArgumentOutOfRangeException(nameof(gameId));

        var retroAchievements = _settings.RetroAchievements;
        if (retroAchievements?.Enabled != true)
            throw new InvalidOperationException("RetroAchievements is not enabled.");

        var userIdentifier = FirstNonEmpty(
            retroAchievements.UserUlid,
            retroAchievements.Username);
        if (userIdentifier == null)
        {
            throw new InvalidOperationException(
                "No RetroAchievements account is configured.");
        }

        var cacheKey = new ProgressCacheKey(
            HashUserIdentifier(userIdentifier),
            gameId);
        var cacheDirectory = _cacheDirectoryProvider();
        forceRefresh |= _invalidated.ContainsKey(cacheKey);
        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh && TryGetFresh(cacheKey, now, out var cached))
            return CreateSnapshot(cached!, usedCachedFallback: false);

        var gate = GetSynchronizationGate(cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            forceRefresh |= _invalidated.ContainsKey(cacheKey);
            now = _timeProvider.GetUtcNow();
            if (!forceRefresh && TryGetFresh(cacheKey, now, out cached))
                return CreateSnapshot(cached!, usedCachedFallback: false);

            TryGetMemoryCache(cacheKey, out var fallback);
            if (fallback == null)
            {
                fallback = await TryLoadCacheAsync(cacheKey, cacheDirectory, cancellationToken)
                    .ConfigureAwait(false);
                if (fallback != null)
                    StoreMemoryCache(cacheKey, fallback);
            }

            if (!forceRefresh && fallback != null && IsFresh(fallback, now))
                return CreateSnapshot(fallback, usedCachedFallback: false);

            try
            {
                var apiKey = await _accountService
                    .GetApiKeyAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(
                        "No RetroAchievements API key is configured.");
                }

                var progress = await _apiClient
                    .GetGameInfoAndUserProgressAsync(
                        gameId,
                        userIdentifier,
                        apiKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                var refreshed = new CacheEntry(progress, _timeProvider.GetUtcNow());
                StoreMemoryCache(cacheKey, refreshed);
                _invalidated.TryRemove(cacheKey, out _);
                await TrySaveCacheAsync(cacheKey, refreshed, cacheDirectory, cancellationToken)
                    .ConfigureAwait(false);
                return CreateSnapshot(refreshed, usedCachedFallback: false);
            }
            catch (RetroAchievementsApiException) when (fallback != null)
            {
                return CreateSnapshot(fallback, usedCachedFallback: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(int gameId)
    {
        if (gameId <= 0)
            return;

        var retroAchievements = _settings.RetroAchievements;
        var userIdentifier = FirstNonEmpty(
            retroAchievements?.UserUlid,
            retroAchievements?.Username);
        if (userIdentifier == null)
            return;

        var key = new ProgressCacheKey(HashUserIdentifier(userIdentifier), gameId);
        _cache.TryRemove(key, out _);
        _invalidated[key] = 0;
    }

    public void ClearMemoryCache()
    {
        _cache.Clear();
        _invalidated.Clear();
    }

    private async Task<CacheEntry?> TryLoadCacheAsync(
        ProgressCacheKey key,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(cacheDirectory, key);
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
            var document = await JsonSerializer
                .DeserializeAsync<CacheDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document == null ||
                document.SchemaVersion != CacheSchemaVersion ||
                document.GameId != key.GameId ||
                document.FetchedAtUtc == default ||
                document.Progress == null ||
                document.Progress.GameId != key.GameId ||
                document.Progress.Achievements == null)
            {
                throw new InvalidDataException("The RetroAchievements progress cache is invalid.");
            }

            return new CacheEntry(document.Progress, document.FetchedAtUtc);
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
            Debug.WriteLine($"[RetroAchievements] Ignoring invalid progress cache: {ex.Message}");
            return null;
        }
    }

    private async Task TrySaveCacheAsync(
        ProgressCacheKey key,
        CacheEntry entry,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(cacheDirectory, key);
        var targetDirectory = Path.GetDirectoryName(cachePath)!;
        var tempPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var document = new CacheDocument
        {
            SchemaVersion = CacheSchemaVersion,
            GameId = key.GameId,
            FetchedAtUtc = entry.FetchedAtUtc,
            Progress = entry.Progress
        };

        try
        {
            Directory.CreateDirectory(targetDirectory);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer
                    .SerializeAsync(stream, document, JsonOptions, cancellationToken)
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
            Debug.WriteLine($"[RetroAchievements] Could not persist progress cache: {ex.Message}");
        }
    }

    private static string GetCachePath(string cacheDirectory, ProgressCacheKey key) =>
        Path.Combine(
            cacheDirectory,
            key.UserIdentifierHash,
            $"game-{key.GameId}.json");

    private bool TryGetFresh(
        ProgressCacheKey key,
        DateTimeOffset now,
        out CacheEntry? entry)
    {
        if (TryGetMemoryCache(key, out entry) && entry != null && IsFresh(entry, now))
            return true;

        entry = null;
        return false;
    }

    internal int MemoryCacheEntryCount => _cache.Count;

    private bool TryGetMemoryCache(ProgressCacheKey key, out CacheEntry? entry)
    {
        if (!_cache.TryGetValue(key, out entry))
            return false;

        entry.Touch(Interlocked.Increment(ref _cacheAccessOrder));
        return true;
    }

    private void StoreMemoryCache(ProgressCacheKey key, CacheEntry entry)
    {
        entry.Touch(Interlocked.Increment(ref _cacheAccessOrder));
        lock (_cacheTrimLock)
        {
            _cache[key] = entry;
            TrimMemoryCache(key);
        }
    }

    private void TrimMemoryCache(ProgressCacheKey retainedKey)
    {
        var excessCount = _cache.Count - _maximumMemoryCacheEntries;
        if (excessCount <= 0)
            return;

        var oldestEntries = _cache
            .Where(pair => pair.Key != retainedKey)
            .OrderBy(pair => pair.Value.LastAccessOrder)
            .Take(excessCount)
            .ToArray();
        foreach (var entry in oldestEntries)
            _cache.TryRemove(entry);
    }

    private SemaphoreSlim GetSynchronizationGate(ProgressCacheKey key) =>
        _gates[(int)((uint)key.GetHashCode() % (uint)_gates.Length)];

    private static SemaphoreSlim[] CreateSynchronizationGates()
    {
        var gates = new SemaphoreSlim[SynchronizationGateCount];
        for (var index = 0; index < gates.Length; index++)
            gates[index] = new SemaphoreSlim(1, 1);

        return gates;
    }

    private static bool IsFresh(CacheEntry entry, DateTimeOffset now) =>
        entry.FetchedAtUtc <= now && now - entry.FetchedAtUtc < CacheLifetime;

    private static RetroAchievementsProgressSnapshot CreateSnapshot(
        CacheEntry entry,
        bool usedCachedFallback)
    {
        return new RetroAchievementsProgressSnapshot(
            entry.Progress,
            entry.FetchedAtUtc,
            usedCachedFallback);
    }

    private static string HashUserIdentifier(string userIdentifier)
    {
        var normalized = userIdentifier.Trim().ToUpperInvariant();
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
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
            // best effort; a future refresh uses a different temporary path
        }
    }

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public int GameId { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
        public RetroAchievementsGameProgress? Progress { get; set; }
    }

    private sealed record ProgressCacheKey(string UserIdentifierHash, int GameId);

    private sealed class CacheEntry(
        RetroAchievementsGameProgress progress,
        DateTimeOffset fetchedAtUtc)
    {
        private long _lastAccessOrder;

        public RetroAchievementsGameProgress Progress { get; } = progress;
        public DateTimeOffset FetchedAtUtc { get; } = fetchedAtUtc;
        public long LastAccessOrder => Volatile.Read(ref _lastAccessOrder);

        public void Touch(long accessOrder) =>
            Interlocked.Exchange(ref _lastAccessOrder, accessOrder);
    }
}
