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
    
    public event Action? RequestClose;

    private async Task StartScraping()
    {
        if (SelectedScraper == null) return;

        IsWorking = true;
        _cts = new CancellationTokenSource();
        Log("Starte Bulk-Scrape mit " + SelectedScraper.Name);

        var provider = _metadataService.GetProvider(SelectedScraper.Id);
        if (provider == null)
        {
            Log("Fehler: Provider konnte nicht geladen werden.");
            IsWorking = false;
            return;
        }

        // Alle Items flach einsammeln
        var allItems = new System.Collections.Generic.List<MediaItem>();
        CollectItems(_rootNode, allItems);
        
        Log($"Gefunden: {allItems.Count} Medien.");
        ProgressValue = 0;

        double step = 100.0 / Math.Max(1, allItems.Count);

        try
        {
            for (int i = 0; i < allItems.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                var item = allItems[i];
                StatusText = $"Bearbeite: {item.Title} ({i + 1}/{allItems.Count})";
                
                // Nur suchen, wenn wir noch keine Daten haben? 
                // Oder immer überschreiben? Wir machen hier "Nur wenn Daten fehlen oder explizit gewünscht".
                // Fürs erste: Einfach machen.

                var results = await provider.SearchAsync(item.Title);

                // Einfache Heuristik: Wenn der Name fast exakt stimmt, nimm den ersten Treffer
                var match = results.FirstOrDefault(r => 
                    string.Equals(r.Title, item.Title, StringComparison.OrdinalIgnoreCase));

                // Fallback: Erster Treffer, wenn nichts exaktes
                if (match == null && results.Count > 0) match = results[0];

                if (match != null)
                {
                    Log($" -> Treffer: {match.Title}");
                    // KEINE direkte Zuweisung hier! Das macht der Callback im MainVM.
                    OnItemScraped?.Invoke(item, match);
                }
                else
                {
                    Log($" -> Nichts gefunden für {item.Title}");
                }

                ProgressValue = (i + 1) * step;
                
                // API schonen (Rate Limiting)
                await Task.Delay(250); 
            }
        }
        catch (Exception ex)
        {
            Log("Fehler: " + ex.Message);
        }
        finally
        {
            IsWorking = false;
            StatusText = "Fertig.";
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