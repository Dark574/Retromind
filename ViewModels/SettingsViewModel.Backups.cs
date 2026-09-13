using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Retromind.ViewModels;

public partial class SettingsViewModel
{
    public string MetadataBackupSectionTitle =>
        T("MetadataBackup.SettingsSection", "Metadata backups");

    public string MetadataBackupSectionHint => T(
        "MetadataBackup.SettingsHint",
        "Manual backups are always available. Choose which events should create automatic backups.");

    public string EnableAutomaticMetadataBackupsText => T(
        "MetadataBackup.EnableAutomatic",
        "Create automatic metadata backups");

    public string BackupBeforeBulkEditText => T(
        "MetadataBackup.Trigger.BeforeBulkEdit",
        "Before applying a bulk edit");

    public string BackupBeforeBulkScrapeText => T(
        "MetadataBackup.Trigger.BeforeBulkScrape",
        "Before starting a bulk scrape");

    public string BackupOnStartupText => T(
        "MetadataBackup.Trigger.OnStartup",
        "When Retromind starts");

    public string BackupBeforeRestoreText => T(
        "MetadataBackup.Trigger.BeforeRestore",
        "Before restoring another backup");

    public string MetadataBackupRetentionHint => T(
        "MetadataBackup.RetentionHint",
        "The ten newest valid automatic startup, bulk-edit and bulk-scrape backups are retained. Invalid archives do not count toward this limit. Manual, pre-restore and invalid backups remain until you delete them.");

    public string ManageMetadataBackupsText =>
        T("MetadataBackup.Manage", "Manage backups...");

    public bool EnableAutomaticMetadataBackups
    {
        get => _appSettings.EnableAutomaticMetadataBackups;
        set
        {
            if (_appSettings.EnableAutomaticMetadataBackups == value)
                return;

            _appSettings.EnableAutomaticMetadataBackups = value;
            OnPropertyChanged();
        }
    }

    public bool BackupBeforeBulkEdit
    {
        get => _appSettings.BackupBeforeBulkEdit;
        set { if (_appSettings.BackupBeforeBulkEdit != value) { _appSettings.BackupBeforeBulkEdit = value; OnPropertyChanged(); } }
    }

    public bool BackupBeforeBulkScrape
    {
        get => _appSettings.BackupBeforeBulkScrape;
        set { if (_appSettings.BackupBeforeBulkScrape != value) { _appSettings.BackupBeforeBulkScrape = value; OnPropertyChanged(); } }
    }

    public bool BackupOnStartup
    {
        get => _appSettings.BackupOnStartup;
        set { if (_appSettings.BackupOnStartup != value) { _appSettings.BackupOnStartup = value; OnPropertyChanged(); } }
    }

    public bool BackupBeforeRestore
    {
        get => _appSettings.BackupBeforeRestore;
        set { if (_appSettings.BackupBeforeRestore != value) { _appSettings.BackupBeforeRestore = value; OnPropertyChanged(); } }
    }

    public event Func<Task>? RequestOpenMetadataBackups;

    [RelayCommand]
    private async Task OpenMetadataBackupsAsync()
    {
        if (RequestOpenMetadataBackups != null)
            await RequestOpenMetadataBackups();
    }
}
