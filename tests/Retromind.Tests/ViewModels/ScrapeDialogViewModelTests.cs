using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.Scrapers;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class ScrapeDialogViewModelTests
{
    [Fact]
    public async Task ApplyCommand_FailureUsesImportStatusAndRetainsSelectionForRetry()
    {
        var settings = new AppSettings();
        using var client = new HttpClient();
        using var vm = new ScrapeDialogViewModel(new MediaItem("Game"), settings, new MetadataService(settings, client));
        vm.SelectedResult = new ScraperSearchResult
        {
            Title = "Game", Documents = [new ScraperDocument { Url = "https://example.test/manual.pdf" }]
        };
        var choice = Assert.Single(vm.ArtworkChoices);
        choice.IsSelected = true;
        const string details = "No PDF returned.\nOther changes remain available.";
        var fail = true;
        vm.OnResultSelectedAsync += _ => fail ? Task.FromException(new InvalidOperationException(details)) : Task.CompletedTask;

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.ResourceManager.GetString("ScrapeDialog.ApplyFailedFormat", Strings.Culture)!, details), vm.StatusMessage);
        Assert.True(choice.IsSelected);
        Assert.False(vm.IsBusy);
        fail = false;
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("NotPdf")]
    [InlineData("SizeLimitExceeded")]
    [InlineData("IncompleteDownload")]
    public void DocumentFailureDescriptionIsSpecificAndSafe(string reason)
    {
        var error = new ScrapedDocumentDownloadException(reason, 200);
        var generic = MainWindowViewModel.DescribeDocumentImportFailure(new IOException("password=private-secret"));
        var message = MainWindowViewModel.DescribeDocumentImportFailure(error);

        Assert.NotEqual(generic, message);
        Assert.DoesNotContain("private-secret", generic);
        Assert.DoesNotContain("private-secret", MainWindowViewModel.DescribeDocumentImportFailure(
            new HttpRequestException("password=private-secret", null, System.Net.HttpStatusCode.Forbidden)));
    }

    [Fact]
    public async Task ApplyCommand_SelectsDocumentsIndividuallyWithoutRequestingImagePreviews()
    {
        var settings = new AppSettings();
        Assert.False(settings.ScraperImport.ImportManuals);
        using var client = new HttpClient();
        using var vm = new ScrapeDialogViewModel(new MediaItem("Game"), settings, new MetadataService(settings, client));
        var manual = new ScraperDocument { Url = "https://example.test/manual.php", Region = "eu", Number = 1 };
        var keychart = new ScraperDocument { Url = "https://example.test/keys.pdf", Title = "Keychart", Region = "eu", Number = 2 };
        vm.SelectedResult = new ScraperSearchResult { Title = "Game", Documents = [manual, keychart] };

        Assert.Equal(2, vm.ArtworkChoices.Count);
        Assert.All(vm.ArtworkChoices, choice =>
        {
            Assert.False(choice.HasImagePreview);
            Assert.Null(choice.ImagePreviewUrl);
            Assert.False(choice.IsSelected);
            Assert.Contains("PDF", choice.Label);
            Assert.Contains("EU", choice.Label);
        });
        vm.ArtworkChoices[1].IsSelected = true;
        ScraperSearchResult? applied = null;
        vm.OnResultSelectedAsync += result => { applied = result; return Task.CompletedTask; };

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(applied);
        Assert.Same(keychart, Assert.Single(applied.Documents));
        Assert.Null(applied.CoverUrl);
    }

    [Fact]
    public void ExistingManualDoesNotSuppressAdditionalDocumentsOrConfiguredSelection()
    {
        var settings = new AppSettings { ScraperImport = new ScraperImportSettings { ImportManuals = true } };
        var item = new MediaItem("Game");
        item.Assets.Add(new MediaAsset { Type = AssetType.Manual, RelativePath = "existing.pdf" });
        using var client = new HttpClient();
        using var vm = new ScrapeDialogViewModel(item, settings, new MetadataService(settings, client));
        vm.SelectedResult = new ScraperSearchResult
        {
            Title = "Game",
            Documents = [new ScraperDocument { Title = "Map", Url = "https://example.test/map.pdf" }]
        };

        Assert.True(Assert.Single(vm.ArtworkChoices).IsSelected);
        vm.ClearArtworkSelectionCommand.Execute(null);
        Assert.False(vm.ArtworkChoices[0].IsSelected);
        vm.SelectAllArtworkCommand.Execute(null);
        Assert.True(vm.ArtworkChoices[0].IsSelected);
        vm.SelectedResult = new ScraperSearchResult { Title = "Other" };
        Assert.Empty(vm.ArtworkChoices);
    }

    [Fact]
    public async Task ApplyCommand_PreservesAlreadyDecodedTextWithoutDecodingAgain()
    {
        var settings = new AppSettings();
        using var client = new HttpClient();
        var existing = new MediaItem("Existing game") { Description = "Existing &quot;description&quot;" };
        using var vm = new ScrapeDialogViewModel(existing, settings, new MetadataService(settings, client));
        vm.SelectedResult = new ScraperSearchResult
        {
            Title = "Literal &quot; and normal \"quotes\"",
            Description = "Already decoded <3 &lt;tag&gt;"
        };
        var descriptionChoice = Assert.Single(vm.MetadataChoices, choice => choice.Field == ScrapeMetadataField.Description);
        descriptionChoice.IsSelected = true;
        ScraperSearchResult? applied = null;
        vm.OnResultSelectedAsync += result => { applied = result; return Task.CompletedTask; };

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.NotNull(applied);
        Assert.Equal(vm.SelectedResult.Title, applied.Title);
        Assert.Equal("Already decoded <3 &lt;tag&gt;", applied.Description);
        Assert.Equal("Existing &quot;description&quot;", existing.Description);
    }

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
