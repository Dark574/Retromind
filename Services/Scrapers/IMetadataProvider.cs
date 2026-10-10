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
/// Optional capability for providers that can use a less expensive search
/// shape when results are selected automatically during bulk scraping.
/// </summary>
public interface IBulkMetadataProvider
{
    Task<List<ScraperSearchResult>> SearchForBulkAsync(
        string query,
        CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Loads only supplemental data the caller intends to use. Providers that
    /// do not support selective enrichment retain their existing all-or-nothing
    /// behavior, but can still skip enrichment when nothing was requested.
    /// </summary>
    Task EnrichAsync(
        ScraperSearchResult result,
        MetadataEnrichmentRequest request,
        CancellationToken cancellationToken = default) =>
        request.HasAny
            ? EnrichAsync(result, cancellationToken)
            : Task.CompletedTask;
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

/// <summary>
/// Optional capability for game metadata providers that can constrain title
/// searches to a provider-neutral game-system assignment.
/// </summary>
public interface IGameSystemMetadataProvider
{
    bool SupportsGameSystem(string? gameSystemId);

    Task<List<ScraperSearchResult>> SearchByGameSystemAsync(
        string query,
        string gameSystemId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for providers that use a reduced system-constrained
/// search shape during bulk scraping.
/// </summary>
public interface IBulkGameSystemMetadataProvider : IGameSystemMetadataProvider
{
    Task<List<ScraperSearchResult>> SearchForBulkByGameSystemAsync(
        string query,
        string gameSystemId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability for game metadata providers that can identify a ROM
/// or disc image from its provider-neutral system assignment and file data.
/// </summary>
public interface IGameFileMetadataProvider : IGameSystemMetadataProvider
{
    Task<ScraperSearchResult?> IdentifyGameFileAsync(
        string gameSystemId,
        string filePath,
        CancellationToken cancellationToken = default);
}
