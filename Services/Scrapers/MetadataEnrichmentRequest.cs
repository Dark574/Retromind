using System;
using System.Linq;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Describes supplemental data that a caller can actually use. Providers may
/// use this to avoid optional detail requests for disabled or existing data.
/// </summary>
public sealed record MetadataEnrichmentRequest
{
    public static MetadataEnrichmentRequest All { get; } = new()
    {
        Developer = true,
        Genre = true,
        Platform = true,
        Publisher = true,
        Cover = true,
        Wallpaper = true,
        Screenshot = true,
        Logo = true,
        Marquee = true,
        Bezel = true,
        ControlPanel = true
    };

    public bool Developer { get; init; }
    public bool Genre { get; init; }
    public bool Platform { get; init; }
    public bool Publisher { get; init; }

    public bool Cover { get; init; }
    public bool Wallpaper { get; init; }
    public bool Screenshot { get; init; }
    public bool Logo { get; init; }
    public bool Marquee { get; init; }
    public bool Bezel { get; init; }
    public bool ControlPanel { get; init; }

    public bool HasAnyMetadata => Developer || Genre || Platform || Publisher;
    public bool HasAnyArtwork => Cover || Wallpaper || Screenshot || Logo || Marquee || Bezel || ControlPanel;
    public bool HasAny => HasAnyMetadata || HasAnyArtwork;

    public static MetadataEnrichmentRequest ForBulk(
        MediaItem item,
        ScraperImportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        var overwriteExisting = settings.ExistingDataMode == ScraperExistingDataMode.OverwriteAlways;
        var existingArtwork = item.Assets.Select(asset => asset.Type).ToHashSet();

        bool NeedsMetadata(bool enabled, string? value) =>
            enabled && (overwriteExisting || string.IsNullOrWhiteSpace(value));

        bool NeedsArtwork(bool enabled, AssetType type) =>
            enabled && (settings.AppendAssetsDuringBulkScrape || !existingArtwork.Contains(type));

        return new MetadataEnrichmentRequest
        {
            Developer = NeedsMetadata(settings.ImportDeveloper, item.Developer),
            Genre = NeedsMetadata(settings.ImportGenre, item.Genre),
            Platform = NeedsMetadata(settings.ImportPlatform, item.Platform),
            Publisher = NeedsMetadata(settings.ImportPublisher, item.Publisher),
            Cover = NeedsArtwork(settings.ImportCover, AssetType.Cover),
            Wallpaper = NeedsArtwork(settings.ImportWallpaper, AssetType.Wallpaper),
            Screenshot = NeedsArtwork(settings.ImportScreenshot, AssetType.Screenshot),
            Logo = NeedsArtwork(settings.ImportLogo, AssetType.Logo),
            Marquee = NeedsArtwork(settings.ImportMarquee, AssetType.Marquee),
            Bezel = NeedsArtwork(settings.ImportBezel, AssetType.Bezel),
            ControlPanel = NeedsArtwork(settings.ImportControlPanel, AssetType.ControlPanel)
        };
    }
}
