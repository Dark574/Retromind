using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Gog;

/// <summary>
/// Placeholder for the planned discovery of existing local GOG installations.
/// The provider must not advertise the InstallDiscovery capability until this
/// service returns real discovery results.
/// </summary>
public sealed class GogInstallDiscoveryService
{
    public Task<IReadOnlyList<StoreInstallRecord>> DiscoverInstallationsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<StoreInstallRecord> empty = [];
        return Task.FromResult(empty);
    }
}
