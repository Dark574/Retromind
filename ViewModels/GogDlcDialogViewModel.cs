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

public partial class GogDlcDialogViewModel : ViewModelBase, IDisposable
{
    private readonly GogInstallService _gogInstallService;
    private readonly string _gameId;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IReadOnlyList<GogDlcCatalogEntry> _allEntries = Array.Empty<GogDlcCatalogEntry>();
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

    public RangeObservableCollection<GogDlcCatalogEntry> FilteredDlcs { get; } = new();
    public IReadOnlyList<GogDlcFilterOption> FilterOptions { get; }

    public string DialogTitleText => T("Gog.Dlc.DialogTitle", "Manage GOG DLCs");
    public string HeaderText => T("Gog.Dlc.Header", "Owned DLCs");
    public string SearchPlaceholderText => T("Gog.Dlc.SearchPlaceholder", "Search by title or GOG ID...");
    public string RefreshText => T("Gog.Dlc.Refresh", "Refresh");
    public string CloseText => Strings.Button_Close;
    public string LoadingText => T("Gog.Dlc.Loading", "Loading DLCs from GOG...");
    public string EmptyStateText => T("Gog.Dlc.EmptyState", "No DLCs match the current filter.");

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowList => !IsLoading && !HasError && FilteredDlcs.Count > 0;
    public bool ShowEmptyState => !IsLoading && !HasError && FilteredDlcs.Count == 0;

    public string SummaryText => string.Format(
        CultureInfo.CurrentCulture,
        T("Gog.Dlc.SummaryFormat", "{0:N0} owned · {1:N0} with supported offline installer · {2:N0} currently without Linux/Windows installer"),
        _allEntries.Count,
        _allEntries.Count(static entry => entry.HasInstaller),
        _allEntries.Count(static entry => !entry.HasInstaller));

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Action? RequestClose;

    public GogDlcDialogViewModel(GogInstallService gogInstallService, string gameId)
    {
        _gogInstallService = gogInstallService ?? throw new ArgumentNullException(nameof(gogInstallService));
        if (string.IsNullOrWhiteSpace(gameId))
            throw new ArgumentException("Game ID is required.", nameof(gameId));

        _gameId = gameId;
        FilterOptions =
        [
            new(GogDlcCatalogFilter.All, T("Gog.Dlc.FilterAll", "All DLCs")),
            new(GogDlcCatalogFilter.Linux, T("Gog.Dlc.FilterLinux", "Linux installer")),
            new(GogDlcCatalogFilter.Windows, T("Gog.Dlc.FilterWindows", "Windows installer")),
            new(GogDlcCatalogFilter.WithoutInstaller, T("Gog.Dlc.FilterWithoutInstaller", "Currently no Linux/Windows installer"))
        ];
        _selectedFilter = FilterOptions[0];

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
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

            _allEntries = dlcs
                .Select(dlc => new GogDlcCatalogEntry(dlc, linuxText, windowsText, noInstallerText))
                .OrderBy(static entry => entry.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static entry => entry.ProductId, StringComparer.Ordinal)
                .ToArray();

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

public sealed class GogDlcCatalogEntry
{
    private readonly string _searchIndex;

    public string ProductId { get; }
    public string Title { get; }
    public bool HasLinuxInstaller { get; }
    public bool HasWindowsInstaller { get; }
    public bool HasInstaller => HasLinuxInstaller || HasWindowsInstaller;
    public string InstallerPlatformsText { get; }

    public GogDlcCatalogEntry(
        GogDlcCatalogItem item,
        string linuxText,
        string windowsText,
        string noInstallerText)
    {
        ProductId = item.ProductId;
        Title = item.Title;
        HasLinuxInstaller = item.AvailableInstallerPlatforms.Contains(GogInstallPlatform.Linux);
        HasWindowsInstaller = item.AvailableInstallerPlatforms.Contains(GogInstallPlatform.Windows);

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

    public bool Matches(string filter) =>
        _searchIndex.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}
