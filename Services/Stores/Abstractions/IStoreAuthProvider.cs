using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;
using System;

namespace Retromind.Services.Stores.Abstractions;

public interface IStoreAuthProvider : IStoreProvider
{
    Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default);

    Task<StoreAccountInfo> SignInInteractiveAsync(
        Func<Uri, CancellationToken, Task<Uri?>>? callbackUriResolver = null,
        CancellationToken ct = default);

    Task<bool> TryRefreshSessionAsync(CancellationToken ct = default);

    Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default);
}
