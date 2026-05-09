using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Security;

/// <summary>
/// Placeholder for Linux Secret Service integration (org.freedesktop.secrets).
/// This skeleton intentionally reports unavailable until the concrete D-Bus implementation is added.
/// </summary>
public sealed class SecretServiceSecretStore : ISecretStore
{
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Secret Service store is not implemented yet.");
    }

    public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Secret Service store is not implemented yet.");
    }

    public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        throw new InvalidOperationException("Secret Service store is not implemented yet.");
    }
}
