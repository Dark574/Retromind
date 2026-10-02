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
}
