using Retromind.Models;
using Retromind.Services;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class ScrapeDialogViewModelTests
{
    [Fact]
    public void ChangingProvider_ClearsResultsFromPreviousProvider()
    {
        var firstProvider = new ScraperConfig { Type = ScraperType.IGDB };
        var secondProvider = new ScraperConfig { Type = ScraperType.TheGamesDB };
        var settings = new AppSettings
        {
            Scrapers = [firstProvider, secondProvider]
        };
        using var httpClient = new HttpClient();
        var metadataService = new MetadataService(settings, httpClient);
        using var viewModel = new ScrapeDialogViewModel(
            new MediaItem("Test Game"),
            settings,
            metadataService);
        var result = new ScraperSearchResult
        {
            Id = "provider-specific-id",
            Title = "Test Game"
        };
        viewModel.SearchResults.Add(result);
        viewModel.SelectedResult = result;

        viewModel.SelectedScraper = secondProvider;

        Assert.Empty(viewModel.SearchResults);
        Assert.Null(viewModel.SelectedResult);
    }

    [Theory]
    [InlineData(true, "https://media.example/gameplay.mp4")]
    [InlineData(false, null)]
    public async Task ApplyCommand_UsesConfiguredVideoSelection(
        bool importVideo,
        string? expectedVideoUrl)
    {
        var settings = new AppSettings
        {
            ScraperImport = new ScraperImportSettings
            {
                ImportVideo = importVideo
            }
        };

        using var httpClient = new HttpClient();
        var metadataService = new MetadataService(settings, httpClient);
        using var viewModel = new ScrapeDialogViewModel(
            new MediaItem("Test Game"),
            settings,
            metadataService);

        viewModel.SelectedResult = new ScraperSearchResult
        {
            Id = "test-result",
            Title = "Test Game",
            VideoUrl = "https://media.example/gameplay.mp4"
        };

        var videoChoice = Assert.Single(
            viewModel.ArtworkChoices,
            choice => choice.Type == AssetType.Video);

        Assert.Equal(importVideo, videoChoice.IsSelected);
        Assert.False(videoChoice.HasImagePreview);
        Assert.Null(videoChoice.ImagePreviewUrl);

        ScraperSearchResult? appliedResult = null;
        viewModel.OnResultSelectedAsync += result =>
        {
            appliedResult = result;
            return Task.CompletedTask;
        };

        await viewModel.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(appliedResult);
        Assert.Equal(expectedVideoUrl, appliedResult.VideoUrl);
    }
}
