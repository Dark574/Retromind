using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.Achievements;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;
using Retromind.Views;

namespace Retromind.ViewModels;

public sealed class DesktopSettingsWorkflow
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly RetroAchievementsAccountService _retroAchievementsAccountService;
    private readonly SteamAchievementsAccountService _steamAchievementsAccountService;
    private readonly SteamAccountDiscoveryService _steamAccountDiscoveryService;
    private readonly RunnerVersionService _runnerVersionService;
    private readonly ScraperCredentialService _scraperCredentialService;
    private readonly LibraryHealthService _libraryHealthService;
    private readonly RetroAchievementsCachePathProvider _retroAchievementsCachePathProvider;
    private readonly StoreAchievementCachePathProvider _storeAchievementCachePathProvider;

    public DesktopSettingsWorkflow(
        AppSettings settings,
        SettingsService settingsService,
        RetroAchievementsAccountService retroAchievementsAccountService,
        SteamAchievementsAccountService steamAchievementsAccountService,
        SteamAccountDiscoveryService steamAccountDiscoveryService,
        RunnerVersionService runnerVersionService,
        ScraperCredentialService scraperCredentialService,
        LibraryHealthService libraryHealthService,
        RetroAchievementsCachePathProvider retroAchievementsCachePathProvider,
        StoreAchievementCachePathProvider storeAchievementCachePathProvider)
    {
        _settings = settings;
        _settingsService = settingsService;
        _retroAchievementsAccountService = retroAchievementsAccountService;
        _steamAchievementsAccountService = steamAchievementsAccountService;
        _steamAccountDiscoveryService = steamAccountDiscoveryService;
        _runnerVersionService = runnerVersionService;
        _scraperCredentialService = scraperCredentialService;
        _libraryHealthService = libraryHealthService;
        _retroAchievementsCachePathProvider = retroAchievementsCachePathProvider;
        _storeAchievementCachePathProvider = storeAchievementCachePathProvider;
    }

    internal async Task<SettingsViewModel> CreateViewModelAsync(
        ObservableCollection<MediaNode> rootItems,
        GamepadService? gamepadService,
        int? initialTabIndex)
    {
        var settingsVm = new SettingsViewModel(
            _settings,
            _settingsService,
            _retroAchievementsAccountService,
            _steamAchievementsAccountService,
            _steamAccountDiscoveryService,
            _runnerVersionService,
            rootItems,
            gamepadService,
            _scraperCredentialService);
        if (initialTabIndex.HasValue)
            settingsVm.SelectedSettingsTabIndex = initialTabIndex.Value;

        await settingsVm.InitializeRetroAchievementsAsync();
        await settingsVm.InitializeSteamAchievementsAsync();

        return settingsVm;
    }

    internal async Task<(SettingsViewModel ViewModel, SettingsView Dialog)> CreateDialogAsync(
        Func<ObservableCollection<MediaNode>> getRootItems,
        GamepadService gamepadService,
        int? initialTabIndex,
        Func<string, Task<bool>> confirm,
        Action markLibraryDirty,
        Func<Task<bool>> saveData)
    {
        var settingsVm = await CreateViewModelAsync(getRootItems(), gamepadService, initialTabIndex);
        settingsVm.RequestRunnerVersionRemovalConfirmation += runner =>
        {
            var message = runner.SourceType == RunnerVersionSourceType.ManagedDownload
                ? string.Format(
                    Strings.ResourceManager.GetString("Settings_RunnerVersionRemoveManagedConfirmFormat", Strings.Culture)
                    ?? "Remove {0} and permanently delete its downloaded files?",
                    runner.Name)
                : string.Format(
                    Strings.ResourceManager.GetString("Settings_RunnerVersionRemoveExternalConfirmFormat", Strings.Culture)
                    ?? "Remove {0} from Retromind? Its external files will be kept.",
                    runner.Name);

            return confirm(message);
        };
        settingsVm.RequestRunnerVersionReplacementConfirmation += (source, replacement) =>
        {
            var format = Strings.ResourceManager.GetString(
                             "Settings_RunnerVersionReplaceConfirmFormat",
                             Strings.Culture)
                         ?? "Replace all game assignments and emulator defaults from {0} with {1}?\n\n{0} remains installed and registered.";

            return confirm(string.Format(format, source.Name, replacement.Name));
        };
        settingsVm.RequestGeProtonReinstallConfirmation += releaseTag =>
        {
            var format = Strings.ResourceManager.GetString(
                             "Settings_GeProtonReinstallConfirmFormat",
                             Strings.Culture)
                         ?? "Download {0} again and replace the existing installation?";
            return confirm(string.Format(format, releaseTag));
        };
        settingsVm.RequestRunnerVersionAssignmentPersistence += async () =>
        {
            if (settingsVm.LibraryModified)
                markLibraryDirty();

            return await saveData();
        };
        var dialog = new SettingsView
        {
            DataContext = settingsVm
        };

        settingsVm.RequestClose += dialog.Close;
        settingsVm.RequestOpenLibraryHealth += async () =>
        {
            var healthDialog = new LibraryHealthView
            {
                DataContext = new LibraryHealthViewModel(_libraryHealthService, getRootItems())
            };
            await healthDialog.ShowDialog(dialog);
        };
        dialog.Closed += (_, _) => settingsVm.Dispose();

        return (settingsVm, dialog);
    }

    internal async Task CompleteAsync(
        SettingsViewModel settingsVm,
        bool portableHomeWasEnabled,
        bool requiresRestart,
        Action markLibraryDirty,
        Func<Task<(bool AllSaved, bool SettingsSaved)>> saveData,
        Action settingsAccepted,
        Func<bool, Task> refreshAchievements)
    {
        // A restore leaves the old settings model in memory until the app closes.
        if (requiresRestart)
            return;

        if (settingsVm.LibraryModified)
            markLibraryDirty();

        if (!settingsVm.IsSaved)
        {
            // Confirmed runner removals can change the library even when the dialog is cancelled.
            if (settingsVm.LibraryModified)
                await saveData();
            return;
        }

        settingsAccepted();
        var saveResult = await saveData();
        if (!saveResult.SettingsSaved)
        {
            var achievementSecretsRestored = await settingsVm.RollbackAchievementChangesAsync();
            var scraperSecretsRestored = await settingsVm.RollbackScraperCredentialChangesAsync();
            if (!achievementSecretsRestored || !scraperSecretsRestored)
            {
                Debug.WriteLine(
                    "[Settings] Secret values could not be fully restored after the settings save failed.");
            }
            return;
        }

        settingsVm.CompleteAchievementSecretChanges();
        settingsVm.CompleteScraperCredentialChanges();

        var portableHomeIsEnabled = _settings.UsePortableHomeInAppImage;
        await RetroAchievementsGameCatalogService.MigrateCacheLocationAsync(
            portableHomeWasEnabled, portableHomeIsEnabled);
        await StoreAchievementCachePathProvider.MigrateCacheLocationAsync(
            portableHomeWasEnabled, portableHomeIsEnabled);
        var cacheLocationChanged = _retroAchievementsCachePathProvider.UsePortableHome(portableHomeIsEnabled);
        cacheLocationChanged |= _storeAchievementCachePathProvider.UsePortableHome(portableHomeIsEnabled);
        await refreshAchievements(cacheLocationChanged);
    }
}
