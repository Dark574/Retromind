using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Gog.Comet;
using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogCometCredentialProviderTests
{
    [Fact]
    public async Task GetCredentialsAsync_UsesFreshAuthSessionAndStoredRefreshToken()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "initial-refresh-token");
        AuthenticationHeaderValue? accountAuthorization = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/token")
            {
                return JsonResponse(
                    """
                    {
                      "access_token": "fresh-access-token",
                      "refresh_token": "rotated-refresh-token",
                      "user_id": "galaxy-user-id",
                      "expires_in": 3600
                    }
                    """);
            }

            if (request.RequestUri?.AbsolutePath == "/userData.json")
            {
                accountAuthorization = request.Headers.Authorization;
                return JsonResponse(
                    """
                    {
                      "userId": "123456",
                      "username": "GalaxyPlayer"
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var authService = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService());
        var provider = new GogCometCredentialProvider(authService);

        var credentials = await provider.GetCredentialsAsync();

        Assert.NotNull(credentials);
        Assert.Equal("fresh-access-token", credentials.AccessToken);
        Assert.Equal("rotated-refresh-token", credentials.RefreshToken);
        Assert.Equal("galaxy-user-id", credentials.UserId);
        Assert.Equal("GalaxyPlayer", credentials.Username);
        Assert.Equal("Bearer", accountAuthorization?.Scheme);
        Assert.Equal("fresh-access-token", accountAuthorization?.Parameter);
        Assert.Equal(
            "rotated-refresh-token",
            await secretStore.GetAsync(new SecretKey("retromind:gog", "default")));
    }

    [Fact]
    public async Task GetCredentialsAsync_ReturnsNullWithoutStoredSession()
    {
        var requests = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }));
        var authService = new GogAuthService(
            new InMemorySecretStore(),
            new GogOAuthClient(httpClient),
            new GogPkceService());
        var provider = new GogCometCredentialProvider(authService);

        var credentials = await provider.GetCredentialsAsync();

        Assert.Null(credentials);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task GetCredentialsAsync_RejectsMissingAccountIdentity()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "refresh-token");
        var responses = new Queue<HttpResponseMessage>(
        [
            JsonResponse(
                """
                {
                  "access_token": "access-token",
                  "refresh_token": "refresh-token",
                  "user_id": "galaxy-user-id",
                  "expires_in": 3600
                }
                """),
            JsonResponse("{}")
        ]);
        using var httpClient = new HttpClient(new StubHandler(_ => responses.Dequeue()));
        var authService = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService());
        var provider = new GogCometCredentialProvider(authService);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetCredentialsAsync());

        Assert.Contains("identity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access-token", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token", exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

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
