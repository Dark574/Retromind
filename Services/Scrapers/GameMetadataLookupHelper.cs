using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Applies the shared exact-file-first lookup policy used by manual and bulk
/// metadata searches without making generic scrapers depend on game systems.
/// </summary>
internal static class GameMetadataLookupHelper
{
    public static async Task<GameMetadataLookupResult> SearchAsync(
        IMetadataProvider provider,
        string query,
        string? gameSystemId,
        string? gameFilePath,
        CancellationToken cancellationToken = default,
        bool useBulkSearch = false)
    {
        if (provider is IGameFileMetadataProvider fileProvider &&
            !string.IsNullOrWhiteSpace(gameSystemId) &&
            !string.IsNullOrWhiteSpace(gameFilePath) &&
            File.Exists(gameFilePath) &&
            fileProvider.SupportsGameSystem(gameSystemId))
        {
            var identified = await fileProvider
                .IdentifyGameFileAsync(gameSystemId, gameFilePath, cancellationToken)
                .ConfigureAwait(false);
            if (identified != null)
            {
                return new GameMetadataLookupResult(
                    new List<ScraperSearchResult> { identified },
                    IsExactMatch: true);
            }
        }

        List<ScraperSearchResult> results;
        if (provider is IGameSystemMetadataProvider systemProvider &&
            !string.IsNullOrWhiteSpace(gameSystemId) &&
            systemProvider.SupportsGameSystem(gameSystemId))
        {
            results = await systemProvider
                .SearchByGameSystemAsync(query, gameSystemId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (useBulkSearch && provider is IBulkMetadataProvider bulkProvider)
        {
            results = await bulkProvider
                .SearchForBulkAsync(query, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            results = await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }

        return new GameMetadataLookupResult(results, IsExactMatch: false);
    }
}

internal readonly record struct GameMetadataLookupResult(
    List<ScraperSearchResult> Results,
    bool IsExactMatch);
