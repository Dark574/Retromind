using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Abstractions;

public interface IStoreAuthProvider : IStoreProvider
{
    Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default);

    Task<StoreAccountInfo> SignInInteractiveAsync(CancellationToken ct = default);

    Task<bool> TryRefreshSessionAsync(CancellationToken ct = default);

    Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default);
}
