using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Services.Stores.Epic;

internal sealed class EpicLibraryService
{
    private readonly LegendaryCliClient _client;

    public EpicLibraryService(LegendaryCliClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(
        CancellationToken ct = default)
    {
        var games = await _client.GetGamesAsync(ct).ConfigureAwait(false);
        return games
            .Select(game => new StoreGameRecord(
                StoreProviderIds.Epic,
                game.AppName,
                game.AppTitle,
                game.Platform,
                game.Version))
            .ToArray();
    }
}
