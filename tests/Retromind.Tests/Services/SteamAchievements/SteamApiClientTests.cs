using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Services.SteamAchievements;

namespace Retromind.Tests.Services.SteamAchievements;

public sealed class SteamApiClientTests
{
    [Fact]
    public async Task GetPlayerSummaryAsync_ParsesProfileAndUsesHeaderForApiKey()
    {
        const string steamId64 = "76561198000000042";
        const string apiKey = "secret-api-key";

        Uri? requestedUri = null;
        string? sentApiKey = null;

        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;

            if (request.Headers.TryGetValues(
                    "x-webapi-key",
                    out var values))
            {
                sentApiKey = Assert.Single(values);
            }

            return JsonResponse(
                $$"""
                {
                  "response": {
                    "players": [
                      {
                        "steamid": "{{steamId64}}",
                        "personaname": "Test player",
                        "profileurl": "https://steamcommunity.com/profiles/{{steamId64}}/",
                        "avatarfull": "https://example.com/avatar.jpg"
                      }
                    ]
                  }
                }
                """);
        }));

        var client = new SteamApiClient(httpClient);

        var profile = await client.GetPlayerSummaryAsync(
            steamId64,
            apiKey);

        Assert.Equal(steamId64, profile.SteamId64);
        Assert.Equal("Test player", profile.PersonaName);
        Assert.Equal(
            $"https://steamcommunity.com/profiles/{steamId64}/",
            profile.ProfileUrl);
        Assert.Equal("https://example.com/avatar.jpg", profile.AvatarUrl);

        Assert.NotNull(requestedUri);
        Assert.Equal("api.steampowered.com", requestedUri.Host);
        Assert.Contains(
            $"steamids={steamId64}",
            requestedUri.Query,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            apiKey,
            requestedUri.OriginalString,
            StringComparison.Ordinal);
        Assert.Equal(apiKey, sentApiKey);
    }

    [Fact]
    public async Task GetPlayerSummaryAsync_RejectsMissingRequestedAccount()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse(
                """
                {
                  "response": {
                    "players": []
                  }
                }
                """)));

        var client = new SteamApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<SteamApiException>(
            () => client.GetPlayerSummaryAsync(
                "76561198000000042",
                "api-key"));

        Assert.Contains(
            "did not return the requested account",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPlayerSummaryAsync_PreservesHttpStatusWithoutExposingKey()
    {
        const string apiKey = "do-not-expose";

        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var client = new SteamApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<SteamApiException>(
            () => client.GetPlayerSummaryAsync(
                "76561198000000042",
                apiKey));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.DoesNotContain(
            apiKey,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameProgressAsync_MergesSchemaAndProgressWithoutExposingKey()
    {
        const string steamId64 = "76561198000000042";
        const string apiKey = "secret-api-key";
        var requests = new List<(Uri Uri, string? ApiKey)>();

        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            var sentApiKey = request.Headers.TryGetValues(
                "x-webapi-key",
                out var values)
                ? Assert.Single(values)
                : null;
            requests.Add((request.RequestUri!, sentApiKey));

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
                              "displayName": "First achievement",
                              "description": "Unlock the first achievement.",
                              "hidden": 0,
                              "icon": "https://example.com/first-unlocked.jpg",
                              "icongray": "https://example.com/first-locked.jpg"
                            },
                            {
                              "name": "ACH_SECRET",
                              "displayName": "Secret title",
                              "description": "Secret description",
                              "hidden": 1,
                              "icon": "https://example.com/secret-unlocked.jpg",
                              "icongray": "https://example.com/secret-locked.jpg"
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
                    "steamID": "{{steamId64}}",
                    "gameName": "Test game",
                    "achievements": [
                      {
                        "apiname": "ACH_FIRST",
                        "achieved": 1,
                        "unlocktime": 1760000000
                      },
                      {
                        "apiname": "ACH_SECRET",
                        "achieved": 0,
                        "unlocktime": 0
                      }
                    ],
                    "success": true
                  }
                }
                """);
        }));

        var client = new SteamApiClient(httpClient);

        var progress = await client.GetGameProgressAsync(
            123,
            steamId64,
            apiKey,
            "german");

        Assert.Equal((uint)123, progress.AppId);
        Assert.Equal("Test game", progress.Title);
        Assert.Equal(2, progress.AchievementCount);
        Assert.Equal(1, progress.UnlockedCount);
        Assert.Equal(50, progress.CompletionPercent);
        Assert.Equal(1, progress.HiddenAchievementCount);
        Assert.Equal(1, progress.LockedHiddenAchievementCount);

        var unlocked = Assert.Single(
            progress.Achievements,
            achievement => achievement.ApiName == "ACH_FIRST");
        Assert.True(unlocked.IsUnlocked);
        Assert.Equal("First achievement", unlocked.Title);
        Assert.Equal("Unlock the first achievement.", unlocked.Description);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1760000000),
            unlocked.UnlockedAtUtc);
        Assert.Equal(
            "https://example.com/first-unlocked.jpg",
            unlocked.ImageUrl);

        var hidden = Assert.Single(
            progress.Achievements,
            achievement => achievement.ApiName == "ACH_SECRET");
        Assert.True(hidden.IsHidden);
        Assert.False(hidden.IsUnlocked);
        Assert.Empty(hidden.Title);
        Assert.Empty(hidden.Description);
        Assert.Null(hidden.ImageUrl);

        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("api.steampowered.com", request.Uri.Host);
            Assert.Equal(apiKey, request.ApiKey);
            Assert.DoesNotContain(
                apiKey,
                request.Uri.OriginalString,
                StringComparison.Ordinal);
            Assert.Contains(
                "l=german",
                request.Uri.Query,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetGameProgressAsync_RevealsUnlockedHiddenAchievement()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
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
                              "name": "ACH_SECRET",
                              "displayName": "Secret title",
                              "description": "Secret description",
                              "hidden": 1,
                              "icon": "https://example.com/secret-unlocked.jpg",
                              "icongray": "https://example.com/secret-locked.jpg"
                            }
                          ]
                        }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "playerstats": {
                    "steamID": "76561198000000042",
                    "achievements": [
                      {
                        "apiname": "ACH_SECRET",
                        "achieved": 1,
                        "unlocktime": 1760000000
                      }
                    ],
                    "success": true
                  }
                }
                """);
        }));

        var client = new SteamApiClient(httpClient);

        var progress = await client.GetGameProgressAsync(
            123,
            "76561198000000042",
            "api-key");

        var achievement = Assert.Single(progress.Achievements);
        Assert.True(achievement.IsHidden);
        Assert.True(achievement.IsUnlocked);
        Assert.Equal("Secret title", achievement.Title);
        Assert.Equal("Secret description", achievement.Description);
        Assert.Equal(
            "https://example.com/secret-unlocked.jpg",
            achievement.ImageUrl);
        Assert.Equal(0, progress.LockedHiddenAchievementCount);
    }

    [Fact]
    public async Task GetGameProgressAsync_SkipsPlayerRequestWhenGameHasNoAchievements()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return JsonResponse(
                """
                {
                  "game": {
                    "gameName": "Game without achievements",
                    "availableGameStats": {
                      "stats": []
                    }
                  }
                }
                """);
        }));

        var client = new SteamApiClient(httpClient);

        var progress = await client.GetGameProgressAsync(
            123,
            "76561198000000042",
            "api-key");

        Assert.Equal(1, requestCount);
        Assert.Equal("Game without achievements", progress.Title);
        Assert.Empty(progress.Achievements);
    }

    [Fact]
    public async Task GetGameProgressAsync_ReportsUnavailablePrivateProgress()
    {
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains(
                    "GetSchemaForGame",
                    StringComparison.Ordinal))
            {
                return JsonResponse(
                    """
                    {
                      "game": {
                        "availableGameStats": {
                          "achievements": [
                            { "name": "ACH_FIRST", "displayName": "First" }
                          ]
                        }
                      }
                    }
                    """);
            }

            return JsonResponse(
                """
                {
                  "playerstats": {
                    "success": false,
                    "error": "Profile is not public"
                  }
                }
                """);
        }));

        var client = new SteamApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<SteamApiException>(
            () => client.GetGameProgressAsync(
                123,
                "76561198000000042",
                "api-key"));

        Assert.Contains(
            "privacy settings",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };
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
}
