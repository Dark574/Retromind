using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsProgressServiceTests
{
    [Fact]
    public async Task GetProgressAsync_UsesStableUlidAndCachesForFiveMinutes()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        Uri? requestedUri = null;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(
            request =>
            {
                requestCount++;
                requestedUri = request.RequestUri;
                return ProgressResponse(awardedCount: 1);
            },
            time,
            temp.GetPath("progress-cache"));

        var first = await service.GetProgressAsync(123);
        time.Advance(TimeSpan.FromMinutes(4));
        var second = await service.GetProgressAsync(123);

        Assert.Equal(1, requestCount);
        Assert.Equal(1, first.Progress.AwardedCount);
        Assert.Equal(first, second);
        Assert.False(first.UsedCachedFallback);
        Assert.NotNull(requestedUri);
        Assert.Contains("u=01TESTULID", requestedUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("MutableName", requestedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProgressAsync_ExpiredCacheIsRefreshed()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(
            _ => ProgressResponse(awardedCount: ++requestCount),
            time,
            temp.GetPath("progress-cache"));
        _ = await service.GetProgressAsync(123);
        time.Advance(TimeSpan.FromMinutes(5));

        var refreshed = await service.GetProgressAsync(123);

        Assert.Equal(2, requestCount);
        Assert.Equal(2, refreshed.Progress.AwardedCount);
        Assert.False(refreshed.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_ForcedRefreshReturnsMarkedFallbackOnApiFailure()
    {
        using var temp = new TemporaryDirectory();
        var fail = false;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(
            _ => fail
                ? throw new HttpRequestException("offline")
                : ProgressResponse(awardedCount: 1),
            time,
            temp.GetPath("progress-cache"));
        var original = await service.GetProgressAsync(123);
        fail = true;

        var fallback = await service.GetProgressAsync(123, forceRefresh: true);

        Assert.True(fallback.UsedCachedFallback);
        Assert.Equal(original.FetchedAtUtc, fallback.FetchedAtUtc);
        Assert.Equal(1, fallback.Progress.AwardedCount);
    }

    [Fact]
    public async Task GetProgressAsync_DisabledIntegrationFailsBeforeApiRequest()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var service = await CreateServiceAsync(
            _ =>
            {
                requestCount++;
                return ProgressResponse(awardedCount: 1);
            },
            time,
            temp.GetPath("progress-cache"),
            enabled: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProgressAsync(123));

        Assert.Equal("RetroAchievements is not enabled.", exception.Message);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task GetProgressAsync_PersistsCacheWithoutCredentialsAndReusesItAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        var cacheDirectory = temp.GetPath("progress-cache");
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var firstService = await CreateServiceAsync(
            _ => ProgressResponse(awardedCount: 3),
            time,
            cacheDirectory);

        _ = await firstService.GetProgressAsync(123);

        var cachePath = Assert.Single(
            Directory.GetFiles(cacheDirectory, "game-123.json", SearchOption.AllDirectories));
        var cacheContents = await File.ReadAllTextAsync(cachePath);
        Assert.DoesNotContain("secret-key", cacheContents, StringComparison.Ordinal);
        Assert.DoesNotContain("01TESTULID", cacheContents, StringComparison.Ordinal);
        Assert.DoesNotContain("MutableName", cacheContents, StringComparison.Ordinal);
        Assert.DoesNotContain("01TESTULID", cachePath, StringComparison.Ordinal);

        var secondService = await CreateServiceAsync(
            _ => throw new InvalidOperationException("Fresh disk cache should prevent an API call."),
            time,
            cacheDirectory);

        var restored = await secondService.GetProgressAsync(123);

        Assert.Equal(3, restored.Progress.AwardedCount);
        Assert.False(restored.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_UsesStalePersistedCacheWhenApiIsUnavailable()
    {
        using var temp = new TemporaryDirectory();
        var cacheDirectory = temp.GetPath("progress-cache");
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var initialService = await CreateServiceAsync(
            _ => ProgressResponse(awardedCount: 4),
            time,
            cacheDirectory);
        var original = await initialService.GetProgressAsync(123);
        time.Advance(TimeSpan.FromMinutes(6));
        var offlineService = await CreateServiceAsync(
            _ => throw new HttpRequestException("offline"),
            time,
            cacheDirectory);

        var fallback = await offlineService.GetProgressAsync(123);

        Assert.Equal(4, fallback.Progress.AwardedCount);
        Assert.Equal(original.FetchedAtUtc, fallback.FetchedAtUtc);
        Assert.True(fallback.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_ForceRefreshBypassesFreshMemoryAndPersistedCache()
    {
        using var temp = new TemporaryDirectory();
        var cacheDirectory = temp.GetPath("progress-cache");
        var requestCount = 0;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(
            _ => ProgressResponse(awardedCount: ++requestCount),
            time,
            cacheDirectory);
        _ = await service.GetProgressAsync(123);

        var refreshed = await service.GetProgressAsync(123, forceRefresh: true);

        Assert.Equal(2, requestCount);
        Assert.Equal(2, refreshed.Progress.AwardedCount);
    }

    [Fact]
    public async Task GetProgressAsync_TrimsMemoryCacheToConfiguredLimit()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(
            _ => ProgressResponse(
                awardedCount: 1,
                gameId: 101 + requestCount++),
            time,
            temp.GetPath("progress-cache"),
            maximumMemoryCacheEntries: 2);

        _ = await service.GetProgressAsync(101);
        _ = await service.GetProgressAsync(102);
        _ = await service.GetProgressAsync(101);
        _ = await service.GetProgressAsync(103);

        Assert.Equal(3, requestCount);
        Assert.Equal(2, service.MemoryCacheEntryCount);
    }

    private static async Task<RetroAchievementsProgressService> CreateServiceAsync(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        TimeProvider timeProvider,
        string cacheDirectory,
        bool enabled = true,
        int maximumMemoryCacheEntries = 128)
    {
        var settings = new AppSettings
        {
            RetroAchievements = new RetroAchievementsSettings
            {
                Enabled = enabled,
                Username = "MutableName",
                UserUlid = "01TESTULID"
            }
        };
        var httpClient = new HttpClient(new StubHandler(responseFactory));
        var apiClient = new RetroAchievementsApiClient(httpClient);
        var accountService = new RetroAchievementsAccountService(
            apiClient,
            new InMemorySecretStore());
        await accountService.StoreApiKeyAsync("secret-key");

        return new RetroAchievementsProgressService(
            settings,
            accountService,
            apiClient,
            timeProvider,
            cacheDirectory,
            maximumMemoryCacheEntries);
    }

    private static HttpResponseMessage ProgressResponse(int awardedCount, int gameId = 123)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "ID": {{gameId}},
                  "Title": "Test Game",
                  "ConsoleID": 4,
                  "NumAchievements": 10,
                  "Achievements": {},
                  "NumAwardedToUser": {{awardedCount}},
                  "NumAwardedToUserHardcore": 0,
                  "UserCompletion": "10.00%",
                  "UserCompletionHardcore": "0.00%",
                  "UserTotalPlaytime": 60
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
