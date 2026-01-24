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
