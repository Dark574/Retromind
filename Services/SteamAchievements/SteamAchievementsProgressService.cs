using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Services.Achievements;

namespace Retromind.Services.SteamAchievements;

/// <summary>
/// Loads account-specific Steam achievement progress and keeps a bounded,
/// short-lived memory cache and a user-separated persistent cache. Cache keys
/// use a hash instead of the SteamID64.
/// </summary>
public sealed class SteamAchievementsProgressService : ISteamAchievementsProgressService
{
    private const int CacheSchemaVersion = 1;
    private const int MaximumCacheEntries = 512;
    private const int SynchronizationGateCount = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly AppSettings _settings;
    private readonly SteamAchievementsAccountService _accountService;
    private readonly SteamApiClient _apiClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _cacheDirectoryProvider;
    private readonly ConcurrentDictionary<ProgressCacheKey, CacheEntry> _cache = new();
    private readonly SemaphoreSlim[] _gates = CreateSynchronizationGates();
    private readonly object _trimLock = new();
    private long _accessOrder;

    public SteamAchievementsProgressService(
        AppSettings settings,
        SteamAchievementsAccountService accountService,
        SteamApiClient apiClient,
        StoreAchievementCachePathProvider cachePathProvider)
        : this(
            settings,
            accountService,
            apiClient,
            TimeProvider.System,
            cachePathProvider == null
                ? throw new ArgumentNullException(nameof(cachePathProvider))
                : cachePathProvider.GetSteamProgressCacheDirectory)
    {
    }

    internal SteamAchievementsProgressService(
        AppSettings settings,
        SteamAchievementsAccountService accountService,
        SteamApiClient apiClient,
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

    private SteamAchievementsProgressService(
        AppSettings settings,
        SteamAchievementsAccountService accountService,
        SteamApiClient apiClient,
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

    public async Task<SteamProgressSnapshot> GetProgressAsync(
        uint appId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId));

        var settings = _settings.SteamAchievements;
        if (settings?.Enabled != true)
            throw new InvalidOperationException("Steam achievements are not enabled.");

        var steamId64 = settings.SteamId64?.Trim();
        if (string.IsNullOrWhiteSpace(steamId64))
            throw new InvalidOperationException("No Steam account is configured.");

        var cacheKey = new ProgressCacheKey(
            HashAccountIdentifier(steamId64),
            appId);
        var cacheDirectory = _cacheDirectoryProvider();
        var now = _timeProvider.GetUtcNow();
        if (!forceRefresh && TryGetFresh(cacheKey, now, out var cached))
            return CreateSnapshot(cached!, usedCachedFallback: false);

        var gate = GetSynchronizationGate(cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (!forceRefresh && TryGetFresh(cacheKey, now, out cached))
                return CreateSnapshot(cached!, usedCachedFallback: false);

            TryGetCache(cacheKey, out var fallback);
            if (fallback == null)
            {
                fallback = await TryLoadCacheAsync(
                        cacheKey,
                        cacheDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (fallback != null)
                    StoreCache(cacheKey, fallback);
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
                    if (fallback != null)
                        return CreateSnapshot(fallback, usedCachedFallback: true);

                    throw new InvalidOperationException("No Steam Web API key is configured.");
                }

                var progress = await _apiClient
                    .GetGameProgressAsync(
                        appId,
                        steamId64,
                        apiKey,
                        GetSteamLanguage(CultureInfo.CurrentUICulture),
                        cancellationToken)
                    .ConfigureAwait(false);
                var refreshed = new CacheEntry(
                    progress,
                    _timeProvider.GetUtcNow(),
                    NextAccessOrder());
                StoreCache(cacheKey, refreshed);
                await TrySaveCacheAsync(
                        cacheKey,
                        refreshed,
                        cacheDirectory,
                        cancellationToken)
                    .ConfigureAwait(false);
                return CreateSnapshot(refreshed, usedCachedFallback: false);
            }
            catch (SteamApiException) when (fallback != null)
            {
                return CreateSnapshot(fallback, usedCachedFallback: true);
            }
        }
        finally
        {
            gate.Release();
        }
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
                document.AppId != key.AppId ||
                document.FetchedAtUtc == default ||
                document.Progress == null ||
                document.Progress.AppId != key.AppId ||
                document.Progress.Achievements == null ||
                document.Progress.Achievements.Any(
                    achievement => string.IsNullOrWhiteSpace(achievement.ApiName)))
            {
                throw new InvalidDataException("The Steam achievement cache is invalid.");
            }

            return new CacheEntry(
                document.Progress,
                document.FetchedAtUtc,
                NextAccessOrder());
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
            Debug.WriteLine($"[Steam] Ignoring invalid achievement cache: {ex.Message}");
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
            AppId = key.AppId,
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
            Debug.WriteLine($"[Steam] Could not persist achievement cache: {ex.Message}");
        }
    }

    private static string GetCachePath(string cacheDirectory, ProgressCacheKey key) =>
        Path.Combine(
            cacheDirectory,
            key.AccountIdentifierHash,
            $"app-{key.AppId}.json");

    private bool TryGetFresh(
        ProgressCacheKey key,
        DateTimeOffset now,
        out CacheEntry? entry)
    {
        return TryGetCache(key, out entry) &&
               entry != null &&
               IsFresh(entry, now);
    }

    private bool TryGetCache(ProgressCacheKey key, out CacheEntry? entry)
    {
        if (!_cache.TryGetValue(key, out entry))
            return false;

        var touched = entry with { AccessOrder = NextAccessOrder() };
        _cache.TryUpdate(key, touched, entry);
        entry = touched;
        return true;
    }

    private void StoreCache(ProgressCacheKey key, CacheEntry entry)
    {
        _cache[key] = entry;
        if (_cache.Count <= MaximumCacheEntries)
            return;

        lock (_trimLock)
        {
            var excess = _cache.Count - MaximumCacheEntries;
            if (excess <= 0)
                return;

            foreach (var candidate in _cache
                         .OrderBy(pair => pair.Value.AccessOrder)
                         .Take(excess))
            {
                _cache.TryRemove(candidate.Key, out _);
            }
        }
    }

    private SemaphoreSlim GetSynchronizationGate(ProgressCacheKey key)
    {
        var hash = HashCode.Combine(key.AccountIdentifierHash, key.AppId) & int.MaxValue;
        return _gates[hash % _gates.Length];
    }

    private static SemaphoreSlim[] CreateSynchronizationGates()
    {
        var gates = new SemaphoreSlim[SynchronizationGateCount];
        for (var index = 0; index < gates.Length; index++)
            gates[index] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private long NextAccessOrder() => Interlocked.Increment(ref _accessOrder);

    private static bool IsFresh(CacheEntry entry, DateTimeOffset now) =>
        entry.FetchedAtUtc <= now && now - entry.FetchedAtUtc < CacheLifetime;

    private static SteamProgressSnapshot CreateSnapshot(
        CacheEntry entry,
        bool usedCachedFallback) =>
        new(entry.Progress, entry.FetchedAtUtc, usedCachedFallback);

    private static string HashAccountIdentifier(string steamId64)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(steamId64));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetSteamLanguage(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "de" => "german",
            _ => "english"
        };

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // best effort; later refreshes use unique temporary paths
        }
    }

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }
        public uint AppId { get; set; }
        public DateTimeOffset FetchedAtUtc { get; set; }
        public SteamGameProgress? Progress { get; set; }
    }

    private readonly record struct ProgressCacheKey(
        string AccountIdentifierHash,
        uint AppId);

    private sealed record CacheEntry(
        SteamGameProgress Progress,
        DateTimeOffset FetchedAtUtc,
        long AccessOrder);
}
