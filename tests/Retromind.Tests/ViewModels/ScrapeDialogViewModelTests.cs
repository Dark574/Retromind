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

    [Fact]
    public void ScreenScraper_SelectsResolvedGameSystemAsDefaultFilter()
    {
        var screenScraper = new ScraperConfig
        {
            Type = ScraperType.ScreenScraper
        };
        var settings = new AppSettings
        {
            Scrapers = [screenScraper]
        };

        using var httpClient = new HttpClient();
        var metadataService = new MetadataService(settings, httpClient);
        using var viewModel = new ScrapeDialogViewModel(
            new MediaItem("Super Mario World"),
            settings,
            metadataService,
            gameSystemId: "nintendo.snes");

        Assert.True(viewModel.IsGameSystemFilterAvailable);
        Assert.Equal("nintendo.snes", viewModel.SelectedGameSystem?.Id);
        Assert.Contains(
            viewModel.AvailableGameSystems,
            option => option.Id == null);
    }

    [Fact]
    public void ProviderWithoutGameSystemSupport_HidesSystemFilter()
    {
        var screenScraper = new ScraperConfig
        {
            Type = ScraperType.ScreenScraper
        };
        var steamGridDb = new ScraperConfig
        {
            Type = ScraperType.SteamGridDB
        };
        var settings = new AppSettings
        {
            Scrapers = [screenScraper, steamGridDb]
        };

        using var httpClient = new HttpClient();
        var metadataService = new MetadataService(settings, httpClient);
        using var viewModel = new ScrapeDialogViewModel(
            new MediaItem("Test Game"),
            settings,
            metadataService,
            gameSystemId: "nintendo.snes");

        Assert.True(viewModel.IsGameSystemFilterAvailable);

        viewModel.SelectedScraper = steamGridDb;

        Assert.False(viewModel.IsGameSystemFilterAvailable);
        Assert.Empty(viewModel.AvailableGameSystems);
        Assert.Null(viewModel.SelectedGameSystem);
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
