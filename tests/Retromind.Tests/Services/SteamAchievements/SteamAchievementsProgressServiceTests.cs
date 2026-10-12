using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Models;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.SteamAchievements;

public sealed class SteamAchievementsProgressServiceTests
{
    private const string SteamId64 = "76561198000000042";

    [Fact]
    public async Task GetProgressAsync_ReusesFreshEntryAndForceRefreshes()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            return CreateApiResponse(request);
        }));
        var service = CreateService(httpClient);

        var first = await service.GetProgressAsync(123);
        var cached = await service.GetProgressAsync(123);
        var refreshed = await service.GetProgressAsync(123, forceRefresh: true);

        Assert.Equal(4, requestCount);
        Assert.Same(first.Progress, cached.Progress);
        Assert.NotSame(first.Progress, refreshed.Progress);
        Assert.False(cached.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_UsesPreviousEntryWhenRefreshFails()
    {
        var failRequests = false;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (failRequests)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            return CreateApiResponse(request);
        }));
        var service = CreateService(httpClient);
        var first = await service.GetProgressAsync(123);
        failRequests = true;

        var fallback = await service.GetProgressAsync(123, forceRefresh: true);

        Assert.Same(first.Progress, fallback.Progress);
        Assert.Equal(first.FetchedAtUtc, fallback.FetchedAtUtc);
        Assert.True(fallback.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_UsesPreviousEntryWhenResponseBodyFails()
    {
        var failResponseBody = false;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (failResponseBody)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new FailingReadContent()
                };
            }

            return CreateApiResponse(request);
        }));
        var service = CreateService(httpClient);
        var first = await service.GetProgressAsync(123);
        failResponseBody = true;

        var fallback = await service.GetProgressAsync(123, forceRefresh: true);

        Assert.Same(first.Progress, fallback.Progress);
        Assert.Equal(first.FetchedAtUtc, fallback.FetchedAtUtc);
        Assert.True(fallback.UsedCachedFallback);
    }

    [Fact]
    public async Task GetProgressAsync_RequiresEnabledConfiguredAccount()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
            CreateApiResponse(request)));
        var settings = new AppSettings
        {
            SteamAchievements = new SteamAchievementsSettings
            {
                Enabled = false,
                SteamId64 = SteamId64
            }
        };
        var service = CreateService(httpClient, settings);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetProgressAsync(123));
    }

    private static SteamAchievementsProgressService CreateService(
        HttpClient httpClient,
        AppSettings? settings = null)
    {
        settings ??= new AppSettings
        {
            SteamAchievements = new SteamAchievementsSettings
            {
                Enabled = true,
                SteamId64 = SteamId64
            }
        };
        var apiClient = new SteamApiClient(httpClient);
        var accountService = new SteamAchievementsAccountService(
            apiClient,
            new TestSecretStore("api-key"));
        return new SteamAchievementsProgressService(
            settings,
            accountService,
            apiClient);
    }

    private static HttpResponseMessage CreateApiResponse(
        HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath.Contains(
                "GetSchemaForGame",
                StringComparison.Ordinal))
        {
            return JsonResponse(
                """
                {
                  "game": {
                    "gameName": "Test game",
                    "availableGameStats": {
                      "achievements": [
                        {
                          "name": "ACH_FIRST",
                          "displayName": "First",
                          "hidden": 0,
                          "icon": "https://example.com/unlocked.jpg",
                          "icongray": "https://example.com/locked.jpg"
                        }
                      ]
                    }
                  }
                }
                """);
        }

        return JsonResponse(
            $$"""
            {
              "playerstats": {
                "steamID": "{{SteamId64}}",
                "achievements": [
                  {
                    "apiname": "ACH_FIRST",
                    "achieved": 1,
                    "unlocktime": 1760000000
                  }
                ],
                "success": true
              }
            }
            """);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private sealed class TestSecretStore(string? storedValue) : ISecretStore
    {
        public Task<bool> IsAvailableAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task SetAsync(
            SecretKey key,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> GetAsync(
            SecretKey key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(storedValue);

        public Task DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

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

    private sealed class FailingReadContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            return Task.FromException(
                new HttpRequestException("The response stream failed."));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
