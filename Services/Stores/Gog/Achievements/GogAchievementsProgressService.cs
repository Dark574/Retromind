using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Auth;

namespace Retromind.Services.Stores.Gog.Achievements;

public sealed class GogAchievementsProgressService : IGogAchievementsProgressService
{
    private const int MaximumCacheEntries = 128;
    private const int SynchronizationGateCount = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly AppSettings _settings;
    private readonly GogAuthService _authService;
    private readonly GogAchievementsApiClient _apiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<ProgressCacheKey, CacheEntry> _cache = new();
    private readonly SemaphoreSlim[] _gates = CreateSynchronizationGates();
    private readonly object _trimLock = new();
    private long _accessOrder;

    public GogAchievementsProgressService(
        AppSettings settings,
        GogAuthService authService,
        GogAchievementsApiClient apiClient)
        : this(settings, authService, apiClient, TimeProvider.System)
    {
    }

    internal GogAchievementsProgressService(
        AppSettings settings,
        GogAuthService authService,
        GogAchievementsApiClient apiClient,
        TimeProvider timeProvider)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<GogProgressSnapshot> GetProgressAsync(
        string gameId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        gameId = gameId.Trim();
        if (_settings.EnableGogGalaxyIntegration != true)
            throw new InvalidOperationException("GOG Galaxy integration is not enabled.");

        var credentials = await _authService
            .GetGameplayCredentialsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (credentials == null)
            throw new InvalidOperationException("No GOG account is configured.");

        var cacheKey = new ProgressCacheKey(
            HashAccountIdentifier(credentials.UserId),
            gameId);
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
                var progress = await _apiClient
                    .GetGameProgressAsync(
                        gameId,
                        credentials.UserId,
                        credentials.AccessToken,
                        GetGogLanguage(CultureInfo.CurrentUICulture),
                        cancellationToken)
                    .ConfigureAwait(false);
                var refreshed = new CacheEntry(
                    progress,
                    _timeProvider.GetUtcNow(),
                    NextAccessOrder());
                StoreCache(cacheKey, refreshed);
                return CreateSnapshot(refreshed, usedCachedFallback: false);
            }
            catch (GogAchievementsApiException) when (fallback != null)
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
        var hash = HashCode.Combine(key.AccountIdentifierHash, key.GameId) & int.MaxValue;
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

    private static GogProgressSnapshot CreateSnapshot(
        CacheEntry entry,
        bool usedCachedFallback) =>
        new(entry.Progress, entry.FetchedAtUtc, usedCachedFallback);

    private static string HashAccountIdentifier(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetGogLanguage(CultureInfo culture) =>
        string.IsNullOrWhiteSpace(culture.Name) ? "en-US" : culture.Name;

    private readonly record struct ProgressCacheKey(
        string AccountIdentifierHash,
        string GameId);

    private sealed record CacheEntry(
        GogGameProgress Progress,
        DateTimeOffset FetchedAtUtc,
        long AccessOrder);
}
