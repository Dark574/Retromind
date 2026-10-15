using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.ViewModels;

public enum EpicDlcCatalogFilter
{
    All,
    Installed,
    NotInstalled,
    Updates
}

public sealed record EpicDlcFilterOption(EpicDlcCatalogFilter Value, string Label);

public sealed partial class EpicDlcDialogViewModel : ViewModelBase, IDisposable
{
    private readonly EpicInstallService _installService;
    private readonly MediaItem _item;
    private readonly Func<EpicDlcCatalogEntry, CancellationToken, Task<bool>> _installAsync;
    private readonly Func<EpicDlcCatalogEntry, CancellationToken, Task<bool>> _uninstallAsync;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IReadOnlyList<EpicDlcCatalogEntry> _allEntries = Array.Empty<EpicDlcCatalogEntry>();
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private EpicDlcFilterOption _selectedFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isOperating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private EpicDlcCatalogEntry? _selectedDlc;

    public RangeObservableCollection<EpicDlcCatalogEntry> FilteredDlcs { get; } = new();
    public IReadOnlyList<EpicDlcFilterOption> FilterOptions { get; }

    public string DialogTitleText => T("Epic.Dlc.DialogTitle", "Manage Epic Games DLCs");
    public string HeaderText => T("Epic.Dlc.Header", "Owned DLCs");
    public string SearchPlaceholderText => T("Epic.Dlc.SearchPlaceholder", "Search by title or Epic app name...");
    public string RefreshText => T("Epic.Dlc.Refresh", "Refresh");
    public string CloseText => Strings.Button_Close;
    public string LoadingText => T("Epic.Dlc.Loading", "Loading DLCs through Legendary...");
    public string EmptyStateText => T("Epic.Dlc.EmptyState", "No DLCs match the current filter.");
    public string InstallText => SelectedDlc?.IsInstalled == true
        ? T("Epic.Dlc.Update", "Update DLC")
        : T("Epic.Dlc.Install", "Install DLC");
    public string UninstallText => T("Epic.Dlc.Uninstall", "Uninstall DLC");

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowList => !IsLoading && !HasError && FilteredDlcs.Count > 0;
    public bool ShowEmptyState => !IsLoading && !HasError && FilteredDlcs.Count == 0;
    public bool IsInteractionEnabled => !IsLoading && !IsOperating;
    public bool CanInstallSelected =>
        IsInteractionEnabled && SelectedDlc is { CanInstallOrUpdate: true };
    public bool CanUninstallSelected =>
        IsInteractionEnabled && SelectedDlc is { IsInstalled: true };

    public string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        T("Epic.Dlc.SummaryFormat", "{0:N0} owned · {1:N0} installed · {2:N0} updates"),
        _allEntries.Count,
        _allEntries.Count(static entry => entry.IsInstalled),
        _allEntries.Count(static entry => entry.HasUpdateAvailable));

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InstallSelectedCommand { get; }
    public IAsyncRelayCommand UninstallSelectedCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Action? RequestClose;

    public EpicDlcDialogViewModel(
        EpicInstallService installService,
        MediaItem item,
        Func<EpicDlcCatalogEntry, CancellationToken, Task<bool>> installAsync,
        Func<EpicDlcCatalogEntry, CancellationToken, Task<bool>> uninstallAsync)
    {
        _installService = installService ?? throw new ArgumentNullException(nameof(installService));
        _item = item ?? throw new ArgumentNullException(nameof(item));
        _installAsync = installAsync ?? throw new ArgumentNullException(nameof(installAsync));
        _uninstallAsync = uninstallAsync ?? throw new ArgumentNullException(nameof(uninstallAsync));
        FilterOptions =
        [
            new(EpicDlcCatalogFilter.All, T("Epic.Dlc.FilterAll", "All DLCs")),
            new(EpicDlcCatalogFilter.Installed, T("Epic.Dlc.FilterInstalled", "Installed")),
            new(EpicDlcCatalogFilter.NotInstalled, T("Epic.Dlc.FilterNotInstalled", "Not installed")),
            new(EpicDlcCatalogFilter.Updates, T("Epic.Dlc.FilterUpdates", "Updates"))
        ];
        _selectedFilter = FilterOptions[0];

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => IsInteractionEnabled);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, () => CanInstallSelected);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync, () => CanUninstallSelected);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    public async Task LoadAsync()
    {
        if (_disposed || IsLoading || IsOperating)
            return;

        var selectedAppName = SelectedDlc?.AppName;
        IsLoading = true;
        ErrorMessage = string.Empty;
        RefreshCommand.NotifyCanExecuteChanged();
        RefreshPresentationState();

        try
        {
            var dlcs = await _installService.GetDlcCatalogAsync(_item, _lifetimeCts.Token);
            _allEntries = dlcs
                .Select(dlc => new EpicDlcCatalogEntry(
                    dlc,
                    T("Epic.Dlc.NotInstalled", "Not installed"),
                    T("Epic.Dlc.UpToDate", "Up to date"),
                    T("Epic.Dlc.UpdateAvailable", "Update available"),
                    T("Epic.Dlc.UpdateUnknown", "Installed · update status unknown")))
                .ToArray();
            ApplyFilter();
            SelectedDlc = _allEntries.FirstOrDefault(entry => string.Equals(
                                  entry.AppName,
                                  selectedAppName,
                                  StringComparison.Ordinal)) ??
                          FilteredDlcs.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] DLC catalog load failed: {ex.Message}");
            ErrorMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("Epic.Dlc.LoadFailedFormat", "DLCs could not be loaded: {0}"),
                ex.Message);
            FilteredDlcs.Clear();
            SelectedDlc = null;
        }
        finally
        {
            IsLoading = false;
            RefreshCommand.NotifyCanExecuteChanged();
            RefreshPresentationState();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(EpicDlcFilterOption value) => ApplyFilter();

    partial void OnSelectedDlcChanged(EpicDlcCatalogEntry? value) => RefreshActionState();

    partial void OnIsOperatingChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        RefreshActionState();
    }

    private async Task InstallSelectedAsync()
    {
        if (!CanInstallSelected || SelectedDlc is not { } entry)
            return;

        await RunOperationAsync(entry, _installAsync);
    }

    private async Task UninstallSelectedAsync()
    {
        if (!CanUninstallSelected || SelectedDlc is not { } entry)
            return;

        await RunOperationAsync(entry, _uninstallAsync);
    }

    private async Task RunOperationAsync(
        EpicDlcCatalogEntry entry,
        Func<EpicDlcCatalogEntry, CancellationToken, Task<bool>> operation)
    {
        IsOperating = true;
        var succeeded = false;
        try
        {
            succeeded = await operation(entry, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] DLC operation failed: {ex.Message}");
            ErrorMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("Epic.Dlc.OperationFailedFormat", "The DLC operation failed: {0}"),
                ex.Message);
        }
        finally
        {
            IsOperating = false;
        }

        if (succeeded)
            await LoadAsync();
    }

    private void ApplyFilter()
    {
        if (_disposed)
            return;

        IEnumerable<EpicDlcCatalogEntry> query = SelectedFilter.Value switch
        {
            EpicDlcCatalogFilter.Installed => _allEntries.Where(static entry => entry.IsInstalled),
            EpicDlcCatalogFilter.NotInstalled => _allEntries.Where(static entry => !entry.IsInstalled),
            EpicDlcCatalogFilter.Updates => _allEntries.Where(static entry => entry.HasUpdateAvailable),
            _ => _allEntries
        };

        var filter = SearchText.Trim();
        if (filter.Length > 0)
            query = query.Where(entry => entry.Matches(filter));

        FilteredDlcs.ReplaceAll(query);
        if (SelectedDlc == null || !FilteredDlcs.Contains(SelectedDlc))
            SelectedDlc = FilteredDlcs.FirstOrDefault();
        RefreshPresentationState();
    }

    private void RefreshPresentationState()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsInteractionEnabled));
        RefreshActionState();
    }

    private void RefreshActionState()
    {
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanInstallSelected));
        OnPropertyChanged(nameof(CanUninstallSelected));
        InstallSelectedCommand.NotifyCanExecuteChanged();
        UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}

public sealed class EpicDlcCatalogEntry
{
    private readonly string _searchIndex;

    public string AppName { get; }
    public string Title { get; }
    public bool IsInstalled { get; }
    public bool HasUpdateAvailable { get; }
    public bool CanInstallOrUpdate => !IsInstalled || HasUpdateAvailable;
    public string StatusText { get; }

    internal EpicDlcCatalogEntry(
        LegendaryDlc dlc,
        string notInstalledText,
        string upToDateText,
        string updateAvailableText,
        string updateUnknownText)
    {
        AppName = dlc.AppName;
        Title = dlc.Title;
        IsInstalled = dlc.IsInstalled;
        HasUpdateAvailable = dlc.HasUpdateAvailable;
        StatusText = !dlc.IsInstalled
            ? notInstalledText
            : dlc.HasUpdateAvailable
                ? updateAvailableText
                : string.IsNullOrWhiteSpace(dlc.AvailableVersion) || string.IsNullOrWhiteSpace(dlc.InstalledVersion)
                    ? updateUnknownText
                    : upToDateText;
        _searchIndex = $"{Title}\n{AppName}";
    }

    public bool Matches(string filter) =>
        _searchIndex.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}
