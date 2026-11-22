using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

internal sealed class DesktopMetadataBackupCoordinator
{
    private readonly MetadataBackupService _backupService;
    private readonly MediaDataService _dataService;
    private readonly SettingsService _settingsService;
    private readonly LibraryChangeTracker _libraryTracker;
    private readonly AppSettings _settings;
    private readonly Func<ObservableCollection<MediaNode>> _getRootItems;
    private readonly Func<bool> _libraryLoadFailed;
    private readonly Action _cancelPendingSettingsSave;

    internal DesktopMetadataBackupCoordinator(
        MetadataBackupService backupService,
        MediaDataService dataService,
        SettingsService settingsService,
        LibraryChangeTracker libraryTracker,
        AppSettings settings,
        Func<ObservableCollection<MediaNode>> getRootItems,
        Func<bool> libraryLoadFailed,
        Action cancelPendingSettingsSave)
    {
        _backupService = backupService;
        _dataService = dataService;
        _settingsService = settingsService;
        _libraryTracker = libraryTracker;
        _settings = settings;
        _getRootItems = getRootItems;
        _libraryLoadFailed = libraryLoadFailed;
        _cancelPendingSettingsSave = cancelPendingSettingsSave;
    }

    internal bool RequiresRestart { get; private set; }

    internal async Task<MetadataBackupInfo> CreateBackupAsync(MetadataBackupReason reason)
    {
        if (_libraryLoadFailed())
        {
            throw new InvalidOperationException(T(
                "MetadataBackup.LibraryUnavailable",
                "A metadata backup cannot be created because the library was not loaded successfully."));
        }

        if (_settingsService.HasLoadFailure)
        {
            throw new InvalidOperationException(T(
                "MetadataBackup.SettingsUnavailable",
                "A metadata backup cannot be created because the application settings were not loaded successfully."));
        }

        var content = await CaptureCurrentMetadataAsync();
        return await _backupService.CreateBackupAsync(content, reason);
    }

    private async Task<MetadataBackupContent> CaptureCurrentMetadataAsync()
    {
        var librarySnapshot = await UiThreadHelper.InvokeAsync(() => _dataService.CreateSnapshot(_getRootItems()));
        var libraryJson = await Task.Run(() => _dataService.Serialize(librarySnapshot));
        var settingsJson = await CaptureCurrentSettingsAsync();
        return new MetadataBackupContent(libraryJson, settingsJson);
    }

    private Task<string> CaptureCurrentSettingsAsync() =>
        UiThreadHelper.InvokeAsync(() => _settingsService.Serialize(_settings));

    internal async Task RestoreAsync(
        MetadataBackupInfo backup,
        MetadataRestoreMode restoreMode)
    {
        var restored = await _backupService.ReadBackupAsync(backup.FilePath);

        // Validate the actual persistence contracts, not only JSON syntax and archive checksums.
        MediaDataService.ValidateSerializedLibrary(restored.LibraryJson);
        if (restoreMode == MetadataRestoreMode.LibraryAndSettings)
            SettingsService.ValidateSerializedSettings(restored.SettingsJson);

        // Block every regular settings save before capturing the preceding state.
        // The scope drains an active write and unblocks automatically if restore fails.
        _cancelPendingSettingsSave();
        using var settingsRestore = await _settingsService.BeginRestoreAsync();

        var libraryLoaded = !_libraryLoadFailed();
        await _libraryTracker.RunRestoreAsync(async () =>
        {
            if (libraryLoaded && !_settingsService.HasLoadFailure && ShouldCreateAutomaticBackup(MetadataBackupReason.BeforeRestore))
            {
                var current = await CaptureCurrentMetadataAsync();
                await _backupService.CreateBackupAsync(current, MetadataBackupReason.BeforeRestore);
            }

            if (restoreMode == MetadataRestoreMode.LibraryAndSettings)
            {
                await _dataService.RestoreWithSettingsAsync(restored.LibraryJson,
                    () => _settingsService.RestoreJsonAsync(restored.SettingsJson));
            }
            else
            {
                // Library-only restores skip the normal shutdown flush.
                if (!_settingsService.HasLoadFailure)
                    await settingsRestore.SaveCurrentSettingsAsync(await CaptureCurrentSettingsAsync());
                await _dataService.RestoreJsonAsync(restored.LibraryJson);
            }

            await UiThreadHelper.InvokeAsync(() =>
            {
                settingsRestore.Complete();
                RequiresRestart = true;
                _libraryTracker.StopTracking();
            });
        }, flushCurrentChanges: libraryLoaded);
    }

    internal bool ShouldCreateAutomaticBackup(MetadataBackupReason reason)
    {
        if (!_settings.EnableAutomaticMetadataBackups)
            return false;

        return reason switch
        {
            MetadataBackupReason.BeforeBulkEdit => _settings.BackupBeforeBulkEdit,
            MetadataBackupReason.BeforeBulkScrape => _settings.BackupBeforeBulkScrape,
            MetadataBackupReason.OnStartup => _settings.BackupOnStartup,
            MetadataBackupReason.BeforeRestore => _settings.BackupBeforeRestore,
            _ => false
        };
    }

    private static string T(string key, string fallback)
    {
        var value = Strings.ResourceManager.GetString(key, Strings.Culture);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
