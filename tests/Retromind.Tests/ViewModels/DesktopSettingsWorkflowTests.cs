using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Achievements;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using ControllableSecretStore = Retromind.Tests.Services.Stores.Security.SecretStoreSafetyTests.ControllableSecretStore;

namespace Retromind.Tests.ViewModels;

public sealed class DesktopSettingsWorkflowTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task CompleteAsync_UsesActualSettingsWriteAndRespectsRestore(bool settingsSaved, bool requiresRestart)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            ("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        using var httpClient = new HttpClient(new UnusedHandler());
        using var runnerService = new RunnerVersionService(temp.RootPath);
        var settings = new AppSettings
        {
            UsePortableHomeInAppImage = false,
            RetroAchievements = new RetroAchievementsSettings { Enabled = true, Username = "OldUser" },
            Scrapers = [new ScraperConfig { Id = "profile-1", Type = ScraperType.IGDB, ApiKey = "old-scraper-key" }]
        };
        var settingsService = new SettingsService();
        await settingsService.SaveAsync(settings);
        var secrets = new ControllableSecretStore();
        var account = new RetroAchievementsAccountService(new RetroAchievementsApiClient(httpClient), secrets);
        await account.StoreApiKeyAsync("old-achievement-key");
        var scraperKey = new SecretKey("retromind:scraper", "profile-1:api-key");
        await secrets.SetAsync(scraperKey, "old-scraper-key");
        var retroCachePaths = new RetroAchievementsCachePathProvider(settings);
        var storeCachePaths = new StoreAchievementCachePathProvider(settings);
        var retroCacheFile = temp.CreateFile("Cache/RetroAchievements/catalog.json", "{}");
        var storeCacheFile = temp.CreateFile("Cache/StoreAchievements/Steam/Progress/progress.json", "{}");
        var workflow = new DesktopSettingsWorkflow(
            settings, settingsService, account, new SteamAchievementsAccountService(new SteamApiClient(httpClient), secrets),
            new SteamAccountDiscoveryService([]), runnerService, new ScraperCredentialService(secrets),
            new LibraryHealthService(), retroCachePaths, storeCachePaths);
        using var viewModel = await workflow.CreateViewModelAsync([], gamepadService: null, initialTabIndex: 2);
        Assert.Equal(2, viewModel.SelectedSettingsTabIndex);
        Assert.True(viewModel.HasStoredRetroAchievementsApiKey);
        viewModel.RetroAchievementsApiKey = "new-achievement-key";
        Assert.Single(viewModel.Scrapers).ApiKey = "new-scraper-key";
        viewModel.UsePortableHomeInAppImage = true;
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsSaved);

        var operations = new List<string>();
        await workflow.CompleteAsync(
            viewModel, portableHomeWasEnabled: false, requiresRestart,
            markLibraryDirty: () => operations.Add("dirty"),
            saveData: async () =>
            {
                operations.Add("save");
                if (settingsSaved)
                    await settingsService.SaveAsync(settings);
                return (AllSaved: false, SettingsSaved: settingsSaved);
            },
            settingsAccepted: () => operations.Add("accepted"),
            refreshAchievements: forceRefresh =>
            {
                Assert.True(forceRefresh);
                operations.Add("refresh");
                return Task.CompletedTask;
            });

        if (settingsSaved)
        {
            // Finalized transactions no longer roll back when asked to restore the old secrets.
            Assert.True(await viewModel.RollbackAchievementChangesAsync());
            Assert.True(await viewModel.RollbackScraperCredentialChangesAsync());
        }

        var keepNewSecrets = settingsSaved || requiresRestart;
        Assert.Equal(keepNewSecrets ? "new-achievement-key" : "old-achievement-key", await account.GetApiKeyAsync());
        Assert.Equal(keepNewSecrets ? "new-scraper-key" : "old-scraper-key", await secrets.GetAsync(scraperKey));
        Assert.Equal(keepNewSecrets ? "new-scraper-key" : "old-scraper-key", Assert.Single(settings.Scrapers).ApiKey);
        Assert.Equal(settingsSaved, (await settingsService.LoadAsync()).UsePortableHomeInAppImage);
        Assert.Equal(!settingsSaved, File.Exists(retroCacheFile));
        Assert.Equal(!settingsSaved, File.Exists(storeCacheFile));
        Assert.True(File.Exists(Path.Combine(retroCachePaths.GetCacheDirectory(), "catalog.json")));
        Assert.True(File.Exists(Path.Combine(storeCachePaths.GetSteamProgressCacheDirectory(), "progress.json")));
        Assert.Equal(requiresRestart ? [] : settingsSaved ? ["accepted", "save", "refresh"] : ["accepted", "save"],
            operations);
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP request is expected in this test.");
    }
}
