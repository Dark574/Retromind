using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Security;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogAuthService
{
    private static readonly SecretKey GogRefreshTokenKey = new("retromind:gog", "default");

    private readonly ISecretStore _secretStore;
    private readonly GogOAuthClient _oAuthClient;
    private readonly GogPkceService _pkceService;

    private GogTokenSet? _currentToken;

    public GogAuthService(ISecretStore secretStore, GogOAuthClient oAuthClient, GogPkceService pkceService)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _oAuthClient = oAuthClient ?? throw new ArgumentNullException(nameof(oAuthClient));
        _pkceService = pkceService ?? throw new ArgumentNullException(nameof(pkceService));
    }

    public Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
    {
        var isAuthenticated = _currentToken is { AccessTokenExpiresAtUtc: var expiry } &&
                              expiry > DateTimeOffset.UtcNow;
        var expiresAt = isAuthenticated ? _currentToken?.AccessTokenExpiresAtUtc : null;
        return Task.FromResult(new StoreAuthState(isAuthenticated, expiresAt));
    }

    public Task<StoreAccountInfo> SignInInteractiveAsync(CancellationToken ct = default)
    {
        var pkce = _pkceService.CreateChallenge();
        Debug.WriteLine($"[GOG] PKCE prepared (method={pkce.CodeChallengeMethod}).");

        throw new NotImplementedException("GOG interactive OAuth sign-in is not implemented yet.");
    }

    public async Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
    {
        var refreshToken = await _secretStore.GetAsync(GogRefreshTokenKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(refreshToken))
            return false;

        Debug.WriteLine("[GOG] Refresh token found, refresh flow not implemented yet.");
        return false;
    }

    public async Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default)
    {
        _currentToken = null;

        if (forgetPersistentToken)
            await _secretStore.DeleteAsync(GogRefreshTokenKey, ct).ConfigureAwait(false);
    }
}
