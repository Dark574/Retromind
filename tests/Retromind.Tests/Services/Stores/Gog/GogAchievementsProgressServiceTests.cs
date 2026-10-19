using System.Net;
using System.Text;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Achievements;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogAchievementsProgressServiceTests
{
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
            new GogPkceService());
        var service = new GogAchievementsProgressService(
            new AppSettings { EnableGogGalaxyIntegration = false },
            authService,
            new GogAchievementsApiClient(httpClient));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetProgressAsync("1994983163"));

        Assert.Equal(0, requests);
    }

    private static async Task<GogAchievementsProgressService> CreateServiceAsync(
        HttpClient httpClient)
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "refresh-token");
        var authService = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService());
        return new GogAchievementsProgressService(
            new AppSettings { EnableGogGalaxyIntegration = true },
            authService,
            new GogAchievementsApiClient(httpClient));
    }

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
}
