using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Abstractions;

public interface IStoreLibraryProvider : IStoreProvider
{
    Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(CancellationToken ct = default);
}
