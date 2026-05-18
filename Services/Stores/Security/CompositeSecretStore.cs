using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Security;

/// <summary>
/// Uses Secret Service when available, otherwise falls back to a session-only in-memory store.
/// </summary>
public sealed class CompositeSecretStore : ISecretStore
{
    private readonly ISecretStore _primary;
    private readonly ISecretStore _fallback;

    public CompositeSecretStore(ISecretStore primary, ISecretStore fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return await IsPrimaryAvailableAsync(ct).ConfigureAwait(false) ||
               await _fallback.IsAvailableAsync(ct).ConfigureAwait(false);
    }

    public async Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
    {
        if (await IsPrimaryAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await _primary.SetAsync(key, secret, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SecretStore] Primary set failed. Using fallback store. Reason: {ex.Message}");
            }
        }

        await _fallback.SetAsync(key, secret, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        if (await IsPrimaryAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var value = await _primary.GetAsync(key, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SecretStore] Primary get failed. Trying fallback store. Reason: {ex.Message}");
            }
        }

        return await _fallback.GetAsync(key, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        if (await IsPrimaryAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await _primary.DeleteAsync(key, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SecretStore] Primary delete failed. Continuing with fallback store. Reason: {ex.Message}");
            }
        }

        await _fallback.DeleteAsync(key, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsPrimaryAvailableAsync(CancellationToken ct)
    {
        try
        {
            return await _primary.IsAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SecretStore] Primary availability check failed: {ex.Message}");
            return false;
        }
    }
}
