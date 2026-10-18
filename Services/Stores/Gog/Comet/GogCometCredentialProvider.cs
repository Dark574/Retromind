using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Services.Stores.Gog.Auth;

namespace Retromind.Services.Stores.Gog.Comet;

internal sealed class GogCometCredentialProvider
{
    private readonly GogAuthService _authService;

    internal GogCometCredentialProvider(GogAuthService authService)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    internal async Task<CometCredentials?> GetCredentialsAsync(CancellationToken ct = default)
    {
        var session = await _authService
            .GetSessionCredentialsAsync(ct)
            .ConfigureAwait(false);
        if (session == null)
            return null;

        return new CometCredentials(
            session.AccessToken,
            session.RefreshToken,
            session.AccountId,
            session.Username);
    }
}
