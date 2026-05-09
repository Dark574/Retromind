using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Abstractions;

public interface IStoreInstallDiscoveryProvider : IStoreProvider
{
    Task<IReadOnlyList<StoreInstallRecord>> DiscoverInstallationsAsync(CancellationToken ct = default);
}
