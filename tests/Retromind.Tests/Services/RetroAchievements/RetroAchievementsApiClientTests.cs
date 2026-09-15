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
