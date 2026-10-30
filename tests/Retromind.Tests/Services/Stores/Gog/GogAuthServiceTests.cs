using System.Net;
using System.Text;
using System.Web;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;
using PrimaryStore = Retromind.Tests.Services.Stores.Security.SecretStoreSafetyTests.ControllableSecretStore;

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
        var token = new GogTokenSet(
            "access-token",
            "refresh-token",
            "galaxy-user-id",
            now.AddSeconds(remainingSeconds));

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
            new GogPkceService(), new RecordingErrorService());

        Assert.True(await service.TryRefreshSessionAsync());
        var accessToken = await service.GetValidAccessTokenAsync();

        Assert.Equal("fresh-access-token", accessToken);
        Assert.Equal(2, refreshRequests);
        Assert.Equal(
            "fresh-refresh-token",
            await secretStore.GetAsync(new SecretKey("retromind:gog", "default")));
        Assert.Equal(
            "galaxy-user-id",
            await secretStore.GetAsync(new SecretKey("retromind:gog", "galaxy-user-id")));
        Assert.Equal("galaxy-user-id", await service.GetGameplayUserIdAsync());
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
            new GogPkceService(), new RecordingErrorService());

        Assert.True(await service.TryRefreshSessionAsync());
        var accessToken = await service.GetValidAccessTokenAsync();

        Assert.Equal("fresh-access-token", accessToken);
        Assert.Equal(1, refreshRequests);
    }

    [Fact]
    public async Task SignOutAsync_RemovesStoredGameplayIdentityWithSession()
    {
        var secretStore = new InMemorySecretStore();
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "default"),
            "refresh-token");
        await secretStore.SetAsync(
            new SecretKey("retromind:gog", "galaxy-user-id"),
            "galaxy-user-id");
        using var httpClient = new HttpClient();
        var service = new GogAuthService(
            secretStore,
            new GogOAuthClient(httpClient),
            new GogPkceService(), new RecordingErrorService());

        await service.SignOutAsync(forgetPersistentToken: true);

        Assert.Null(await secretStore.GetAsync(new SecretKey("retromind:gog", "default")));
        Assert.Null(await secretStore.GetAsync(new SecretKey("retromind:gog", "galaxy-user-id")));
        Assert.Null(await service.GetGameplayUserIdAsync());
    }

    [Fact]
    public async Task RefreshWithSessionFallback_UsesRotatedTokenAfterPrimaryRecovers()
    {
        var key = new SecretKey("retromind:gog", "default");
        var primary = new PrimaryStore { FailWrite = true };
        await primary.Values.SetAsync(key, "original-refresh");
        var secrets = new CompositeSecretStore(primary, new InMemorySecretStore(), new RecordingErrorService());
        var forms = new List<string>();
        using var http = new HttpClient(new AsyncStubHandler(async request =>
        {
            forms.Add(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CreateTokenResponse("access", $"rotated-{forms.Count}", forms.Count == 1 ? 20 : 3600))
            };
        }));
        var service = new GogAuthService(secrets, new GogOAuthClient(http), new GogPkceService(), new RecordingErrorService());

        Assert.True(await service.TryRefreshSessionAsync());
        Assert.Equal(SecretStorageStatus.SessionOnly, service.RefreshTokenStorageStatus);
        Assert.True((await service.GetAuthStateAsync()).IsAuthenticated);
        primary.FailWrite = false;
        Assert.True(await service.TryRefreshSessionAsync());

        Assert.Equal(2, forms.Count);
        Assert.Contains("refresh_token=original-refresh", forms[0]);
        Assert.Contains("refresh_token=rotated-1", forms[1]);
        Assert.Equal(SecretStorageStatus.Persistent, service.RefreshTokenStorageStatus);
        Assert.Equal("rotated-2", await primary.Values.GetAsync(key));
    }

    [Fact]
    public async Task InteractiveSignIn_WithUnavailablePrimary_RemainsAuthenticatedButSessionOnly()
    {
        using var environment = new EnvironmentVariableScope(("RETROMIND_GOG_REDIRECT_URI", null));
        var primary = new PrimaryStore { Available = false };
        var secrets = new CompositeSecretStore(primary, new InMemorySecretStore(), new RecordingErrorService());
        using var http = new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.Method == HttpMethod.Post
                ? CreateTokenResponse("login-access", "login-refresh", 3600)
                : """{"userId":"galaxy-user-id","username":"TestUser"}""")
        }));
        var service = new GogAuthService(secrets, new GogOAuthClient(http), new GogPkceService(), new RecordingErrorService());

        var account = await service.SignInInteractiveAsync((challenge, _) =>
        {
            var state = HttpUtility.ParseQueryString(challenge.AuthorizationUri.Query)["state"];
            return Task.FromResult<StoreSignInResponse?>(StoreSignInResponse.FromCallbackUri(
                new Uri($"https://embed.gog.com/on_login_success?origin=client&code=code&state={Uri.EscapeDataString(state!)}")));
        });

        Assert.Equal("TestUser", account.DisplayName);
        Assert.True((await service.GetAuthStateAsync()).IsAuthenticated);
        Assert.Equal(SecretStorageStatus.SessionOnly, service.RefreshTokenStorageStatus);
        primary.Available = true;
        Assert.Equal("login-access", await service.GetValidAccessTokenAsync());
        Assert.Null(await primary.Values.GetAsync(new SecretKey("retromind:gog", "default")));
    }

    [Fact]
    public async Task SignOutWithFailedPersistentDelete_ReportsFailureAndCannotRefreshFromOldToken()
    {
        var primary = new PrimaryStore { FailDelete = true };
        await primary.Values.SetAsync(new SecretKey("retromind:gog", "default"), "old-refresh");
        await primary.Values.SetAsync(new SecretKey("retromind:gog", "galaxy-user-id"), "user-id");
        var secrets = new CompositeSecretStore(primary, new InMemorySecretStore(), new RecordingErrorService());
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No refresh request expected")));
        var service = new GogAuthService(secrets, new GogOAuthClient(http), new GogPkceService(), new RecordingErrorService());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignOutAsync(forgetPersistentToken: true));

        Assert.Equal(2, primary.DeleteCalls);
        Assert.Null(await service.GetValidAccessTokenAsync());
        Assert.Null(await service.GetGameplayUserIdAsync());
        Assert.Equal(SecretStorageStatus.Failed, service.RefreshTokenStorageStatus);
    }

    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    private static string CreateTokenResponse(
        string accessToken,
        string refreshToken,
        int expiresIn) =>
        $$"""
          {
            "access_token": "{{accessToken}}",
            "refresh_token": "{{refreshToken}}",
            "user_id": "galaxy-user-id",
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
