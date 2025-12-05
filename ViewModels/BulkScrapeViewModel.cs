using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class BulkScrapeViewModel : ViewModelBase
{
    private readonly MetadataService _metadataService;
    private readonly MediaNode _rootNode;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private ScraperConfig? _selectedScraper;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusText = "Bereit.";
    [ObservableProperty] private string _logText = "";

    public BulkScrapeViewModel(MediaNode node, AppSettings settings, MetadataService metadataService)
    {
        _rootNode = node;
        _settings = settings;
        _metadataService = metadataService;

        foreach (var s in _settings.Scrapers) AvailableScrapers.Add(s);
        if (AvailableScrapers.Count > 0) SelectedScraper = AvailableScrapers[0];

        StartCommand = new AsyncRelayCommand(StartScraping);
        CancelCommand = new RelayCommand(Cancel);
    }

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }
    
    private async Task StartScraping()
    {
        if (SelectedScraper == null) return;

        IsWorking = true;
        _cts = new CancellationTokenSource();
        Log("Starting bulk scrape with " + SelectedScraper.Name);

        var provider = _metadataService.GetProvider(SelectedScraper.Id);
        if (provider == null)
        {
            Log("Error: Could not load provider.");
            IsWorking = false;
            return;
        }

        // Collect all items flat
        var allItems = new System.Collections.Generic.List<MediaItem>();
        CollectItems(_rootNode, allItems);
        
        Log($"Found: {allItems.Count} media items.");
        ProgressValue = 0;

        // Use atomic counter for thread-safe progress tracking
        int processedCount = 0;
        double step = 100.0 / Math.Max(1, allItems.Count);

        // Run in parallel but limit concurrency to be polite to APIs (e.g. 4 concurrent requests)
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4, 
            CancellationToken = _cts.Token
        };
        
        try
        {
            await Parallel.ForEachAsync(allItems, parallelOptions, async (item, token) =>
            {
                // Update Status (UI Thread)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    StatusText = $"Processing: {item.Title}";
                });

                try 
                {
                    var results = await provider.SearchAsync(item.Title);

                    // Simple heuristic: If the name matches almost exactly, take the first hit
                    var match = results.FirstOrDefault(r => 
                        string.Equals(r.Title, item.Title, StringComparison.OrdinalIgnoreCase));

                    // Fallback: First hit if nothing exact
                    if (match == null && results.Count > 0) match = results[0];

                    if (match != null)
                    {
                        // Invoke changes on UI thread to be safe (modifies ObservableObjects/UI)
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            Log($" -> Match: {match.Title}");
                            OnItemScraped?.Invoke(item, match);
                        });
                    }
                    else
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Log($" -> Nothing found for {item.Title}"));
                    }
                }
                catch (Exception ex)
                {
                     await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Log($"Error processing {item.Title}: {ex.Message}"));
                }

                // Update Progress (Thread-safe)
                var current = Interlocked.Increment(ref processedCount);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ProgressValue = current * step);

                // Small dynamic delay per task to prevent burst-banning (politeness delay)
                await Task.Delay(250, token);
            });
        }
        catch (OperationCanceledException)
        {
            Log("Scraping cancelled.");
        }
        catch (Exception ex)
        {
            Log("Critical Error: " + ex.Message);
        }
        finally
        {
            IsWorking = false;
            StatusText = "Done.";
        }
    }

    public Action<MediaItem, ScraperSearchResult>? OnItemScraped;

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Abbruch angefordert...";
    }

    private void CollectItems(MediaNode node, System.Collections.Generic.List<MediaItem> list)
    {
        list.AddRange(node.Items);
        foreach (var child in node.Children) CollectItems(child, list);
    }

    private void Log(string msg)
    {
        LogText += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
    }
}