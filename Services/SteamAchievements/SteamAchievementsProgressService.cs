using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.SteamAchievements;

/// <summary>
/// Loads account-specific Steam achievement progress and keeps a bounded,
/// short-lived memory cache. Cache keys use a hash instead of the SteamID64.
/// </summary>
public sealed class SteamAchievementsProgressService : ISteamAchievementsProgressService
{
    private const int MaximumCacheEntries = 128;
    private const int SynchronizationGateCount = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly SteamAchievementsAccountService _accountService;
    private readonly SteamApiClient _apiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ProgressCacheKey, CacheEntry> _cache = new();
    private readonly SemaphoreSlim[] _gates = CreateSynchronizationGates();
    private readonly object _trimLock = new();
    private long _accessOrder;

    public SteamAchievementsProgressService(
        AppSettings settings,
        SteamAchievementsAccountService accountService,
        SteamApiClient apiClient)
        : this(settings, accountService, apiClient, TimeProvider.System)
    {
    }

    internal SteamAchievementsProgressService(
        AppSettings settings,
        SteamAchievementsAccountService accountService,
        SteamApiClient apiClient,
        TimeProvider timeProvider)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
            try
            {
                var apiKey = await _accountService
                    .GetApiKeyAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(apiKey))
                    throw new InvalidOperationException("No Steam Web API key is configured.");

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

    private bool TryGetFresh(
        ProgressCacheKey key,
        DateTimeOffset now,
        out CacheEntry? entry)
    {
        return TryGetCache(key, out entry) &&
               entry != null &&
               now - entry.FetchedAtUtc <= CacheLifetime;
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

    private readonly record struct ProgressCacheKey(
        string AccountIdentifierHash,
        uint AppId);

    private sealed record CacheEntry(
        SteamGameProgress Progress,
        DateTimeOffset FetchedAtUtc,
        long AccessOrder);
}
