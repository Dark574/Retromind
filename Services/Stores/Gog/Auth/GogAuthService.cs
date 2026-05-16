using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
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
    private static readonly TimeSpan LoopbackAuthTimeout = TimeSpan.FromMinutes(5);

    private static readonly SecretKey GogRefreshTokenKey = new("retromind:gog", "default");

    private readonly ISecretStore _secretStore;
    private readonly GogOAuthClient _oAuthClient;
    private readonly GogPkceService _pkceService;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Uri _redirectUri;

    private GogTokenSet? _currentToken;

    public GogAuthService(ISecretStore secretStore, GogOAuthClient oAuthClient, GogPkceService pkceService)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _oAuthClient = oAuthClient ?? throw new ArgumentNullException(nameof(oAuthClient));
        _pkceService = pkceService ?? throw new ArgumentNullException(nameof(pkceService));

        _clientId = GetEnvironmentOrDefault(GogClientIdEnv, DefaultGogClientId);
        _clientSecret = GetEnvironmentOrDefault(GogClientSecretEnv, DefaultGogClientSecret);
        _redirectUri = ResolveRedirectUri();
    }

    public Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        var isAuthenticated = _currentToken is { AccessTokenExpiresAtUtc: var expiry } &&
                              expiry > DateTimeOffset.UtcNow;
        var expiresAt = isAuthenticated ? _currentToken?.AccessTokenExpiresAtUtc : null;
        return Task.FromResult(new StoreAuthState(isAuthenticated, expiresAt));
    }

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        if (_currentToken is { AccessToken: { Length: > 0 } accessToken, AccessTokenExpiresAtUtc: var expiry } &&
            expiry > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return accessToken;
        }

        var refreshed = await TryRefreshSessionAsync(ct).ConfigureAwait(false);
        if (!refreshed)
            return null;

        return _currentToken?.AccessToken;
    }

    public Task<StoreAccountInfo> SignInInteractiveAsync(
        Func<Uri, CancellationToken, Task<Uri?>>? callbackUriResolver = null,
        CancellationToken ct = default)
    {
        return SignInInteractiveInternalAsync(callbackUriResolver, ct);
    }

    public async Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
    {
        if (_currentToken is { AccessTokenExpiresAtUtc: var expiry } && expiry > DateTimeOffset.UtcNow)
            return true;

        var refreshToken = await _secretStore.GetAsync(GogRefreshTokenKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        try
        {
            var token = await _oAuthClient
                .RefreshAsync(_clientId, _clientSecret, refreshToken, ct)
                .ConfigureAwait(false);

            _currentToken = token;
            await _secretStore.SetAsync(GogRefreshTokenKey, token.RefreshToken, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Refresh failed: {ex.Message}");
            return false;
        }
    }

    public async Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default)
    {
        _currentToken = null;

        if (forgetPersistentToken)
            await _secretStore.DeleteAsync(GogRefreshTokenKey, ct).ConfigureAwait(false);
    }

    private async Task<StoreAccountInfo> SignInInteractiveInternalAsync(
        Func<Uri, CancellationToken, Task<Uri?>>? callbackUriResolver,
        CancellationToken ct)
    {
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
            OpenSystemBrowser(authorizeUri);

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
            if (callbackUriResolver == null)
            {
                throw new InvalidOperationException(
                    "Configured OAuth flow requires in-app callback handling, but no callback resolver was provided.");
            }

            var callbackUri = await ResolveCallbackUriAsync(callbackUriResolver, authorizeUri, ct).ConfigureAwait(false);
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
        var token = await _oAuthClient
            .ExchangeCodeAsync(
                _clientId,
                _clientSecret,
                _redirectUri,
                code,
                codeVerifier,
                ct)
            .ConfigureAwait(false);

        _currentToken = token;
        await _secretStore.SetAsync(GogRefreshTokenKey, token.RefreshToken, ct).ConfigureAwait(false);

        try
        {
            return await _oAuthClient.GetAccountInfoAsync(token.AccessToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Account info request failed: {ex.Message}");
            return new StoreAccountInfo("gog-user", null, null);
        }
    }

    private static async Task<Uri> ResolveCallbackUriAsync(
        Func<Uri, CancellationToken, Task<Uri?>>? callbackUriResolver,
        Uri authorizeUri,
        CancellationToken ct)
    {
        if (callbackUriResolver == null)
            throw new InvalidOperationException(
                "Configured OAuth flow requires manual callback input, but no callback resolver was provided.");

        var callbackUri = await callbackUriResolver(authorizeUri, ct).ConfigureAwait(false);
        if (callbackUri == null)
            throw new OperationCanceledException("GOG sign-in was canceled.");

        return callbackUri;
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

    private static void OpenSystemBrowser(Uri uri)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });

            if (process == null)
                throw new InvalidOperationException("Browser process could not be started.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not open system browser for GOG login.", ex);
        }
    }

    private static string CreateState()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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
            return parsed;

        return DefaultGogRedirectUri;
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
