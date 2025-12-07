using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel handling the manual scraping dialog for a single item.
/// Allows the user to select a scraper service, enter a query, and pick a result.
/// </summary>
public partial class ScrapeDialogViewModel : ViewModelBase
{
    private readonly MetadataService _metadataService;
    private readonly MediaItem _targetItem;
    private readonly AppSettings _settings;

    [ObservableProperty] 
    private string _searchQuery = string.Empty;

    [ObservableProperty] 
    private ScraperConfig? _selectedScraper;

    [ObservableProperty] 
    private bool _isBusy;

    [ObservableProperty] 
    private string _statusMessage = "";

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private ScraperSearchResult? _selectedResult;
    
    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();
    
    // We bind results directly. Ensure ScraperSearchResult has properties that the View can display (Title, Description, ImageUrl)
    public ObservableCollection<ScraperSearchResult> SearchResults { get; } = new();
    
    public IAsyncRelayCommand SearchCommand { get; }
    public IRelayCommand ApplyCommand { get; }

    public event Action<ScraperSearchResult>? OnResultSelected;

    public ScrapeDialogViewModel(MediaItem item, AppSettings settings, MetadataService metadataService)
    {
        _targetItem = item ?? throw new ArgumentNullException(nameof(item));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));

        InitializeData();

        SearchCommand = new AsyncRelayCommand(SearchAsync);
        ApplyCommand = new RelayCommand(Apply, () => SelectedResult != null);
    }

    private void InitializeData()
    {
        // Default query is the cleaned title
        SearchQuery = _targetItem.Title ?? string.Empty;

        // Load available scrapers
        // Future improvement: Filter by media type (Game, Movie, etc.) if ScraperConfig supports it
        foreach (var s in _settings.Scrapers)
        {
            AvailableScrapers.Add(s);
        }

        if (AvailableScrapers.Count > 0) 
        {
            SelectedScraper = AvailableScrapers[0];
        }
    }

    private async Task SearchAsync()
    {
        if (SelectedScraper == null || string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsBusy = true;
        SearchResults.Clear();
        SelectedResult = null;
        StatusMessage = string.Empty;

        var provider = _metadataService.GetProvider(SelectedScraper.Id);
        if (provider == null)
        {
            StatusMessage = "Error: Service provider not found or not implemented.";
            IsBusy = false;
            return;
        }
        
        try
        {
            var results = await provider.SearchAsync(SearchQuery);
            
            if (results.Count == 0)
            {
                StatusMessage = "No results found.";
            }
            else
            {
                foreach (var res in results) 
                {
                    SearchResults.Add(res);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally 
        {
            IsBusy = false;
        }
    }
    
    private void Apply()
    {
        if (SelectedResult != null)
        {
            OnResultSelected?.Invoke(SelectedResult);
        }
    }
}