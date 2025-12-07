using System;

namespace Retromind.Models;

/// <summary>
/// Represents a unified search result from a metadata provider (Scraper).
/// Acts as an adapter/DTO between external API responses and our internal MediaItem model.
/// </summary>
public class ScraperSearchResult
{
    /// <summary>
    /// The unique ID provided by the source service (e.g., TMDB-ID "550" or IGDB-ID "1337").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The main title of the found media.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// A localized summary or plot description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Original release date (if available).
    /// </summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Normalized rating value (0 to 100). 
    /// Scrapers must convert their internal scale (e.g. 0-10 or 0-5) to this range.
    /// </summary>
    public double? Rating { get; set; }

    // --- Assets (URLs) ---
    // These are strings because we download them later on demand.

    /// <summary>
    /// URL to the front cover image (Boxart / Poster).
    /// </summary>
    public string? CoverUrl { get; set; }

    /// <summary>
    /// URL to the background image (Fanart / Backdrop).
    /// </summary>
    public string? WallpaperUrl { get; set; }

    /// <summary>
    /// URL to the transparent logo image (Clearlogo).
    /// </summary>
    public string? LogoUrl { get; set; }
    
    // --- Additional Metadata ---

    public string? Developer { get; set; }
    public string? Genre { get; set; }
    
    /// <summary>
    /// Name of the provider source (e.g. "TMDB", "IGDB", "Screenscraper").
    /// Useful for debugging or UI badges.
    /// </summary>
    public string Source { get; set; } = "Unknown";
}