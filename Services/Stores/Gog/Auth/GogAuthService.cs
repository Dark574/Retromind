using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Security;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogAuthService
{
    private const string GogClientIdEnv = "RETROMIND_GOG_CLIENT_ID";
    private const string GogClientSecretEnv = "RETROMIND_GOG_CLIENT_SECRET";
    private const string GogRedirectUriEnv = "RETROMIND_GOG_REDIRECT_URI";

    // Publicly used by open-source GOG Linux integrations (not treated as confidential in practice).
    private const string DefaultGogClientId = "46899977096215655";
    private const string DefaultGogClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    private static readonly Uri DefaultGogRedirectUri = new("https://embed.gog.com/on_login_success?origin=client");
    private const string AllowedWebRedirectHost = "embed.gog.com";
    private const string AllowedWebRedirectPath = "/on_login_success";
    private static readonly TimeSpan LoopbackAuthTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AccessTokenRefreshBuffer = TimeSpan.FromSeconds(30);

    private static readonly SecretKey GogRefreshTokenKey = new("retromind:gog", "default");
    private static readonly SecretKey GogGalaxyUserIdKey = new("retromind:gog", "galaxy-user-id");

    private readonly ISecretStore _secretStore;
    private readonly GogOAuthClient _oAuthClient;
    private readonly GogPkceService _pkceService;
    private readonly IErrorService _errorService;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Uri _redirectUri;

    private volatile GogTokenSet? _currentToken;
    private StoreAccountInfo? _currentAccountInfo;

    /// <summary>Authentication can succeed for this session without a durable refresh token.</summary>
    public SecretStorageStatus RefreshTokenStorageStatus => _secretStore.GetStorageStatus(GogRefreshTokenKey);

    public GogAuthService(ISecretStore secretStore, GogOAuthClient oAuthClient, GogPkceService pkceService,
        IErrorService errorService)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _oAuthClient = oAuthClient ?? throw new ArgumentNullException(nameof(oAuthClient));
        _pkceService = pkceService ?? throw new ArgumentNullException(nameof(pkceService));
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));

        _clientId = GetEnvironmentOrDefault(GogClientIdEnv, DefaultGogClientId);
        _clientSecret = GetEnvironmentOrDefault(GogClientSecretEnv, DefaultGogClientSecret);
        _redirectUri = ResolveRedirectUri();
    }

    public Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var token = _currentToken;
        var isAuthenticated = token is { AccessTokenExpiresAtUtc: var expiry } &&
                              expiry > DateTimeOffset.UtcNow;
        var expiresAt = isAuthenticated ? token?.AccessTokenExpiresAtUtc : null;
        return Task.FromResult(new StoreAuthState(isAuthenticated, expiresAt));
    }

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default)
        => (await GetValidTokenAsync(ct).ConfigureAwait(false))?.AccessToken;

    private async Task<GogTokenSet?> GetValidTokenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var currentToken = _currentToken;
        if (HasUsableAccessToken(currentToken, DateTimeOffset.UtcNow))
            return currentToken;

        var refreshed = await TryRefreshSessionAsync(ct).ConfigureAwait(false);
        if (!refreshed)
            return null;

        return _currentToken;
    }

    internal async Task<GogGameplayCredentials?> GetGameplayCredentialsAsync(
        CancellationToken ct = default)
    {
        var token = await GetValidTokenAsync(ct).ConfigureAwait(false);
        return token == null || string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.UserId)
            ? null
            : new GogGameplayCredentials(token.AccessToken, token.UserId);
    }

    internal async Task<string?> GetGameplayUserIdAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var currentUserId = _currentToken?.UserId?.Trim();
        if (!string.IsNullOrWhiteSpace(currentUserId))
            return currentUserId;

        try
        {
            var storedUserId = await _secretStore
                .GetAsync(GogGalaxyUserIdKey, ct)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(storedUserId)
                ? null
                : storedUserId.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Stored gameplay identity could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    internal async Task<GogSessionCredentials?> GetSessionCredentialsAsync(CancellationToken ct = default)
    {
        var accessToken = await GetValidAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Login/logout may have completed while waiting; use one committed token/account snapshot.
            var token = _currentToken;
            if (token == null)
                return null;
            var galaxyUserId = token.UserId;
            if (string.IsNullOrWhiteSpace(galaxyUserId))
                return null;

            var refreshToken = await _secretStore
                .GetAsync(GogRefreshTokenKey, ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshToken) ||
                !string.Equals(refreshToken, token.RefreshToken, StringComparison.Ordinal))
                return null;

            var accountInfo = _currentAccountInfo;
            if (accountInfo == null)
            {
                accountInfo = await _oAuthClient
                    .GetAccountInfoAsync(token.AccessToken, ct)
                    .ConfigureAwait(false);
                _currentAccountInfo = accountInfo;
            }

            EnsureUsableAccountIdentity(accountInfo);
            var username = !string.IsNullOrWhiteSpace(accountInfo.DisplayName)
                ? accountInfo.DisplayName
                : accountInfo.AccountId;
            return new GogSessionCredentials(
                token.AccessToken,
                refreshToken,
                galaxyUserId,
                username);
        }
        finally { _sessionGate.Release(); }
    }

    public Task<StoreAccountInfo> SignInInteractiveAsync(
        Func<StoreSignInChallenge, CancellationToken, Task<StoreSignInResponse?>>? challengeResolver = null,
        CancellationToken ct = default)
    {
        return SignInInteractiveInternalAsync(challengeResolver, ct);
    }

    public async Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasUsableAccessToken(_currentToken, DateTimeOffset.UtcNow))
                return true;

            var refreshToken = await _secretStore.GetAsync(GogRefreshTokenKey, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshToken))
                return false;

            try
            {
                var token = await _oAuthClient
                    .RefreshAsync(_clientId, _clientSecret, refreshToken, ct)
                    .ConfigureAwait(false);

                await StoreRefreshTokenAsync(token.RefreshToken, "Refresh", ct).ConfigureAwait(false);
                await TryStoreGameplayUserIdAsync(token.UserId, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(_currentToken?.UserId, token.UserId, StringComparison.Ordinal))
                    _currentAccountInfo = null;
                _currentToken = token; // Commit: no fallible/awaited work follows activation.
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Refresh failed ({ex.GetType().Name}).");
                return false;
            }
        }
        finally { _sessionGate.Release(); }
    }

    internal static bool HasUsableAccessToken(GogTokenSet? token, DateTimeOffset utcNow)
    {
        return token is { AccessToken: { Length: > 0 }, AccessTokenExpiresAtUtc: var expiry } &&
               expiry > utcNow.Add(AccessTokenRefreshBuffer);
    }

    public async Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentToken = null;
            _currentAccountInfo = null;

            if (forgetPersistentToken)
            {
                try
                {
                    await _secretStore.DeleteAsync(GogRefreshTokenKey, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Keep the offline-cache identity and the persistent session in
                    // lockstep, even if deleting one key fails.
                    await _secretStore.DeleteAsync(GogGalaxyUserIdKey, ct).ConfigureAwait(false);
                }
            }
        }
        finally { _sessionGate.Release(); }
    }

    private async Task<StoreAccountInfo> SignInInteractiveInternalAsync(
        Func<StoreSignInChallenge, CancellationToken, Task<StoreSignInResponse?>>? challengeResolver,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var pkce = _pkceService.CreateChallenge();
        var state = CreateState();

        var authorizeUri = _oAuthClient.BuildAuthorizeUri(
            _clientId,
            _redirectUri,
            state,
            pkce.CodeChallenge,
            pkce.CodeChallengeMethod);

        OAuthCallbackResult callback;
        if (IsLoopbackUri(_redirectUri))
        {
            using var listener = new GogOAuthLoopbackListener(_redirectUri);
            if (!SystemBrowserLauncher.TryOpen(authorizeUri, out var browserError))
                throw new InvalidOperationException("Could not open system browser for GOG login.", browserError);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(LoopbackAuthTimeout);
            try
            {
                callback = await listener.WaitForCallbackAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("GOG OAuth callback timed out. Please retry sign-in.");
            }
        }
        else
        {
            if (challengeResolver == null)
            {
                throw new InvalidOperationException(
                    "Configured OAuth flow requires in-app callback handling, but no callback resolver was provided.");
            }

            var callbackUri = await ResolveCallbackUriAsync(challengeResolver, authorizeUri, ct).ConfigureAwait(false);
            ValidateCallbackUri(callbackUri, _redirectUri);
            callback = ParseCallbackFromUri(callbackUri);
        }

        if (!string.Equals(state, callback.State, StringComparison.Ordinal))
            throw new InvalidOperationException("GOG OAuth state mismatch.");

        return await ExchangeTokenAndLoadAccountAsync(callback.Code, pkce.CodeVerifier, ct).ConfigureAwait(false);
    }

    private async Task<StoreAccountInfo> ExchangeTokenAndLoadAccountAsync(
        string code,
        string? codeVerifier,
        CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var token = await _oAuthClient
                .ExchangeCodeAsync(
                    _clientId,
                    _clientSecret,
                    _redirectUri,
                    code,
                    codeVerifier,
                    ct)
                .ConfigureAwait(false);

            await StoreRefreshTokenAsync(token.RefreshToken, "SignIn", ct).ConfigureAwait(false);
            await TryStoreGameplayUserIdAsync(token.UserId, ct).ConfigureAwait(false);

            StoreAccountInfo? accountInfo = null;
            try
            {
                accountInfo = await _oAuthClient
                    .GetAccountInfoAsync(token.AccessToken, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Account info request failed ({ex.GetType().Name}).");
            }
            var result = accountInfo ?? new StoreAccountInfo("gog-user", null, null);
            ct.ThrowIfCancellationRequested();
            _currentAccountInfo = accountInfo; // Never retain an earlier account's profile on login fallback.
            _currentToken = token;
            return result;
        }
        finally { _sessionGate.Release(); }
    }

    private static async Task<Uri> ResolveCallbackUriAsync(
        Func<StoreSignInChallenge, CancellationToken, Task<StoreSignInResponse?>>? challengeResolver,
        Uri authorizeUri,
        CancellationToken ct)
    {
        if (challengeResolver == null)
            throw new InvalidOperationException(
                "Configured OAuth flow requires manual callback input, but no callback resolver was provided.");

        var response = await challengeResolver(
                new StoreSignInChallenge(authorizeUri, StoreSignInResponseKind.CallbackUri),
                ct)
            .ConfigureAwait(false);
        var callbackUri = response?.CallbackUri;
        if (callbackUri == null)
            throw new OperationCanceledException("GOG sign-in was canceled.");

        return callbackUri;
    }

    private static void ValidateCallbackUri(Uri callbackUri, Uri expectedRedirectUri)
    {
        ArgumentNullException.ThrowIfNull(callbackUri);
        ArgumentNullException.ThrowIfNull(expectedRedirectUri);

        if (!callbackUri.IsAbsoluteUri)
            throw new InvalidOperationException("OAuth callback URI must be absolute.");

        if (!string.Equals(callbackUri.Scheme, expectedRedirectUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(callbackUri.Host, expectedRedirectUri.Host, StringComparison.OrdinalIgnoreCase) ||
            callbackUri.Port != expectedRedirectUri.Port ||
            !string.Equals(callbackUri.AbsolutePath, expectedRedirectUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GOG OAuth callback redirect URI mismatch.");
        }

        if (!ContainsExpectedQueryParameters(callbackUri, expectedRedirectUri))
            throw new InvalidOperationException("GOG OAuth callback redirect URI mismatch.");
    }

    private static bool ContainsExpectedQueryParameters(Uri callbackUri, Uri expectedRedirectUri)
    {
        if (string.IsNullOrWhiteSpace(expectedRedirectUri.Query))
            return true;

        var expected = HttpUtility.ParseQueryString(expectedRedirectUri.Query);
        var actual = HttpUtility.ParseQueryString(callbackUri.Query);

        foreach (var key in expected.AllKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var expectedValue = expected[key];
            var actualValue = actual[key];
            if (!string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static OAuthCallbackResult ParseCallbackFromUri(Uri callbackUri)
    {
        var query = HttpUtility.ParseQueryString(callbackUri.Query);
        var code = query["code"];
        var state = query["state"];
        var error = query["error"];

        if (string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(callbackUri.Fragment))
        {
            var fragment = callbackUri.Fragment.TrimStart('#');
            var fragValues = HttpUtility.ParseQueryString(fragment);
            code = fragValues["code"];
            state = string.IsNullOrWhiteSpace(state) ? fragValues["state"] : state;
            error = string.IsNullOrWhiteSpace(error) ? fragValues["error"] : error;
        }

        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"GOG OAuth callback error: {error}");

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("GOG OAuth callback did not include an authorization code.");

        return new OAuthCallbackResult(code, state);
    }

    private static string CreateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void EnsureUsableAccountIdentity(StoreAccountInfo accountInfo)
    {
        if (string.IsNullOrWhiteSpace(accountInfo.AccountId) ||
            string.Equals(accountInfo.AccountId, "gog-user", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GOG account identity is unavailable.");
        }
    }

    private async Task TryStoreGameplayUserIdAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        try
        {
            var status = await _secretStore
                .SetWithStatusAsync(GogGalaxyUserIdKey, userId.Trim(), cancellationToken).ConfigureAwait(false);
            if (status == SecretStorageStatus.Failed)
                Debug.WriteLine("[GOG] Gameplay identity could not be stored.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The active session remains usable. Only cross-restart offline cache
            // lookup is unavailable until a later identity write succeeds.
            Debug.WriteLine($"[GOG] Gameplay identity could not be stored ({ex.GetType().Name}).");
        }
    }

    private async Task StoreRefreshTokenAsync(string token, string operation, CancellationToken ct)
    {
        SecretStorageStatus status;
        try
        {
            status = await _secretStore.SetWithStatusAsync(GogRefreshTokenKey, token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            ReportTokenStorageFailure(operation, SecretStorageStatus.Failed);
            throw new InvalidOperationException("The GOG refresh token could not be stored; the new session was not activated.");
        }
        if (status is SecretStorageStatus.Persistent or SecretStorageStatus.SessionOnly)
            return;

        ReportTokenStorageFailure(operation, status);
        throw new InvalidOperationException("The GOG refresh token could not be stored; the new session was not activated.");
    }

    private void ReportTokenStorageFailure(string operation, SecretStorageStatus status)
    {
        // This concrete store already reports failed backend writes, without exposing their payloads.
        if (status == SecretStorageStatus.Failed && _secretStore is CompositeSecretStore)
            return;
        try
        {
            _errorService.Report(new ErrorReport("GogAuth.TokenStorageFailed", ErrorSeverity.Error, nameof(GogAuthService),
                "The new GOG session was not activated because refresh-token storage was not confirmed. An older refresh token may no longer be valid.",
                Context: new Dictionary<string, string>
                {
                    ["Operation"] = operation,
                    ["StorageStatus"] = status.ToString(),
                    ["OAuthResponseReceived"] = "True"
                }));
        }
        catch { /* Diagnostics must not activate a rejected token or mask the persistence failure. */ }
    }

    private static string GetEnvironmentOrDefault(string variableName, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static Uri ResolveRedirectUri()
    {
        var value = Environment.GetEnvironmentVariable(GogRedirectUriEnv);
        if (!string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            if (IsSupportedRedirectUri(parsed))
                return parsed;

            Debug.WriteLine("[GOG] Ignoring unsupported RETROMIND_GOG_REDIRECT_URI value.");
        }

        return DefaultGogRedirectUri;
    }

    private static bool IsSupportedRedirectUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            return false;

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            return false;

        if (IsLoopbackUri(uri))
            return true;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort &&
               string.Equals(uri.Host, AllowedWebRedirectHost, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, AllowedWebRedirectPath, StringComparison.Ordinal);
    }

    private static bool IsLoopbackUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}
