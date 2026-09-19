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
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

public enum GogDlcCatalogFilter
{
    All,
    Linux,
    Windows,
    WithoutInstaller
}

public sealed record GogDlcFilterOption(GogDlcCatalogFilter Value, string Label);

public sealed record GogDlcInstallBatchResult(
    IReadOnlySet<string> InstalledProductIds,
    string Message);

public partial class GogDlcDialogViewModel : ViewModelBase, IDisposable
{
    private readonly GogInstallService _gogInstallService;
    private readonly string _gameId;
    private readonly GogInstallPlatform? _installedPlatform;
    private readonly Func<IReadOnlyList<GogDlcCatalogEntry>, CancellationToken, Task<GogDlcInstallBatchResult>>? _installAsync;
    private readonly HashSet<string> _installedProductIds;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IReadOnlyList<GogDlcCatalogEntry> _allEntries = Array.Empty<GogDlcCatalogEntry>();
    private int _selectedCount;
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private GogDlcFilterOption _selectedFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractionEnabled))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = string.Empty;

    public RangeObservableCollection<GogDlcCatalogEntry> FilteredDlcs { get; } = new();
    public IReadOnlyList<GogDlcFilterOption> FilterOptions { get; }

    public string DialogTitleText => T("Gog.Dlc.DialogTitle", "Manage GOG DLCs");
    public string HeaderText => T("Gog.Dlc.Header", "Owned DLCs");
    public string SearchPlaceholderText => T("Gog.Dlc.SearchPlaceholder", "Search by title or GOG ID...");
    public string RefreshText => T("Gog.Dlc.Refresh", "Refresh");
    public string CloseText => Strings.Button_Close;
    public string SelectAllFilteredText => T("Gog.Dlc.SelectAllFiltered", "Select all installable");
    public string ClearSelectionText => T("Gog.Dlc.ClearSelection", "Clear selection");
    public string InstallSelectionText => T("Gog.Dlc.InstallSelection", "Install selection");
    public string LoadingText => T("Gog.Dlc.Loading", "Loading DLCs from GOG...");
    public string EmptyStateText => T("Gog.Dlc.EmptyState", "No DLCs match the current filter.");
    public string InstalledText => T("Gog.Dlc.Installed", "Installed");
    public string MainGameRequiredText => T(
        "Gog.Dlc.MainGameRequired",
        "Install the main game before installing DLCs.");

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool ShowList => !IsLoading && !HasError && FilteredDlcs.Count > 0;
    public bool ShowEmptyState => !IsLoading && !HasError && FilteredDlcs.Count == 0;
    public bool IsInteractionEnabled => !IsLoading && !IsInstalling;
    public bool CanInstallDlcs => _installedPlatform.HasValue && _installAsync != null;
    public bool ShowMainGameRequired => !CanInstallDlcs;
    public int SelectedCount => _selectedCount;
    public string SelectionText => string.Format(
        CultureInfo.CurrentCulture,
        T("Gog.Dlc.SelectionFormat", "Selected: {0:N0}"),
        SelectedCount);

    public string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        T("Gog.Dlc.SummaryFormat", "{0:N0} owned · {1:N0} with supported offline installer · {2:N0} currently without Linux/Windows installer"),
        _allEntries.Count,
        _allEntries.Count(static entry => entry.HasInstaller),
        _allEntries.Count(static entry => !entry.HasInstaller));

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InstallSelectedCommand { get; }
    public IRelayCommand SelectAllFilteredCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Action? RequestClose;

    public GogDlcDialogViewModel(
        GogInstallService gogInstallService,
        string gameId,
        GogInstallPlatform? installedPlatform = null,
        IEnumerable<GogDlcInstallationState>? installedDlcs = null,
        Func<IReadOnlyList<GogDlcCatalogEntry>, CancellationToken, Task<GogDlcInstallBatchResult>>? installAsync = null)
    {
        _gogInstallService = gogInstallService ?? throw new ArgumentNullException(nameof(gogInstallService));
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        _gameId = gameId;
        _installedPlatform = installedPlatform;
        _installAsync = installAsync;
        _installedProductIds = installedDlcs?
            .Where(static state => !string.IsNullOrWhiteSpace(state.ProductId))
            .Select(static state => state.ProductId.Trim())
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        FilterOptions =
        [
            new(GogDlcCatalogFilter.All, T("Gog.Dlc.FilterAll", "All DLCs")),
            new(GogDlcCatalogFilter.Linux, T("Gog.Dlc.FilterLinux", "Linux installer")),
            new(GogDlcCatalogFilter.Windows, T("Gog.Dlc.FilterWindows", "Windows installer")),
            new(GogDlcCatalogFilter.WithoutInstaller, T("Gog.Dlc.FilterWithoutInstaller", "Currently no Linux/Windows installer"))
        ];
        _selectedFilter = FilterOptions[0];

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => IsInteractionEnabled);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, CanInstallSelection);
        SelectAllFilteredCommand = new RelayCommand(SelectAllFiltered, () => IsInteractionEnabled);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => IsInteractionEnabled && SelectedCount > 0);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
    }

    public async Task LoadAsync()
    {
        if (_disposed || IsLoading)
            return;

        IsLoading = true;
        ErrorMessage = string.Empty;
        RefreshCommand.NotifyCanExecuteChanged();
        RefreshPresentationState();

        try
        {
            var dlcs = await _gogInstallService
                .GetOwnedDlcCatalogAsync(_gameId, _lifetimeCts.Token);

            var linuxText = T("Gog.Install.PlatformLinux", "Linux");
            var windowsText = T("Gog.Install.PlatformWindows", "Windows");
            var noInstallerText = T("Gog.Dlc.NoSeparateInstaller", "Currently no Linux/Windows offline installer");

            foreach (var entry in _allEntries)
                entry.SelectionChanged -= OnEntrySelectionChanged;

            _selectedCount = 0;
            _allEntries = dlcs
                .Select(dlc => new GogDlcCatalogEntry(
                    dlc,
                    linuxText,
                    windowsText,
                    noInstallerText,
                    InstalledText,
                    _installedPlatform,
                    _installedProductIds.Contains(dlc.ProductId)))
                .OrderBy(static entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static entry => entry.ProductId, StringComparer.Ordinal)
                .ToArray();

            foreach (var entry in _allEntries)
                entry.SelectionChanged += OnEntrySelectionChanged;

            ApplyFilter();
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] DLC catalog load failed: {ex.Message}");
            ErrorMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("Gog.Dlc.LoadFailedFormat", "DLCs could not be loaded: {0}"),
                ex.Message);
            FilteredDlcs.Clear();
        }
        finally
        {
            IsLoading = false;
            RefreshCommand.NotifyCanExecuteChanged();
            RefreshPresentationState();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedFilterChanged(GogDlcFilterOption value) => ApplyFilter();

    partial void OnIsInstallingChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        SelectAllFilteredCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private bool CanInstallSelection() =>
        CanInstallDlcs && IsInteractionEnabled && SelectedCount > 0;

    private async Task InstallSelectedAsync()
    {
        if (_installAsync == null || !CanInstallSelection())
            return;

        var selected = _allEntries.Where(static entry => entry.IsSelected).ToArray();
        if (selected.Length == 0)
            return;

        IsInstalling = true;
        StatusMessage = string.Empty;
        try
        {
            var result = await _installAsync(selected, _lifetimeCts.Token);
            foreach (var productId in result.InstalledProductIds)
                _installedProductIds.Add(productId);

            foreach (var entry in _allEntries)
            {
                if (!_installedProductIds.Contains(entry.ProductId))
                    continue;

                entry.IsInstalled = true;
                entry.IsSelected = false;
            }

            StatusMessage = result.Message;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] DLC installation failed: {ex.Message}");
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("Gog.Dlc.InstallFailedFormat", "DLC installation failed: {0}"),
                ex.Message);
        }
        finally
        {
            IsInstalling = false;
            RefreshPresentationState();
        }
    }

    private void SelectAllFiltered()
    {
        foreach (var entry in FilteredDlcs)
        {
            if (entry.IsSelectionEnabled)
                entry.IsSelected = true;
        }
    }

    private void ClearSelection()
    {
        foreach (var entry in _allEntries)
            entry.IsSelected = false;
    }

    private void OnEntrySelectionChanged(GogDlcCatalogEntry entry)
    {
        _selectedCount += entry.IsSelected ? 1 : -1;
        _selectedCount = Math.Max(0, _selectedCount);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        InstallSelectedCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        if (_disposed)
            return;

        IEnumerable<GogDlcCatalogEntry> query = _allEntries;
        query = SelectedFilter.Value switch
        {
            GogDlcCatalogFilter.Linux => query.Where(static entry => entry.HasLinuxInstaller),
            GogDlcCatalogFilter.Windows => query.Where(static entry => entry.HasWindowsInstaller),
            GogDlcCatalogFilter.WithoutInstaller => query.Where(static entry => !entry.HasInstaller),
            _ => query
        };

        var filter = SearchText.Trim();
        if (filter.Length > 0)
            query = query.Where(entry => entry.Matches(filter));

        FilteredDlcs.ReplaceAll(query);
        RefreshPresentationState();
    }

    private void RefreshPresentationState()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(SelectionText));
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var entry in _allEntries)
            entry.SelectionChanged -= OnEntrySelectionChanged;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
    }
}

public sealed partial class GogDlcCatalogEntry : ObservableObject
{
    private readonly string _searchIndex;

    public string ProductId { get; }
    public string Title { get; }
    public bool HasLinuxInstaller { get; }
    public bool HasWindowsInstaller { get; }
    public bool HasInstaller => HasLinuxInstaller || HasWindowsInstaller;
    public string InstallerPlatformsText { get; }
    public bool IsInstallableForCurrentPlatform { get; }
    public bool IsSelectionEnabled => IsInstallableForCurrentPlatform && !IsInstalled;
    public string StatusText => IsInstalled ? _installedText : InstallerPlatformsText;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionEnabled))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isInstalled;

    private readonly string _installedText;

    public event Action<GogDlcCatalogEntry>? SelectionChanged;

    public GogDlcCatalogEntry(
        GogDlcCatalogItem item,
        string linuxText,
        string windowsText,
        string noInstallerText,
        string installedText,
        GogInstallPlatform? installedPlatform,
        bool isInstalled)
    {
        ProductId = item.ProductId;
        Title = item.Title;
        HasLinuxInstaller = item.AvailableInstallerPlatforms.Contains(GogInstallPlatform.Linux);
        HasWindowsInstaller = item.AvailableInstallerPlatforms.Contains(GogInstallPlatform.Windows);
        IsInstallableForCurrentPlatform = installedPlatform switch
        {
            GogInstallPlatform.Linux => HasLinuxInstaller,
            GogInstallPlatform.Windows => HasWindowsInstaller,
            _ => false
        };
        _installedText = installedText;
        _isInstalled = isInstalled;

        var platforms = new List<string>(2);
        if (HasLinuxInstaller)
            platforms.Add(linuxText);
        if (HasWindowsInstaller)
            platforms.Add(windowsText);

        InstallerPlatformsText = platforms.Count > 0
            ? string.Join(" · ", platforms)
            : noInstallerText;
        _searchIndex = $"{Title}\n{ProductId}";
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);

    partial void OnIsInstalledChanged(bool value)
    {
        if (value && IsSelected)
            IsSelected = false;
    }

    public bool Matches(string filter) =>
        _searchIndex.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}
