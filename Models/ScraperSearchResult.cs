using System;

namespace Retromind.Models;

/// <summary>
/// Represents a unified search result from any metadata provider.
/// Acts as an adapter between different API responses and our internal model.
/// </summary>
public class ScraperSearchResult
{
    /// <summary>
    /// The unique ID provided by the source service (e.g., TMDB-ID or IGDB-ID).
    /// </summary>
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    /// <summary>
    /// Rating value (normalized logic depends on scraper, mostly 0-100 or 0-10).
    /// </summary>
    public double? Rating { get; set; }

    // --- Assets (URLs to be downloaded later) ---
    public string? CoverUrl { get; set; }
    public string? WallpaperUrl { get; set; }
    public string? LogoUrl { get; set; }
    
    // --- Additional Metadata ---
    public string? Developer { get; set; }
    public string? Genre { get; set; }
    
    /// <summary>
    /// Name of the provider source (e.g. "TMDB", "IGDB").
    /// </summary>
    public string Source { get; set; } = "Unknown";
}