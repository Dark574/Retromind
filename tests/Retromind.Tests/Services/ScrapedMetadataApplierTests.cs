using Retromind.Models;
using Retromind.Services.Scrapers;

namespace Retromind.Tests.Services;

public sealed class ScrapedMetadataApplierTests
{
    [Theory]
    [InlineData(ScraperExistingDataMode.OnlyMissing, "Original developer")]
    [InlineData(ScraperExistingDataMode.OverwriteAlways, "Scraped developer")]
    public void Apply_RespectsConflictModeAndFieldSelection(
        ScraperExistingDataMode mode, string expectedDeveloper)
    {
        var item = new MediaItem
        {
            Developer = "Original developer",
            Description = "Original description"
        };
        var result = new ScraperSearchResult
        {
            Developer = "  Scraped developer  ",
            Description = "Scraped description",
            Genre = " Adventure ",
            ReleaseDate = new DateTime(1995, 7, 12, 16, 30, 0),
            Rating = 110
        };
        var settings = new ScraperImportSettings { ExistingDataMode = mode, ImportDescription = false };

        Assert.True(ScrapedMetadataApplier.Apply(item, result, settings));
        Assert.Equal(expectedDeveloper, item.Developer);
        Assert.Equal("Original description", item.Description);
        Assert.Equal("Adventure", item.Genre);
        Assert.Equal(new DateTime(1995, 7, 12), item.ReleaseDate);
        Assert.Equal(100d, item.Rating);
        Assert.False(ScrapedMetadataApplier.Apply(item, result, settings));
    }

    [Fact]
    public void Apply_OnlyTagsTheRetainedPlatformWithoutDuplicates()
    {
        var item = new MediaItem { Platform = "Amiga" };
        var settings = new ScraperImportSettings { ImportSource = false };

        Assert.False(ScrapedMetadataApplier.Apply(
            item, new ScraperSearchResult { Platform = "DOS" }, settings));
        Assert.Equal("Amiga", item.Platform);
        Assert.Empty(item.Tags);

        Assert.True(ScrapedMetadataApplier.Apply(
            item, new ScraperSearchResult { Platform = " amiga " }, settings));
        Assert.False(ScrapedMetadataApplier.Apply(
            item, new ScraperSearchResult { Platform = "AMIGA" }, settings));
        Assert.Equal("amiga", Assert.Single(item.Tags));
    }

    [Theory]
    [InlineData(ScraperExistingDataMode.OnlyMissing, "Original")]
    [InlineData(ScraperExistingDataMode.OverwriteAlways, "Updated")]
    public void Apply_MergesCustomFieldsWithoutClearingUnrelatedValues(
        ScraperExistingDataMode mode, string expectedValue)
    {
        var item = new MediaItem
        {
            CustomFields = new() { ["Existing"] = "Original", ["Local"] = "Keep" }
        };
        var result = new ScraperSearchResult
        {
            CustomFields = new()
            {
                [" Existing "] = " Updated ",
                [" New "] = " Added ",
                ["Local"] = " ",
                [" "] = "Ignored"
            }
        };
        var settings = new ScraperImportSettings { ExistingDataMode = mode };

        Assert.True(ScrapedMetadataApplier.Apply(item, result, settings));
        Assert.Equal(expectedValue, item.CustomFields["Existing"]);
        Assert.Equal("Keep", item.CustomFields["Local"]);
        Assert.Equal("Added", item.CustomFields["New"]);
        Assert.Equal(3, item.CustomFields.Count);
    }
}
