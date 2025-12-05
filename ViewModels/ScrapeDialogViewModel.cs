using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class ScrapeDialogViewModel : ViewModelBase
{
    private readonly MetadataService _metadataService;
    private readonly MediaItem _targetItem;
    private readonly AppSettings _settings;

    [ObservableProperty] private string _searchQuery;
    [ObservableProperty] private ScraperConfig? _selectedScraper;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusMessage = ""; // Für Fehler/Infos
    // WICHTIG: NotifyCanExecuteChangedFor hinzufügen!
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private ScraperSearchResult? _selectedResult;
    
    public ScrapeDialogViewModel(MediaItem item, AppSettings settings, MetadataService metadataService)
    {
        _targetItem = item;
        _settings = settings;
        _metadataService = metadataService;

        // Standard-Suchbegriff: Bereinigter Titel
        SearchQuery = item.Title;

        // Verfügbare Scraper laden
        // Für Spiele nehmen wir IGDB, ScreenScraper etc. (Filtern nach Typ wäre hier gut, machen wir simple erstmal alle)
        foreach (var s in _settings.Scrapers)
        {
            AvailableScrapers.Add(s);
        }

        if (AvailableScrapers.Count > 0) SelectedScraper = AvailableScrapers[0];

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ApplyCommand = new RelayCommand(Apply, () => SelectedResult != null);
    }

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();
    public ObservableCollection<ScraperSearchResult> SearchResults { get; } = new();
    
    public IAsyncRelayCommand SearchCommand { get; }
    public IRelayCommand ApplyCommand { get; }

    public event Action<ScraperSearchResult>? OnResultSelected;

    private async Task SearchAsync()
    {
        if (SelectedScraper == null || string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching = true;
        SearchResults.Clear();
        SelectedResult = null;
        StatusMessage = ""; // Reset

        var provider = _metadataService.GetProvider(SelectedScraper.Id);
        if (provider == null)
        {
            StatusMessage = "Fehler: Dieser Dienst ist noch nicht implementiert.";
            IsSearching = false;
            return;
        }
        
        try
        {
            var results = await provider.SearchAsync(SearchQuery);
            if (results.Count == 0)
            {
                StatusMessage = "Keine Treffer gefunden.";
            }
            else
            {
                foreach (var res in results) SearchResults.Add(res);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Fehler bei der Suche: {ex.Message}";
        }
        
        IsSearching = false;
    }
    
    // Wird aufgerufen, wenn der User "Übernehmen" klickt
    private void Apply()
    {
        if (SelectedResult != null)
        {
            OnResultSelected?.Invoke(SelectedResult);
        }
    }
}