using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Scrapers;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel handling the bulk scraping process for a collection of media items.
/// </summary>
public partial class BulkScrapeViewModel : ViewModelBase, IDisposable
{
    private const int MaxConcurrentRequests = 4;
    private const int RequestDelayMs = 250;
    
    private readonly MetadataService _metadataService;
    private readonly MediaNode _rootNode;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    // Buffer for logs to prevent UI flooding
    private readonly StringBuilder _logBuffer = new();
    private readonly Lock _logLock = new(); // .NET 9 Lock object

    [ObservableProperty] 
    private ScraperConfig? _selectedScraper;

    [ObservableProperty] 
    private bool _isBusy;

    [ObservableProperty] 
    private double _progressValue;

    [ObservableProperty] 
    private string _statusMessage = "Ready.";

    [ObservableProperty] 
    private string _logText = "";

    public BulkScrapeViewModel(MediaNode node, AppSettings settings, MetadataService metadataService)
    {
        _rootNode = node;
        _settings = settings;
        _metadataService = metadataService;

        InitializeScrapers();

        StartCommand = new AsyncRelayCommand(StartScrapingAsync);
        CancelCommand = new RelayCommand(CancelOperation);
    }

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // Event raised when an item has been successfully scraped/matched.
    public Func<MediaItem, ScraperSearchResult, Task>? OnItemScrapedAsync;

    private void InitializeScrapers()
    {
        AvailableScrapers.Clear();
        foreach (var scraper in _settings.Scrapers)
        {
            AvailableScrapers.Add(scraper);
        }

        if (AvailableScrapers.Count > 0)
        {
            SelectedScraper = AvailableScrapers[0];
        }
    }

    private async Task StartScrapingAsync()
    {
        if (_disposed)
            return;

        if (SelectedScraper == null) return;

        IsBusy = true;
        _cancellationTokenSource = new CancellationTokenSource();
        ClearLog();
        AppendLog($"Starting bulk scrape with {SelectedScraper.Name}...");

        IMetadataProvider? provider;
        try
        {
            provider = await _metadataService.GetProviderAsync(SelectedScraper.Id, _cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Scraping operation cancelled by user.");
            IsBusy = false;
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            return;
        }

        if (provider == null)
        {
            AppendLog("Error: Could not load provider.");
            IsBusy = false;
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
            return;
        }

        // 1. Collect Items
        // Using a HashSet or checking for duplicates might be useful here if one item is in multiple categories,
        // but for now, we assume tree nodes are unique instances.
        var allItems = new List<MediaItem>();
        await CollectItemsRecursiveSnapshotAsync(_rootNode, allItems);
        
        AppendLog($"Found {allItems.Count} media items in total.");
        ProgressValue = 0;

        // 2. Prepare Processing
        int processedCount = 0;
        int totalItems = Math.Max(1, allItems.Count);
        
        // ParallelOptions to control concurrency
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentRequests, 
            CancellationToken = _cancellationTokenSource.Token
        };

        // Timer to update the log UI periodically instead of constantly
        // This prevents the UI thread from freezing when processing thousands of items.
        using var logUpdateTimer = new Timer(_ => FlushLogBufferToUi(), null, 500, 500);

        try
        {
            await Parallel.ForEachAsync(allItems, parallelOptions, async (item, token) =>
            {
                // Logic: Searching
                try 
                {
                    // Only update status text every X items to save UI cycles, 
                    // or if it's really slow, update always. With 250ms delay, updating always is okayish,
                    // but let's stick to updating progress bar always and text sometimes.
                    if (processedCount % 5 == 0)
                    {
                        await UiThreadHelper.InvokeAsync(() =>
                        {
                            StatusMessage = $"Processing: {item.Title}";
                        });
                    }

                    var results = await provider.SearchAsync(item.Title, token);

                    // Heuristic: Exact match (Case Insensitive)
                    var match = results.FirstOrDefault(r => 
                        string.Equals(r.Title, item.Title, StringComparison.OrdinalIgnoreCase));

                    // Fallback: Take first if available
                    if (match == null && results.Count > 0) 
                    {
                        match = results[0];
                    }

                    if (match != null)
                    {
                        // Buffer log in worker thread (no UI)
                        AppendLogBuffer($"[MATCH] {item.Title} -> {match.Title}");

                        // Apply scraping result on UI thread (likely touches bound objects)
                        await UiThreadHelper.InvokeAsync(async () =>
                        {
                            if (OnItemScrapedAsync != null)
                                await OnItemScrapedAsync(item, match);
                        });
                    }
                    else
                    {
                        // Fail Case
                        AppendLogBuffer($"[MISS] No results for: {item.Title}");
                    }
                }
                catch (Exception ex)
                {
                     AppendLogBuffer($"[ERROR] {item.Title}: {ex.Message}");
                }

                // Update Progress
                // Interlocked is crucial here for thread safety without locking
                var current = Interlocked.Increment(ref processedCount);
                
                // Throttle progress updates: Update UI only every 1% or at least every 10 items
                // to avoid 30.000 Dispatcher calls.
                if (current % 10 == 0 || current == totalItems)
                {
                    double newVal = (double)current * 100 / totalItems;
                    await UiThreadHelper.InvokeAsync(() => ProgressValue = newVal);
                }

                // Politeness Delay
                await Task.Delay(RequestDelayMs, token);
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("Scraping operation cancelled by user.");
        }
        catch (Exception ex)
        {
            AppendLog($"Critical Error during batch processing: {ex.Message}");
        }
        finally
        {
            // Final Flush of logs
            FlushLogBufferToUi();
            
            IsBusy = false;
            StatusMessage = "Processing finished.";
            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private void CancelOperation()
    {
        if (_disposed)
            return;

        if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
            StatusMessage = "Stopping...";
            AppendLog("Cancellation requested...");
        }
    }

    /// <summary>
    /// Recursively collects all MediaItems from the node tree.
    /// </summary>
    private async Task CollectItemsRecursiveSnapshotAsync(MediaNode node, List<MediaItem> list)
    {
        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            items = node.Items.ToList();
            children = node.Children.ToList();
        });

        list.AddRange(items);

        foreach (var child in children)
        {
            await CollectItemsRecursiveSnapshotAsync(child, list);
        }
    }

    // --- High Performance Logging ---

    private void ClearLog()
    {
        lock (_logLock)
        {
            _logBuffer.Clear();
        }
        
        // ensure UI thread
        UiThreadHelper.Post(() => LogText = "");
    }

    /// <summary>
    /// Direct log update (use only for low-frequency messages like Start/End)
    /// </summary>
    private void AppendLog(string msg)
    {
        AppendLogBuffer(msg);
        FlushLogBufferToUi();
    }

    /// <summary>
    /// Buffered log update (safe to call from high-frequency loops)
    /// </summary>
    private void AppendLogBuffer(string msg)
    {
        lock (_logLock)
        {
            _logBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
    }

    /// <summary>
    /// Pushes the buffered logs to the UI property.
    /// Should be called via Timer or Dispatcher.
    /// </summary>
    private void FlushLogBufferToUi()
    {
        if (_disposed)
            return;

        string newLogChunk = "";
        lock (_logLock)
        {
            if (_logBuffer.Length == 0) return;
            newLogChunk = _logBuffer.ToString();
            _logBuffer.Clear();
        }

        if (string.IsNullOrEmpty(newLogChunk))
            return;

        UiThreadHelper.Post(() =>
        {
            // Consider limiting total log size here to avoid OOM with 30k items
            if (LogText.Length > 50000)
            {
                LogText = "..." + LogText.Substring(LogText.Length - 40000);
            }

            LogText += newLogChunk;
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();

            if (!IsBusy)
            {
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
        }
    }
}
