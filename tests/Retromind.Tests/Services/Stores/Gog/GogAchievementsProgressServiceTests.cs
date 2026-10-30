using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Achievements;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogAchievementsProgressServiceTests : IDisposable
{
    private readonly TemporaryDirectory _cacheDirectory = new();

    [Fact]
    public async Task GetProgressAsync_ReusesFreshEntryAndForceRefreshes()
    {
        var gameplayRequests = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/token")
                return TokenResponse();

            gameplayRequests++;
            return ProgressResponse();
        }));
        var service = await CreateServiceAsync(httpClient);

        var first = await service.GetProgressAsync("1994983163");
        var cached = await service.GetProgressAsync("1994983163");
        var refreshed = await service.GetProgressAsync("1994983163", forceRefresh: true);

        Assert.Equal(2, gameplayRequests);
        Assert.Same(first.Progress, cached.Progress);
        Assert.NotSame(first.Progress, refreshed.Progress);
        Assert.False(cached.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_RefreshesAfterOneHour()
    {
        var gameplayRequests = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/token")
                return TokenResponse();

            gameplayRequests++;
            return ProgressResponse();
        }));
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
        var service = await CreateServiceAsync(httpClient, timeProvider: time);

        var first = await service.GetProgressAsync("1994983163");
        time.Advance(TimeSpan.FromMinutes(59));
        var cached = await service.GetProgressAsync("1994983163");
        time.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await service.GetProgressAsync("1994983163");

        Assert.Equal(2, gameplayRequests);
        Assert.Same(first.Progress, cached.Progress);
        Assert.NotSame(first.Progress, refreshed.Progress);
    }

    [Fact]
    public async Task GetProgressAsync_UsesPreviousEntryWhenRefreshFails()
    {
        var failGameplayRequest = false;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/token")
                return TokenResponse();

            return failGameplayRequest
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : ProgressResponse();
        }));
        var service = await CreateServiceAsync(httpClient);
        var first = await service.GetProgressAsync("1994983163");
        failGameplayRequest = true;

        var fallback = await service.GetProgressAsync(
            "1994983163",
            forceRefresh: true);

        Assert.Same(first.Progress, fallback.Progress);
        Assert.Equal(first.FetchedAtUtc, fallback.FetchedAtUtc);
        Assert.True(fallback.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_RequiresEnabledIntegration()
    {
        var requests = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return ProgressResponse();
        }));
        var secretStore = new InMemorySecretStore();
        var authService = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService(), new RecordingErrorService());
        var service = new GogAchievementsProgressService(
            new AppSettings { EnableGogGalaxyIntegration = false },
            authService,
            new GogAchievementsApiClient(httpClient),
            TimeProvider.System,
            _cacheDirectory.RootPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetProgressAsync("1994983163"));

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task GetProgressAsync_UsesPersistentFallbackAfterRestart()
    {
        var secretStore = new InMemorySecretStore();
        using var onlineClient = new HttpClient(new StubHandler(request =>
            request.RequestUri?.AbsolutePath == "/token"
                ? TokenResponse()
                : ProgressResponse()));
        var onlineService = await CreateServiceAsync(onlineClient, secretStore);
        var online = await onlineService.GetProgressAsync("1994983163");
        var cachePath = Assert.Single(
            Directory.EnumerateFiles(
                _cacheDirectory.RootPath,
                "*.json",
                SearchOption.AllDirectories));
        Assert.DoesNotContain(
            "46988829925743611",
            cachePath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "46988829925743611",
            await File.ReadAllTextAsync(cachePath),
            StringComparison.Ordinal);

        using var offlineClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var restartedAuthService = new GogAuthService(
            secretStore,
            new GogOAuthClient(offlineClient),
            new GogPkceService(), new RecordingErrorService());
        var restartedService = new GogAchievementsProgressService(
            new AppSettings { EnableGogGalaxyIntegration = true },
            restartedAuthService,
            new GogAchievementsApiClient(offlineClient),
            TimeProvider.System,
            _cacheDirectory.RootPath);

        var fallback = await restartedService.GetProgressAsync(
            "1994983163",
            forceRefresh: true);

        Assert.NotSame(online.Progress, fallback.Progress);
        Assert.Equal("First", Assert.Single(fallback.Progress.Achievements).Title);
        Assert.Equal(online.FetchedAtUtc, fallback.FetchedAtUtc);
        Assert.True(fallback.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_DoesNotReuseAnotherAccountsPersistentCache()
    {
        var originalSecretStore = new InMemorySecretStore();
        using var onlineClient = new HttpClient(new StubHandler(request =>
            request.RequestUri?.AbsolutePath == "/token"
                ? TokenResponse()
                : ProgressResponse()));
        _ = await (await CreateServiceAsync(onlineClient, originalSecretStore))
            .GetProgressAsync("1994983163");

        var otherAccountSecretStore = new InMemorySecretStore();
        await otherAccountSecretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "other-refresh-token");
        await otherAccountSecretStore.SetAsync(
            new SecretKey("retromind:gog", "galaxy-user-id"),
            "46988829925743999");
        using var offlineClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var authService = new GogAuthService(
            otherAccountSecretStore,
            new GogOAuthClient(offlineClient),
            new GogPkceService(), new RecordingErrorService());
        var restartedService = new GogAchievementsProgressService(
            new AppSettings { EnableGogGalaxyIntegration = true },
            authService,
            new GogAchievementsApiClient(offlineClient),
            TimeProvider.System,
            _cacheDirectory.RootPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedService.GetProgressAsync("1994983163", forceRefresh: true));
    }

    private async Task<GogAchievementsProgressService> CreateServiceAsync(
        HttpClient httpClient,
        InMemorySecretStore? secretStore = null,
        TimeProvider? timeProvider = null)
    {
        secretStore ??= new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "refresh-token");
        var authService = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService(), new RecordingErrorService());
        return new GogAchievementsProgressService(
            new AppSettings { EnableGogGalaxyIntegration = true },
            authService,
            new GogAchievementsApiClient(httpClient),
            timeProvider ?? TimeProvider.System,
            _cacheDirectory.RootPath);
    }

    public void Dispose() => _cacheDirectory.Dispose();

    private static HttpResponseMessage TokenResponse() =>
        JsonResponse(
            """
            {
              "access_token": "access-token",
              "refresh_token": "rotated-refresh-token",
              "user_id": "46988829925743611",
              "expires_in": 3600
            }
            """);

    private static HttpResponseMessage ProgressResponse() =>
        JsonResponse(
            """
            {
              "items": [
                {
                  "achievement_id": "101",
                  "achievement_key": "FIRST",
                  "name": "First",
                  "description": "Complete the first task.",
                  "image_url_locked": "https://images.gog.com/locked.png",
                  "image_url_unlocked": "https://images.gog.com/unlocked.png",
                  "visible": true,
                  "date_unlocked": "2026-10-19T05:17:20+00:00",
                  "rarity": 58.1,
                  "rarity_level_description": "Common"
                }
              ]
            }
            """);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration) => utcNow += duration;
    }
}
