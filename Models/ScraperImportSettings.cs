using System.Text.Json.Serialization;

namespace Retromind.Models;

/// <summary>
/// Controls which scraper fields/assets are applied and how conflicts
/// with existing item data are handled.
/// </summary>
public class ScraperImportSettings
{
    // Default conflict policy for metadata. Manual scraping presents an
    // explicit per-field selection; bulk scraping applies this mode directly.
    public ScraperExistingDataMode ExistingDataMode { get; set; } = ScraperExistingDataMode.OnlyMissing;

    // Metadata fields
    public bool ImportDescription { get; set; } = true;
    public bool ImportReleaseDate { get; set; } = true;
    public bool ImportRating { get; set; } = true;
    public bool ImportDeveloper { get; set; } = true;
    public bool ImportGenre { get; set; } = true;
    public bool ImportPlatform { get; set; } = true;
    public bool ImportPublisher { get; set; } = true;
    public bool ImportSeries { get; set; } = true;
    public bool ImportReleaseType { get; set; } = true;
    public bool ImportSortTitle { get; set; } = true;
    public bool ImportPlayMode { get; set; } = true;
    public bool ImportMaxPlayers { get; set; } = true;
    public bool ImportSource { get; set; } = true;
    public bool ImportCustomFields { get; set; } = true;

    // Artwork / assets
    public bool ImportCover { get; set; } = true;
    public bool ImportWallpaper { get; set; } = true;
    public bool ImportScreenshot { get; set; } = true;
    public bool ImportLogo { get; set; } = true;
    public bool ImportMarquee { get; set; } = true;
    public bool ImportBezel { get; set; } = true;
    public bool ImportControlPanel { get; set; } = true;

    /// <summary>
    /// Default behavior for an artwork type that already exists:
    /// false = skip it, true = add the downloaded image as another variant.
    /// Existing artwork is never removed or overwritten, if you want to get rid of artwork you
    /// can delete it in the asset settings for the item.
    /// Uses legacy JSON key for backward compatibility.
    /// </summary>
    [JsonPropertyName("AskAssetConflictsDuringBulkScrape")]
    public bool AppendAssetsDuringBulkScrape { get; set; } = false;
}
