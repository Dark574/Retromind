using System.Net;
using System.Text.Json;
using System.Web;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogAuthServiceStateTests
{
    private static readonly SecretKey RefreshKey = new("retromind:gog", "default");

    [Theory]
    [InlineData(SecretStorageStatus.Persistent)]
    [InlineData(SecretStorageStatus.SessionOnly)]
    public async Task Refresh_ActivatesOnlyAfterStorageCompletes(SecretStorageStatus status)
    {
        var store = await CreateStoreAsync();
        var entered = NewSignal();
        var release = NewSignal();
        store.Write = async (key, value, ct) =>
        {
            if (key == RefreshKey)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            await store.Values.SetAsync(key, value, ct);
            return status;
        };
        var errors = new RecordingErrorService();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var service = CreateService(store, http, errors);

        var refresh = service.TryRefreshSessionAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
            Assert.Equal("initial-refresh", await store.Values.GetAsync(RefreshKey));
        }
        finally { release.TrySetResult(); }

        Assert.True(await refresh);
        Assert.Equal("new-access", await service.GetValidAccessTokenAsync());
        var gameplay = await service.GetGameplayCredentialsAsync();
        Assert.NotNull(gameplay);
        Assert.Equal("new-access", gameplay.AccessToken);
        Assert.Equal("new-user", gameplay.UserId);
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData(SecretStorageStatus.Failed)]
    [InlineData(SecretStorageStatus.Unknown)]
    public async Task FailedStorage_NeverExposesCandidateAndCannotAssumeOldRefreshTokenStillWorks(SecretStorageStatus status)
    {
        var store = await CreateStoreAsync();
        store.Write = (_, _, _) => Task.FromResult(status);
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(++requests == 1
            ? TokenResponse() : JsonResponse(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""))));
        var errors = new RecordingErrorService();
        var service = CreateService(store, http, errors);

        Assert.False(await service.TryRefreshSessionAsync());
        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
        Assert.Null(await service.GetValidAccessTokenAsync());
        Assert.Equal(2, requests);
        Assert.Null(await service.GetGameplayCredentialsAsync());
        Assert.Null(await service.GetSessionCredentialsAsync());
        Assert.Equal("initial-refresh", await store.Values.GetAsync(RefreshKey));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("GogAuth.TokenStorageFailed", report.Code);
        Assert.Equal("Refresh", report.Context!["Operation"]);
        Assert.Equal(status.ToString(), report.Context["StorageStatus"]);
    }

    [Fact]
    public async Task FailedRefresh_PreservesPreviousActiveTokenAndExpiry()
    {
        var store = await CreateStoreAsync();
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            ++requests == 1 ? TokenResponse("previous-access", "previous-refresh", "previous-user", 20) : TokenResponse())));
        var service = CreateService(store, http);
        Assert.True(await service.TryRefreshSessionAsync());
        var previousState = await service.GetAuthStateAsync();
        store.Write = (_, _, _) => Task.FromResult(SecretStorageStatus.Failed);

        Assert.False(await service.TryRefreshSessionAsync());

        Assert.Equal(previousState, await service.GetAuthStateAsync());
        Assert.Equal("previous-user", await service.GetGameplayUserIdAsync());
        Assert.Equal("previous-refresh", await store.Values.GetAsync(RefreshKey));
    }

    [Fact]
    public async Task CancellationDuringStorage_DoesNotActivateCandidateAndReleasesGate()
    {
        var store = await CreateStoreAsync();
        var entered = NewSignal();
        store.Write = async (_, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return SecretStorageStatus.Persistent;
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var errors = new RecordingErrorService();
        var service = CreateService(store, http, errors);
        using var cts = new CancellationTokenSource();
        var refresh = service.TryRefreshSessionAsync(cts.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cts.Cancel(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
        Assert.Equal("initial-refresh", await store.Values.GetAsync(RefreshKey));
        Assert.Empty(errors.Reports);
        store.Write = null;
        Assert.True(await service.TryRefreshSessionAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterAWriteWasCommitted_DoesNotActivateOrUndoStoredToken(bool duringIdentity)
    {
        var store = await CreateStoreAsync();
        using var cts = new CancellationTokenSource();
        store.Write = async (key, value, _) =>
        {
            // Simulate a backend that commits before observing cancellation (including the identity write).
            await store.Values.SetAsync(key, value);
            if ((key != RefreshKey) == duringIdentity) cts.Cancel();
            return SecretStorageStatus.Persistent;
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var errors = new RecordingErrorService();
        var service = CreateService(store, http, errors);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TryRefreshSessionAsync(cts.Token));

        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
        Assert.Equal("new-refresh", await store.Values.GetAsync(RefreshKey));
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InteractiveLogin_StorageFailureOrCancellationPreservesPreviousSession(bool cancel)
    {
        using var environment = UseDefaultRedirect();
        var store = await CreateStoreAsync();
        var loginCount = 0;
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Post
            ? ++loginCount == 1 ? TokenResponse("previous-access", "previous-refresh", "previous-user") : TokenResponse()
            : ProfileResponse("PreviousUser"))));
        var errors = new RecordingErrorService();
        var service = CreateService(store, http, errors);
        await SignInAsync(service);
        var previousState = await service.GetAuthStateAsync();
        using var cts = new CancellationTokenSource();
        store.Write = (_, _, _) => cancel
            ? Task.FromException<SecretStorageStatus>(new OperationCanceledException(cts.Token))
            : Task.FromResult(SecretStorageStatus.Failed);

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SignInAsync(service, cts.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => SignInAsync(service));

        Assert.Equal(previousState, await service.GetAuthStateAsync());
        Assert.Equal("previous-access", await service.GetValidAccessTokenAsync());
        Assert.Equal("previous-refresh", await store.Values.GetAsync(RefreshKey));
        if (cancel) Assert.Empty(errors.Reports);
        else Assert.Equal("SignIn", Assert.Single(errors.Reports).Context!["Operation"]);
    }

    [Fact]
    public async Task FirstInteractiveLogin_FailedStorageLeavesServiceUnauthenticated()
    {
        using var environment = UseDefaultRedirect();
        var store = await CreateStoreAsync();
        store.Write = (_, _, _) => Task.FromResult(SecretStorageStatus.Failed);
        var profileRequests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) profileRequests++;
            return Task.FromResult(TokenResponse());
        }));
        var service = CreateService(store, http);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SignInAsync(service));

        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
        Assert.Equal(0, profileRequests);
        Assert.Null(await store.Values.GetAsync(new SecretKey("retromind:gog", "galaxy-user-id")));
    }

    [Fact]
    public async Task CancelledAccountInfoRequest_KeepsPreviousAccountAndNeverMixesCredentialPairs()
    {
        using var environment = UseDefaultRedirect();
        var store = await CreateStoreAsync();
        var loginCount = 0;
        var accountEntered = NewSignal();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
                return ++loginCount == 1 ? TokenResponse("old-access", "old-refresh", "old-user") : TokenResponse();
            if (loginCount == 1) return ProfileResponse("OldUser");
            accountEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return ProfileResponse("NewUser");
        }));
        var service = CreateService(store, http);
        await SignInAsync(service);
        using var cts = new CancellationTokenSource();
        var login = SignInAsync(service, cts.Token);
        try
        {
            await accountEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("old-access", await service.GetValidAccessTokenAsync());
        }
        finally { cts.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login);

        var gameplay = await service.GetGameplayCredentialsAsync();
        Assert.NotNull(gameplay);
        Assert.Equal("old-access", gameplay.AccessToken);
        Assert.Equal("old-user", gameplay.UserId);
        Assert.Equal("new-refresh", await store.Values.GetAsync(RefreshKey));
        Assert.Null(await service.GetSessionCredentialsAsync());
    }

    [Fact]
    public async Task LoginWithAccountInfoFailure_ActivatesSavedTokenAndDoesNotReusePreviousProfile()
    {
        using var environment = UseDefaultRedirect();
        var store = await CreateStoreAsync();
        var loginCount = 0;
        var profileCount = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(++loginCount == 1 ? TokenResponse("old-access", "old-refresh", "old-user") : TokenResponse());
            return Task.FromResult(++profileCount switch
            {
                1 => ProfileResponse("OldUser"),
                2 => JsonResponse(HttpStatusCode.ServiceUnavailable, "{}"),
                _ => ProfileResponse("NewUser")
            });
        }));
        var service = CreateService(store, http);
        await SignInAsync(service);

        var fallbackAccount = await SignInAsync(service);
        var credentials = await service.GetSessionCredentialsAsync();

        Assert.Equal("gog-user", fallbackAccount.AccountId);
        Assert.NotNull(credentials);
        Assert.Equal("NewUser", credentials.Username);
        Assert.Equal("new-user", credentials.UserId);
        Assert.Equal("new-access", credentials.AccessToken);
        Assert.Equal("new-refresh", credentials.RefreshToken);
        Assert.Equal(3, profileCount);
    }

    [Fact]
    public async Task CompositeStorageFailure_DoesNotDuplicateBackendDiagnostics()
    {
        var primary = await CreateStoreAsync();
        primary.FailLegacyWrite = true;
        var fallback = new ControlledStore { FailLegacyWrite = true };
        var errors = new RecordingErrorService();
        var composite = new CompositeSecretStore(primary, fallback, errors);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var service = CreateService(composite, http, errors);

        Assert.False(await service.TryRefreshSessionAsync());

        Assert.Equal(2, errors.Reports.Count);
        Assert.Contains(errors.Reports, report => report.Code == "SecretStore.PrimaryWriteFailed");
        Assert.Contains(errors.Reports, report => report.Code == "SecretStore.FallbackWriteFailed");
        Assert.DoesNotContain(errors.Reports, report => report.Code == "GogAuth.TokenStorageFailed");
        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
    }

    [Fact]
    public async Task AuthDiagnostics_AreSafeAndCannotChangeFailureOutcome()
    {
        using var environment = UseDefaultRedirect();
        var store = await CreateStoreAsync();
        store.Write = (_, _, _) => throw new InvalidOperationException("new-access new-refresh oauth-code client-secret raw-server-body");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = CreateService(store, http, errors);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => SignInAsync(service));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("new-refresh", exception.ToString());
        var report = Assert.Single(errors.Reports);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Equal(nameof(GogAuthService), report.Component);
        Assert.Null(report.Exception);
        var diagnostic = JsonSerializer.Serialize(report);
        foreach (var sensitive in new[] { "new-access", "new-refresh", "oauth-code", "client-secret", "raw-server-body" })
            Assert.DoesNotContain(sensitive, diagnostic);
        Assert.All(errors.Notifications, notification => Assert.Null(notification));
        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
    }

    [Fact]
    public async Task ConcurrentRefreshes_AreSerializedAndReuseOneCommittedToken()
    {
        var store = await CreateStoreAsync();
        var entered = NewSignal();
        var release = NewSignal();
        var requests = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return TokenResponse();
        }));
        var service = CreateService(store, http);
        var first = service.TryRefreshSessionAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.TryRefreshSessionAsync();
        release.TrySetResult();

        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)), result => Assert.True(result));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task SignOutDuringPendingStorage_CannotBeUndoneByLateActivation()
    {
        var store = await CreateStoreAsync();
        var entered = NewSignal();
        var release = NewSignal();
        store.Write = async (key, value, ct) =>
        {
            if (key == RefreshKey) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            await store.Values.SetAsync(key, value, ct);
            return SecretStorageStatus.Persistent;
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var service = CreateService(store, http);
        var refresh = service.TryRefreshSessionAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var logout = service.SignOutAsync(forgetPersistentToken: true);
        release.TrySetResult();

        await Task.WhenAll(refresh, logout).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
        Assert.Null(await service.GetValidAccessTokenAsync());
        Assert.Null(await store.Values.GetAsync(RefreshKey));
    }

    [Fact]
    public async Task PreCanceledLogin_DoesNotInvokeInteractiveCallback()
    {
        var store = await CreateStoreAsync();
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("No request expected")));
        var service = CreateService(store, http);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SignInInteractiveAsync(
            (_, _) => throw new InvalidOperationException("No callback expected"), cts.Token));
        Assert.False((await service.GetAuthStateAsync()).IsAuthenticated);
    }

    [Fact]
    public async Task BestEffortIdentityStorageFailure_DoesNotRejectSavedTokenOrGameplayCredentials()
    {
        var store = await CreateStoreAsync();
        store.Write = async (key, value, ct) =>
        {
            if (key != RefreshKey) return SecretStorageStatus.Failed;
            await store.Values.SetAsync(key, value, ct);
            return SecretStorageStatus.Persistent;
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(TokenResponse())));
        var service = CreateService(store, http);

        Assert.True(await service.TryRefreshSessionAsync());

        Assert.Equal("new-user", await service.GetGameplayUserIdAsync());
        var gameplay = await service.GetGameplayCredentialsAsync();
        Assert.NotNull(gameplay);
        Assert.Equal("new-user", gameplay.UserId);
        Assert.Equal("new-access", gameplay.AccessToken);
    }

    private static GogAuthService CreateService(ISecretStore store, HttpClient http, RecordingErrorService? errors = null)
        => new(store, new GogOAuthClient(http), new GogPkceService(), errors ?? new RecordingErrorService());

    private static async Task<ControlledStore> CreateStoreAsync()
    {
        var store = new ControlledStore();
        await store.Values.SetAsync(RefreshKey, "initial-refresh");
        return store;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static EnvironmentVariableScope UseDefaultRedirect() => new(("RETROMIND_GOG_REDIRECT_URI", null));
    private static Task<StoreAccountInfo> SignInAsync(GogAuthService service, CancellationToken ct = default)
        => service.SignInInteractiveAsync((challenge, _) =>
        {
            var state = HttpUtility.ParseQueryString(challenge.AuthorizationUri.Query)["state"];
            return Task.FromResult<StoreSignInResponse?>(StoreSignInResponse.FromCallbackUri(new Uri(
                $"https://embed.gog.com/on_login_success?origin=client&code=oauth-code&state={Uri.EscapeDataString(state!)}")));
        }, ct);

    private static HttpResponseMessage TokenResponse(string access = "new-access", string refresh = "new-refresh",
        string user = "new-user", int expires = 3600) => JsonResponse(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { access_token = access, refresh_token = refresh, user_id = user, expires_in = expires }));

    private static HttpResponseMessage ProfileResponse(string username) => JsonResponse(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { userId = username, username }));
    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request, ct);
    }

    private sealed class ControlledStore : IPersistentSecretStore
    {
        internal InMemorySecretStore Values { get; } = new();
        internal Func<SecretKey, string, CancellationToken, Task<SecretStorageStatus>>? Write { get; set; }
        internal bool FailLegacyWrite { get; set; }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
            => FailLegacyWrite ? throw new IOException("Sensitive backend output") : Values.SetAsync(key, secret, ct);
        public async Task<SecretStorageStatus> SetWithStatusAsync(SecretKey key, string secret, CancellationToken ct = default)
        {
            if (Write != null) return await Write(key, secret, ct);
            await SetAsync(key, secret, ct);
            return SecretStorageStatus.Persistent;
        }
        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default) => Values.GetAsync(key, ct);
        public Task DeleteAsync(SecretKey key, CancellationToken ct = default) => Values.DeleteAsync(key, ct);
    }
}
