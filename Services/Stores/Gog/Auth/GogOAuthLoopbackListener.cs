using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogOAuthLoopbackListener : IDisposable
{
    public Uri RedirectUri { get; }

    public GogOAuthLoopbackListener(Uri redirectUri)
    {
        RedirectUri = redirectUri ?? throw new ArgumentNullException(nameof(redirectUri));
    }

    public Task<OAuthCallbackResult> WaitForCallbackAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException("OAuth loopback callback listener is not implemented yet.");
    }

    public void Dispose()
    {
        // Reserved for listener resources.
    }
}
