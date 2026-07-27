using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Interface for all metadata providers (scrapers) like TMDB, IGDB, etc.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Initializes the provider (e.g. performs authentication/login).
    /// Should be called before SearchAsync.
    /// </summary>
    /// <returns>True if connection/authentication was successful.</returns>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for media based on a text query.
    /// </summary>
    /// <param name="query">The search term (title, keyword).</param>
    /// <returns>A list of standardized search results.</returns>
    Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for providers whose search endpoint returns lightweight
/// results and whose complete metadata or artwork requires additional requests.
/// </summary>
public interface IMetadataResultEnricher
{
    /// <summary>
    /// Loads the remaining provider data for a selected search result.
    /// Implementations should leave already populated fields unchanged.
    /// </summary>
    Task EnrichAsync(ScraperSearchResult result, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for providers that can populate lightweight artwork
/// previews without fully enriching every search result.
/// </summary>
public interface IMetadataSearchPreviewEnricher
{
    /// <summary>
    /// Populates preview data for the results displayed in the manual search dialog.
    /// </summary>
    Task EnrichPreviewsAsync(
        IReadOnlyList<ScraperSearchResult> results,
        CancellationToken cancellationToken = default);
}
