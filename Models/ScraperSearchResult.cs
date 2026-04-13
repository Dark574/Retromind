using System;
using System.Collections.Generic;
using System.Linq;

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
    /// URL to a screenshot image (ingame/scene capture).
    /// </summary>
    public string? ScreenshotUrl { get; set; }

    /// <summary>
    /// URL to the transparent logo image (Clearlogo).
    /// </summary>
    public string? LogoUrl { get; set; }
    
    /// <summary>
    /// URL to the marquee artwork (typically a wide cabinet header graphic).
    /// </summary>
    public string? MarqueeUrl { get; set; }

    /// <summary>
    /// URL to the bezel artwork (decorative frame around the game screen).
    /// </summary>
    public string? BezelUrl { get; set; }

    /// <summary>
    /// URL to the control panel artwork (joysticks/buttons layout).
    /// </summary>
    public string? ControlPanelUrl { get; set; }
    
    // --- Additional Metadata ---

    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? Genre { get; set; }
    public string? Series { get; set; }
    public string? ReleaseType { get; set; }
    public string? SortTitle { get; set; }
    public string? PlayMode { get; set; }
    public string? MaxPlayers { get; set; }
    
    /// <summary>
    /// Platform / system name (e.g. "Amiga", "PC (Windows)", "SNES").
    /// For sources that support multiple platforms, this may be a
    /// comma-separated list.
    /// </summary>
    public string? Platform { get; set; }
    
    /// <summary>
    /// Name of the provider source (e.g. "TMDB", "IGDB", "Screenscraper").
    /// Useful for debugging or UI badges.
    /// </summary>
    public string Source { get; set; } = "Unknown";

    /// <summary>
    /// Optional provider-specific metadata that has no dedicated first-class field.
    /// </summary>
    public Dictionary<string, string> CustomFields { get; set; } = new(StringComparer.Ordinal);

    public bool HasCustomFields => CustomFields.Count > 0;

    public IEnumerable<KeyValuePair<string, string>> VisibleCustomFields =>
        CustomFields
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase);
}
