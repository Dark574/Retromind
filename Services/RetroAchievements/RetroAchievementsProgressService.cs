using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    private readonly ConcurrentDictionary<ProgressCacheKey, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<ProgressCacheKey, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<ProgressCacheKey, byte> _invalidated = new();

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
        string cacheDirectory)
        : this(
            settings,
            accountService,
            apiClient,
            timeProvider,
            () => Path.GetFullPath(cacheDirectory))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
    }

    private RetroAchievementsProgressService(
        AppSettings settings,
        RetroAchievementsAccountService accountService,
        RetroAchievementsApiClient apiClient,
        TimeProvider timeProvider,
        Func<string> cacheDirectoryProvider)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _cacheDirectoryProvider = cacheDirectoryProvider ??
                                  throw new ArgumentNullException(nameof(cacheDirectoryProvider));
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

        var gate = _gates.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            forceRefresh |= _invalidated.ContainsKey(cacheKey);
            now = _timeProvider.GetUtcNow();
            if (!forceRefresh && TryGetFresh(cacheKey, now, out cached))
                return CreateSnapshot(cached!, usedCachedFallback: false);

            _cache.TryGetValue(cacheKey, out var fallback);
            if (fallback == null)
            {
                fallback = await TryLoadCacheAsync(cacheKey, cacheDirectory, cancellationToken)
                    .ConfigureAwait(false);
                if (fallback != null)
                    _cache[cacheKey] = fallback;
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
                _cache[cacheKey] = refreshed;
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
        if (_cache.TryGetValue(key, out entry) && IsFresh(entry, now))
            return true;

        entry = null;
        return false;
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

    private sealed record CacheEntry(
        RetroAchievementsGameProgress Progress,
        DateTimeOffset FetchedAtUtc);
}
