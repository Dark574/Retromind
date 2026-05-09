using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Security;

public interface ISecretStore
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task SetAsync(SecretKey key, string secret, CancellationToken ct = default);

    Task<string?> GetAsync(SecretKey key, CancellationToken ct = default);

    Task DeleteAsync(SecretKey key, CancellationToken ct = default);
}
