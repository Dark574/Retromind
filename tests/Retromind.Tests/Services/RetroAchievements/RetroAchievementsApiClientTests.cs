using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Services.RetroAchievements;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsApiClientTests
{
    [Fact]
    public async Task GetUserProfileAsync_ParsesProfileAndEncodesCredentials()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                {
                  "User": "Test User",
                  "ULID": "01TESTULID",
                  "TotalPoints": 1234,
                  "TotalSoftcorePoints": 56,
                  "TotalTruePoints": 7890,
                  "UserPic": "/UserPic/Test.png"
                }
                """);
        }));
        var client = new RetroAchievementsApiClient(httpClient);

        var profile = await client.GetUserProfileAsync("Test User", "key+/=");

        Assert.Equal("Test User", profile.Username);
        Assert.Equal("01TESTULID", profile.UserUlid);
        Assert.Equal(1234, profile.TotalPoints);
        Assert.Equal(56, profile.TotalSoftcorePoints);
        Assert.Equal(7890, profile.TotalTruePoints);
        Assert.Equal("/UserPic/Test.png", profile.UserPicturePath);
        Assert.NotNull(requestedUri);
        Assert.Equal("retroachievements.org", requestedUri.Host);
        Assert.Contains("u=Test%20User", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("y=key%2B%2F%3D", requestedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserProfileAsync_ReportsApiErrorWithoutExposingKey()
    {
        const string apiKey = "do-not-expose-this-key";
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse($$"""{"Success":false,"Error":"Invalid API key {{apiKey}}."}""")));
        var client = new RetroAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<RetroAchievementsApiException>(
            () => client.GetUserProfileAsync("TestUser", apiKey));

        Assert.Contains("Invalid API key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserProfileAsync_RejectsProfileWithoutStableUlid()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse("""{"User":"TestUser","TotalPoints":10}""")));
        var client = new RetroAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<RetroAchievementsApiException>(
            () => client.GetUserProfileAsync("TestUser", "key"));

        Assert.Contains("missing required account data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserProfileAsync_ParsesCurrentUnauthorizedResponseFormat()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"message":"Unauthenticated.","errors":[{"status":"401","code":"unauthorized","title":"Unauthenticated."}]}""",
                    Encoding.UTF8,
                    "application/json")
            }));
        var client = new RetroAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<RetroAchievementsApiException>(
            () => client.GetUserProfileAsync("TestUser", "invalid-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Contains("Unauthenticated", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameCatalogAsync_RequestsAchievementGamesWithHashesAndParsesEntries()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return JsonResponse(
                """
                [
                  {
                    "ID": 123,
                    "Title": "Test Game",
                    "ConsoleID": 4,
                    "ConsoleName": "Game Boy",
                    "ImageIcon": "/Images/123.png",
                    "NumAchievements": 10,
                    "Hashes": ["25F9E794323B453885F5181F1B624D0B", "invalid"]
                  },
                  {
                    "ID": 124,
                    "Title": "No Supported Files",
                    "ConsoleID": 4,
                    "Hashes": []
                  }
                ]
                """);
        }));
        var client = new RetroAchievementsApiClient(httpClient);

        var games = await client.GetGameCatalogAsync(4, "key+/=");

        var game = Assert.Single(games);
        Assert.Equal(123, game.GameId);
        Assert.Equal("Test Game", game.Title);
        Assert.Equal((uint)4, game.ConsoleId);
        Assert.Equal("Game Boy", game.ConsoleName);
        Assert.Equal(10, game.AchievementCount);
        Assert.Equal("25f9e794323b453885f5181f1b624d0b", Assert.Single(game.Hashes));
        Assert.NotNull(requestedUri);
        Assert.Contains("i=4", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("f=1", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("h=1", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("y=key%2B%2F%3D", requestedUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameCatalogAsync_ReportsApiErrorWithoutExposingKey()
    {
        const string apiKey = "do-not-expose-this-key";
        using var httpClient = new HttpClient(new StubHandler(_ =>
            JsonResponse($$"""{"Success":false,"Error":"Invalid API key {{apiKey}}."}""")));
        var client = new RetroAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<RetroAchievementsApiException>(
            () => client.GetGameCatalogAsync(4, apiKey));

        Assert.Contains("Invalid API key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetGameCatalogAsync_NetworkFailureDoesNotRetainKeyInExceptionChain()
    {
        const string apiKey = "do-not-expose-this-key";
        using var httpClient = new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException($"Failed URI contained {apiKey}")));
        var client = new RetroAchievementsApiClient(httpClient);

        var exception = await Assert.ThrowsAsync<RetroAchievementsApiException>(
            () => client.GetGameCatalogAsync(4, apiKey));

        Assert.DoesNotContain(apiKey, exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
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
