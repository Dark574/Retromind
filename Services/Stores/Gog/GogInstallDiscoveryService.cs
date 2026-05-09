using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Gog;

public sealed class GogInstallDiscoveryService
{
    public Task<IReadOnlyList<StoreInstallRecord>> DiscoverInstallationsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<StoreInstallRecord> empty = [];
        return Task.FromResult(empty);
    }
}
