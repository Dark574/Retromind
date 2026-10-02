using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class MetadataEnrichmentRequestTests
{
    [Fact]
    public void ForBulk_OnlyMissing_ExcludesDisabledAndExistingData()
    {
        var item = new MediaItem
        {
            Developer = "Existing developer"
        };
        item.Assets.Add(new MediaAsset { Type = AssetType.Cover, RelativePath = "cover.jpg" });
        var settings = DisabledSettings();
        settings.ImportDeveloper = true;
        settings.ImportGenre = true;
        settings.ImportCover = true;
        settings.ImportLogo = true;

        var request = MetadataEnrichmentRequest.ForBulk(item, settings);

        Assert.False(request.Developer);
        Assert.True(request.Genre);
        Assert.False(request.Cover);
        Assert.True(request.Logo);
    }

    [Fact]
    public void ForBulk_OverwriteAndAppend_IncludesExistingData()
    {
        var item = new MediaItem
        {
            Developer = "Existing developer"
        };
        item.Assets.Add(new MediaAsset { Type = AssetType.Cover, RelativePath = "cover.jpg" });
        var settings = DisabledSettings();
        settings.ExistingDataMode = ScraperExistingDataMode.OverwriteAlways;
        settings.AppendAssetsDuringBulkScrape = true;
        settings.ImportDeveloper = true;
        settings.ImportCover = true;

        var request = MetadataEnrichmentRequest.ForBulk(item, settings);

        Assert.True(request.Developer);
        Assert.True(request.Cover);
    }

    private static ScraperImportSettings DisabledSettings() => new()
    {
        ImportDescription = false,
        ImportReleaseDate = false,
        ImportRating = false,
        ImportDeveloper = false,
        ImportGenre = false,
        ImportPlatform = false,
        ImportPublisher = false,
        ImportSeries = false,
        ImportReleaseType = false,
        ImportSortTitle = false,
        ImportPlayMode = false,
        ImportMaxPlayers = false,
        ImportSource = false,
        ImportCustomFields = false,
        ImportCover = false,
        ImportWallpaper = false,
        ImportScreenshot = false,
        ImportLogo = false,
        ImportMarquee = false,
        ImportBezel = false,
        ImportControlPanel = false
    };
}
