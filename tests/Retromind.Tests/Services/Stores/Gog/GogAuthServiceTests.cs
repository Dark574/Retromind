using System.Net;
using System.Text;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogAuthServiceTests
{
    [Theory]
    [InlineData(31, true)]
    [InlineData(30, false)]
    [InlineData(29, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void HasUsableAccessToken_EnforcesRefreshBuffer(int remainingSeconds, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var token = new GogTokenSet("access-token", "refresh-token", now.AddSeconds(remainingSeconds));

        Assert.Equal(expected, GogAuthService.HasUsableAccessToken(token, now));
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_RefreshesTokenInsideBuffer()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "initial-refresh-token");
        var responses = new Queue<string>(
        [
            CreateTokenResponse("near-expiry-access-token", "rotated-refresh-token", expiresIn: 20),
            CreateTokenResponse("fresh-access-token", "fresh-refresh-token", expiresIn: 3600)
        ]);
        var refreshRequests = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            refreshRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responses.Dequeue(),
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService());

        Assert.True(await service.TryRefreshSessionAsync());
        var accessToken = await service.GetValidAccessTokenAsync();

        Assert.Equal("fresh-access-token", accessToken);
        Assert.Equal(2, refreshRequests);
        Assert.Equal(
            "fresh-refresh-token",
            await secretStore.GetAsync(new SecretKey("retromind:gog", "default")));
    }

    [Fact]
    public async Task GetValidAccessTokenAsync_ReusesTokenOutsideBuffer()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "initial-refresh-token");
        var refreshRequests = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            refreshRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    CreateTokenResponse("fresh-access-token", "fresh-refresh-token", expiresIn: 3600),
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var service = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService());

        Assert.True(await service.TryRefreshSessionAsync());
        var accessToken = await service.GetValidAccessTokenAsync();

        Assert.Equal("fresh-access-token", accessToken);
        Assert.Equal(1, refreshRequests);
    }

    private static string CreateTokenResponse(
        string accessToken,
        string refreshToken,
        int expiresIn) =>
        $$"""
          {
            "access_token": "{{accessToken}}",
            "refresh_token": "{{refreshToken}}",
            "expires_in": {{expiresIn}}
          }
          """;

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
