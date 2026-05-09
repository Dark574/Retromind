using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Security;

/// <summary>
/// Session-only fallback store. Data is not persisted to disk.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(secret);

        _secrets[BuildCompositeKey(key)] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Task.FromResult(_secrets.TryGetValue(BuildCompositeKey(key), out var value) ? value : null);
    }

    public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        _secrets.TryRemove(BuildCompositeKey(key), out _);
        return Task.CompletedTask;
    }

    private static string BuildCompositeKey(SecretKey key)
    {
        return $"{key.Service}::{key.Account}";
    }
}
