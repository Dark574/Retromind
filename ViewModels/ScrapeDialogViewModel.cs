using System;
using System.Collections.Generic;
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
using Retromind.Services.Scrapers;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel handling the manual scraping dialog for a single item.
/// Allows the user to select a scraper service, enter a query, and pick a result.
/// </summary>
public partial class ScrapeDialogViewModel : ViewModelBase, IDisposable
{
    private const int MaxResults = 40;

    private readonly MetadataService _metadataService;
    private readonly MediaItem _targetItem;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _previewEnrichmentCts;
    private Task _previewEnrichmentTask = Task.CompletedTask;
    private readonly HashSet<ScraperSearchResult> _enrichedResults = new();
    private bool _disposed;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ScraperConfig? _selectedScraper;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isPreviewBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private ScraperSearchResult? _selectedResult;

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();

    // Bulk-update friendly collection (prevents UI stalls when a provider returns many results).
    public RangeObservableCollection<ScraperSearchResult> SearchResults { get; } = new();

    public string NoCoverText =>
        Strings.ResourceManager.GetString("Metadata.NoCover", Strings.Culture) ?? "No cover";

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
        foreach (var s in _settings.Scrapers.Where(s => s.Type != ScraperType.None && s.Type != ScraperType.EmuMovies))
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
        _enrichedResults.Clear();
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

            var limited = results.Take(MaxResults).ToList();
            if (provider is IMetadataSearchPreviewEnricher previewEnricher)
                await previewEnricher.EnrichPreviewsAsync(limited, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
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

    partial void OnSelectedScraperChanged(ScraperConfig? value)
    {
        CancelPreviewEnrichment();
        _enrichedResults.Clear();
        IsPreviewBusy = false;
    }

    partial void OnSelectedResultChanged(ScraperSearchResult? value)
    {
        CancelPreviewEnrichment();
        IsPreviewBusy = false;

        if (_disposed ||
            value == null ||
            SelectedScraper == null ||
            _enrichedResults.Contains(value))
        {
            return;
        }

        if (_metadataService.GetProvider(SelectedScraper.Id) is not IMetadataResultEnricher)
            return;

        StatusMessage = string.Empty;
        var cts = new CancellationTokenSource();
        _previewEnrichmentCts = cts;
        IsPreviewBusy = true;
        _previewEnrichmentTask = EnrichSelectedPreviewAsync(value, SelectedScraper.Id, cts);
    }

    private async Task EnrichSelectedPreviewAsync(
        ScraperSearchResult result,
        string scraperId,
        CancellationTokenSource cts)
    {
        try
        {
            var provider = await _metadataService.GetProviderAsync(scraperId, cts.Token);
            if (provider is not IMetadataResultEnricher enricher)
                return;

            await enricher.EnrichAsync(result, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            if (!_disposed &&
                ReferenceEquals(_previewEnrichmentCts, cts) &&
                ReferenceEquals(SelectedResult, result) &&
                string.Equals(SelectedScraper?.Id, scraperId, StringComparison.Ordinal))
            {
                _enrichedResults.Add(result);

                // ScraperSearchResult is a lightweight DTO rather than an observable
                // model. Notify the parent binding so nested image URLs are reevaluated.
                OnPropertyChanged(nameof(SelectedResult));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the user quickly selects another result.
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_previewEnrichmentCts, cts))
                StatusMessage = string.Format(Strings.Metadata_Search_FailedFormat, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_previewEnrichmentCts, cts))
            {
                _previewEnrichmentCts = null;
                IsPreviewBusy = false;
            }

            cts.Dispose();
        }
    }

    private void CancelPreviewEnrichment()
    {
        var cts = _previewEnrichmentCts;
        _previewEnrichmentCts = null;
        cts?.Cancel();
    }

    private async Task StopPreviewEnrichmentAsync()
    {
        var pendingTask = _previewEnrichmentTask;
        CancelPreviewEnrichment();

        try
        {
            await pendingTask;
        }
        catch (OperationCanceledException)
        {
            // Expected while switching from preview loading to apply.
        }
        finally
        {
            _previewEnrichmentTask = Task.CompletedTask;
            IsPreviewBusy = false;
        }
    }

    private async Task ApplyAsync()
    {
        if (_disposed || SelectedResult == null)
            return;

        var handler = OnResultSelectedAsync;
        if (handler == null)
            return;

        IsBusy = true;
        StatusMessage = string.Empty;

        try
        {
            var selectedResult = SelectedResult;
            await StopPreviewEnrichmentAsync();

            if (SelectedScraper != null)
            {
                var provider = await _metadataService.GetProviderAsync(SelectedScraper.Id);
                if (provider is IMetadataResultEnricher enricher &&
                    !_enrichedResults.Contains(selectedResult))
                {
                    await enricher.EnrichAsync(selectedResult);
                    _enrichedResults.Add(selectedResult);
                }
            }

            foreach (var subscriber in handler.GetInvocationList())
            {
                if (subscriber is Func<ScraperSearchResult, Task> callback)
                    await callback(selectedResult);
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing or a provider operation was cancelled.
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(Strings.Metadata_Search_FailedFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelPreviewEnrichment();

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
