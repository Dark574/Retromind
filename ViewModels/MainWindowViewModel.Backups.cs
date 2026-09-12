using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool _startupMetadataBackupAttempted;

    private async Task<MetadataBackupInfo> CreateCurrentMetadataBackupAsync(MetadataBackupReason reason)
    {
        if (_libraryLoadFailed)
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
        return await _metadataBackupService.CreateBackupAsync(content, reason);
    }

    private async Task<MetadataBackupContent> CaptureCurrentMetadataAsync()
    {
        var librarySnapshot = await UiThreadHelper.InvokeAsync(() => _dataService.CreateSnapshot(RootItems));
        var libraryJson = await Task.Run(() => _dataService.Serialize(librarySnapshot));
        var settingsJson = await UiThreadHelper.InvokeAsync(() => _settingsService.Serialize(_currentSettings));
        return new MetadataBackupContent(libraryJson, settingsJson);
    }

    private async Task RestoreMetadataBackupAsync(
        MetadataBackupInfo backup,
        MetadataRestoreMode restoreMode)
    {
        var restored = await _metadataBackupService.ReadBackupAsync(backup.FilePath);

        // Validate the actual persistence contracts, not only JSON syntax and archive checksums.
        MediaDataService.ValidateSerializedLibrary(restored.LibraryJson);
        if (restoreMode == MetadataRestoreMode.LibraryAndSettings)
            SettingsService.ValidateSerializedSettings(restored.SettingsJson);

        MetadataBackupContent? current = null;
        if (!_libraryLoadFailed && !_settingsService.HasLoadFailure)
        {
            current = await CaptureCurrentMetadataAsync();
            if (ShouldCreateAutomaticMetadataBackup(MetadataBackupReason.BeforeRestore))
                await _metadataBackupService.CreateBackupAsync(current, MetadataBackupReason.BeforeRestore);
        }

        // Prevent delayed settings writes from overtaking the restored settings. The library
        // sequencer is drained before the restore and the persistence services serialize file IO.
        _saveSettingsCts?.Cancel();
        if (!_libraryLoadFailed)
            await _libraryTracker.SaveIfDirtyAsync(force: false);

        try
        {
            await _dataService.RestoreJsonAsync(restored.LibraryJson);
            if (restoreMode == MetadataRestoreMode.LibraryAndSettings)
                await _settingsService.RestoreJsonAsync(restored.SettingsJson);
        }
        catch (Exception restoreException)
        {
            if (current == null)
                throw;

            try
            {
                await _dataService.RestoreJsonAsync(current.LibraryJson);
                if (restoreMode == MetadataRestoreMode.LibraryAndSettings)
                    await _settingsService.RestoreJsonAsync(current.SettingsJson);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The metadata restore failed and Retromind could not fully restore the preceding state.",
                    restoreException,
                    rollbackException);
            }

            throw;
        }

        // The in-memory models still represent the preceding state. The normal shutdown save
        // must therefore be skipped or it would immediately overwrite the restored files.
        _closeWithoutPersistenceAfterRestore = true;
        _libraryTracker.StopTracking();
        _audioService.StopMusic();
    }

    private async Task<bool> OpenMetadataBackupsAsync(Window owner)
    {
        var viewModel = new MetadataBackupViewModel(
            _metadataBackupService,
            CreateCurrentMetadataBackupAsync,
            RestoreMetadataBackupAsync,
            ShouldCreateAutomaticMetadataBackup(MetadataBackupReason.BeforeRestore));
        var dialog = new MetadataBackupView { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task CreateStartupMetadataBackupIfEnabledAsync()
    {
        if (_startupMetadataBackupAttempted)
            return;

        _startupMetadataBackupAttempted = true;
        if (!ShouldCreateAutomaticMetadataBackup(MetadataBackupReason.OnStartup))
            return;

        try
        {
            await CreateCurrentMetadataBackupAsync(MetadataBackupReason.OnStartup);
        }
        catch (Exception ex)
        {
            if (CurrentWindow is { } owner)
            {
                var format = T(
                    "MetadataBackup.StartupFailedFormat",
                    "The automatic startup backup could not be created. Retromind will continue normally.\n\n{0}");
                await ShowInfoDialog(owner, string.Format(format, ex.Message));
            }
        }
    }

    private bool ShouldCreateAutomaticMetadataBackup(MetadataBackupReason reason)
    {
        if (!_currentSettings.EnableAutomaticMetadataBackups)
            return false;

        return reason switch
        {
            MetadataBackupReason.BeforeBulkEdit => _currentSettings.BackupBeforeBulkEdit,
            MetadataBackupReason.BeforeBulkScrape => _currentSettings.BackupBeforeBulkScrape,
            MetadataBackupReason.OnStartup => _currentSettings.BackupOnStartup,
            MetadataBackupReason.BeforeRestore => _currentSettings.BackupBeforeRestore,
            _ => false
        };
    }
}
