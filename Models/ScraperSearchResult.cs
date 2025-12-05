using System;

namespace Retromind.Models;

/// <summary>
/// Ein einheitliches Suchergebnis, egal von welchem Anbieter.
/// </summary>
public class ScraperSearchResult
{
    public string Id { get; set; } = string.Empty; // Die ID beim Anbieter (z.B. "12345")
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public double? Rating { get; set; } // 0-100

    // URLs zu den Bildern (noch nicht heruntergeladen)
    public string? CoverUrl { get; set; }
    public string? WallpaperUrl { get; set; }
    public string? Developer { get; set; }
    public string? Genre { get; set; }
    public string? LogoUrl { get; set; }
    
    public string Source { get; set; } = "Unknown"; // z.B. "IGDB"
}