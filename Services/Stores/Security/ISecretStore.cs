using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Security;

public interface ISecretStore
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task SetAsync(SecretKey key, string secret, CancellationToken ct = default);

    /// <summary>Returns the write outcome. Cancellation still propagates; Unknown makes no durability promise.</summary>
    async Task<SecretStorageStatus> SetWithStatusAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await SetAsync(key, secret, ct).ConfigureAwait(false);
            return GetStorageStatus(key);
        }
        catch (OperationCanceledException) { throw; }
        catch { return SecretStorageStatus.Failed; }
    }

    /// <summary>
    /// Last observed durability, not an availability probe or a guarantee that a value exists.
    /// Implementations without status support make no promise unless they implement IPersistentSecretStore.
    /// </summary>
    SecretStorageStatus GetStorageStatus(SecretKey key)
        => this is IPersistentSecretStore ? SecretStorageStatus.Persistent : SecretStorageStatus.Unknown;

    Task<string?> GetAsync(SecretKey key, CancellationToken ct = default);

    Task DeleteAsync(SecretKey key, CancellationToken ct = default);
}
