using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel handling the manual scraping dialog for a single item.
/// Allows the user to select a scraper service, enter a query, and pick a result.
/// </summary>
public partial class ScrapeDialogViewModel : ViewModelBase, IDisposable
{
    private const int MaxResults = 200;

    private readonly MetadataService _metadataService;
    private readonly MediaItem _targetItem;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _searchCts;
    private bool _disposed;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ScraperConfig? _selectedScraper;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private ScraperSearchResult? _selectedResult;

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();

    // Bulk-update friendly collection (prevents UI stalls when a provider returns many results).
    public RangeObservableCollection<ScraperSearchResult> SearchResults { get; } = new();

    public IAsyncRelayCommand SearchCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }

    public event Func<ScraperSearchResult, Task>? OnResultSelectedAsync;

    public ScrapeDialogViewModel(MediaItem item, AppSettings settings, MetadataService metadataService)
    {
        _targetItem = item ?? throw new ArgumentNullException(nameof(item));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));

        InitializeData();

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => SelectedResult != null);
    }

    private void InitializeData()
    {
        SearchQuery = _targetItem.Title ?? string.Empty;

        AvailableScrapers.Clear();
        foreach (var s in _settings.Scrapers.Where(s => s.Type != ScraperType.EmuMovies))
            AvailableScrapers.Add(s);

        SelectedScraper = AvailableScrapers.Count > 0 ? AvailableScrapers[0] : null;
    }

    private async Task SearchAsync()
    {
        if (_disposed)
            return;

        if (SelectedScraper == null || string.IsNullOrWhiteSpace(SearchQuery))
            return;

        // Cancel previous search (avoid out-of-order results + unnecessary traffic).
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsBusy = true;
        SearchResults.Clear();
        SelectedResult = null;
        StatusMessage = string.Empty;

        try
        {
            var provider = await _metadataService.GetProviderAsync(SelectedScraper.Id, token);
            if (provider == null)
            {
                StatusMessage = Strings.Metadata_Error_ProviderNotAvailable;
                return;
            }

            var results = await provider.SearchAsync(SearchQuery, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (results.Count == 0)
            {
                StatusMessage = Strings.Metadata_Search_NoResults;
                return;
            }

            var limited = results.Take(MaxResults);
            await UiThreadHelper.InvokeAsync(() => SearchResults.ReplaceAll(limited));
        }
        catch (OperationCanceledException)
        {
            // Expected when the user searches again quickly; keep UI quiet.
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Strings.Metadata_Search_FailedFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
            if (_disposed)
            {
                _searchCts?.Dispose();
                _searchCts = null;
            }
        }
    }

    private async Task ApplyAsync()
    {
        if (_disposed || SelectedResult == null)
            return;

        var handler = OnResultSelectedAsync;
        if (handler == null)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            if (subscriber is Func<ScraperSearchResult, Task> callback)
                await callback(SelectedResult);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_searchCts != null)
        {
            _searchCts.Cancel();
            if (!IsBusy)
            {
                _searchCts.Dispose();
                _searchCts = null;
            }
        }
    }
}
