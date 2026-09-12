using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Services;

namespace Retromind.ViewModels;

public sealed record MetadataBackupRow(
    MetadataBackupInfo Backup,
    string CreatedText,
    string ReasonText,
    string VersionText,
    string SizeText,
    string? DetailText)
{
    public bool IsValid => Backup.IsValid;
}

public enum MetadataRestoreMode
{
    LibraryOnly,
    LibraryAndSettings
}

public sealed record MetadataRestoreModeOption(MetadataRestoreMode Value, string Label);

public sealed partial class MetadataBackupViewModel : ViewModelBase
{
    private readonly MetadataBackupService _backupService;
    private readonly Func<MetadataBackupReason, Task<MetadataBackupInfo>> _createCurrentBackup;
    private readonly Func<MetadataBackupInfo, MetadataRestoreMode, Task> _restoreBackup;
    private readonly bool _createsSafetyBackupBeforeRestore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteBackupCommand))]
    [NotifyPropertyChangedFor(nameof(SelectedBackupDetail))]
    private MetadataBackupRow? _selectedBackup;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteBackupCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private MetadataRestoreModeOption _selectedRestoreMode;

    public ObservableCollection<MetadataBackupRow> Backups { get; } = new();
    public IReadOnlyList<MetadataRestoreModeOption> RestoreModes { get; }

    public string Title => T("MetadataBackup.Title", "Metadata backups");
    public string Description => T(
        "MetadataBackup.Description",
        "Backups contain the library and settings, but no games, artwork, videos, music, manuals, themes or portable HOME data.");
    public string CreatedHeader => T("MetadataBackup.Created", "Created");
    public string ReasonHeader => T("MetadataBackup.Reason", "Reason");
    public string VersionHeader => T("MetadataBackup.Version", "Version");
    public string SizeHeader => T("MetadataBackup.Size", "Size");
    public string EmptyText => T("MetadataBackup.Empty", "No metadata backups exist yet.");
    public string CreateText => T("MetadataBackup.Create", "Create backup now");
    public string RestoreText => T("MetadataBackup.Restore", "Restore");
    public string RestoreModeLabel => T("MetadataBackup.RestoreMode", "Restore");
    public string DeleteText => T("MetadataBackup.Delete", "Delete");
    public string CloseText => T("Button_Close", "Close");
    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);
    public bool HasBackups => Backups.Count > 0;
    public string? SelectedBackupDetail => SelectedBackup?.DetailText;

    public IAsyncRelayCommand CreateBackupCommand { get; }
    public IAsyncRelayCommand RestoreBackupCommand { get; }
    public IAsyncRelayCommand DeleteBackupCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Func<string, Task<bool>>? RequestConfirmation;
    public event Action<bool>? RequestClose;

    public MetadataBackupViewModel(
        MetadataBackupService backupService,
        Func<MetadataBackupReason, Task<MetadataBackupInfo>> createCurrentBackup,
        Func<MetadataBackupInfo, MetadataRestoreMode, Task> restoreBackup,
        bool createsSafetyBackupBeforeRestore)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _createCurrentBackup = createCurrentBackup ?? throw new ArgumentNullException(nameof(createCurrentBackup));
        _restoreBackup = restoreBackup ?? throw new ArgumentNullException(nameof(restoreBackup));
        _createsSafetyBackupBeforeRestore = createsSafetyBackupBeforeRestore;

        RestoreModes =
        [
            new MetadataRestoreModeOption(
                MetadataRestoreMode.LibraryOnly,
                T("MetadataBackup.RestoreMode.LibraryOnly", "Library only")),
            new MetadataRestoreModeOption(
                MetadataRestoreMode.LibraryAndSettings,
                T("MetadataBackup.RestoreMode.LibraryAndSettings", "Library and settings"))
        ];
        _selectedRestoreMode = RestoreModes[0];

        CreateBackupCommand = new AsyncRelayCommand(CreateBackupAsync, () => !IsBusy);
        RestoreBackupCommand = new AsyncRelayCommand(
            RestoreSelectedBackupAsync,
            () => !IsBusy && SelectedBackup?.IsValid == true);
        DeleteBackupCommand = new AsyncRelayCommand(
            DeleteSelectedBackupAsync,
            () => !IsBusy && SelectedBackup != null);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(false), () => !IsBusy);
        Backups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasBackups));
    }

    public async Task LoadAsync()
    {
        await RunBusyAsync(async () =>
        {
            var selectedPath = SelectedBackup?.Backup.FilePath;
            var backups = await _backupService.GetBackupsAsync();

            Backups.Clear();
            foreach (var backup in backups)
                Backups.Add(CreateRow(backup));

            SelectedBackup = Backups.FirstOrDefault(row =>
                                     string.Equals(
                                         row.Backup.FilePath,
                                         selectedPath,
                                         StringComparison.Ordinal))
                                 ?? Backups.FirstOrDefault();
        }, clearStatus: false);
    }

    private async Task CreateBackupAsync()
    {
        await RunBusyAsync(async () =>
        {
            var created = await _createCurrentBackup(MetadataBackupReason.Manual);
            await ReloadCoreAsync(created.FilePath);
            StatusText = T("MetadataBackup.CreatedSuccess", "The metadata backup was created successfully.");
        });
    }

    private async Task RestoreSelectedBackupAsync()
    {
        var selected = SelectedBackup;
        if (selected?.IsValid != true)
            return;

        var format = SelectedRestoreMode.Value == MetadataRestoreMode.LibraryOnly
            ? T(
                "MetadataBackup.RestoreLibraryConfirmFormat",
                "Restore only the library from the backup dated {0}?\n\nThe current application settings will be kept.")
            : T(
                "MetadataBackup.RestoreAllConfirmFormat",
                "Restore the library and settings from the backup dated {0}?");
        var safetyNotice = _createsSafetyBackupBeforeRestore
            ? T(
                "MetadataBackup.RestoreSafetyEnabled",
                "If the current metadata was loaded successfully, it will be backed up first.")
            : T(
                "MetadataBackup.RestoreSafetyDisabled",
                "Automatic pre-restore backups are disabled. No additional safety backup will be created.");
        var closeNotice = T(
            "MetadataBackup.RestoreCloseNotice",
            "Retromind will then close.");
        var message = string.Format(CultureInfo.CurrentCulture, format, selected.CreatedText)
                      + $"\n\n{safetyNotice} {closeNotice}";
        if (!await ConfirmAsync(message))
            return;

        await RunBusyAsync(async () =>
        {
            await _restoreBackup(selected.Backup, SelectedRestoreMode.Value);
            RequestClose?.Invoke(true);
        });
    }

    private async Task DeleteSelectedBackupAsync()
    {
        var selected = SelectedBackup;
        if (selected == null)
            return;

        var format = T(
            "MetadataBackup.DeleteConfirmFormat",
            "Permanently delete the metadata backup from {0}?");
        if (!await ConfirmAsync(string.Format(CultureInfo.CurrentCulture, format, selected.CreatedText)))
            return;

        await RunBusyAsync(async () =>
        {
            await _backupService.DeleteBackupAsync(selected.Backup.FilePath);
            await ReloadCoreAsync(selectedPath: null);
            StatusText = T("MetadataBackup.DeletedSuccess", "The metadata backup was deleted.");
        });
    }

    private async Task ReloadCoreAsync(string? selectedPath)
    {
        var backups = await _backupService.GetBackupsAsync();
        Backups.Clear();
        foreach (var backup in backups)
            Backups.Add(CreateRow(backup));

        SelectedBackup = Backups.FirstOrDefault(row =>
                                 string.Equals(
                                     row.Backup.FilePath,
                                     selectedPath,
                                     StringComparison.Ordinal))
                             ?? Backups.FirstOrDefault();
    }

    private async Task RunBusyAsync(Func<Task> action, bool clearStatus = true)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        if (clearStatus)
            StatusText = string.Empty;

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            var format = T("MetadataBackup.OperationFailedFormat", "The backup operation failed.\n\n{0}");
            StatusText = string.Format(CultureInfo.CurrentCulture, format, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> ConfirmAsync(string message)
        => RequestConfirmation != null && await RequestConfirmation(message);

    private static MetadataBackupRow CreateRow(MetadataBackupInfo backup)
    {
        var reason = backup.Reason switch
        {
            MetadataBackupReason.Manual => T("MetadataBackup.Reason.Manual", "Manual"),
            MetadataBackupReason.BeforeBulkEdit => T("MetadataBackup.Reason.BeforeBulkEdit", "Before bulk edit"),
            MetadataBackupReason.BeforeBulkScrape => T("MetadataBackup.Reason.BeforeBulkScrape", "Before bulk scrape"),
            MetadataBackupReason.OnStartup => T("MetadataBackup.Reason.OnStartup", "At startup"),
            MetadataBackupReason.BeforeRestore => T("MetadataBackup.Reason.BeforeRestore", "Before restore"),
            _ => T("MetadataBackup.Reason.Invalid", "Invalid backup")
        };

        var version = string.IsNullOrWhiteSpace(backup.RetromindVersion)
            ? "–"
            : backup.RetromindVersion;
        var detail = backup.IsValid
            ? backup.FileName
            : string.Format(
                CultureInfo.CurrentCulture,
                T("MetadataBackup.InvalidFormat", "Invalid backup: {0}"),
                backup.ValidationError ?? T("MetadataBackup.UnknownError", "Unknown error"));

        return new MetadataBackupRow(
            backup,
            backup.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            reason,
            version,
            FormatSize(backup.ArchiveSizeBytes),
            detail);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return string.Format(CultureInfo.CurrentCulture, "{0:N0} B", bytes);

        var kibibytes = bytes / 1024d;
        if (kibibytes < 1024)
            return string.Format(CultureInfo.CurrentCulture, "{0:N1} KiB", kibibytes);

        return string.Format(CultureInfo.CurrentCulture, "{0:N1} MiB", kibibytes / 1024d);
    }
}
