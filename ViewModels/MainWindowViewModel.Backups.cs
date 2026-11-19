using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool _startupMetadataBackupAttempted;

    private async Task RestoreMetadataBackupAsync(MetadataBackupInfo backup, MetadataRestoreMode restoreMode)
    {
        await _metadataBackups.RestoreAsync(backup, restoreMode);
        _audioService.StopMusic();
    }

    private async Task<bool> OpenMetadataBackupsAsync(Window owner)
    {
        var viewModel = new MetadataBackupViewModel(
            _metadataBackupService,
            _metadataBackups.CreateBackupAsync,
            RestoreMetadataBackupAsync,
            _metadataBackups.ShouldCreateAutomaticBackup(MetadataBackupReason.BeforeRestore));
        var dialog = new MetadataBackupView { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task CreateStartupMetadataBackupIfEnabledAsync()
    {
        if (_startupMetadataBackupAttempted)
            return;

        _startupMetadataBackupAttempted = true;
        if (!_metadataBackups.ShouldCreateAutomaticBackup(MetadataBackupReason.OnStartup))
            return;

        try
        {
            await _metadataBackups.CreateBackupAsync(MetadataBackupReason.OnStartup);
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
}
