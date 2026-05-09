using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Gog;

public sealed class GogLibraryService
{
    public Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<StoreGameRecord> empty = [];
        return Task.FromResult(empty);
    }
}
