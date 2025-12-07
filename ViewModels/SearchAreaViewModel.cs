using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// Helper wrapper for search scopes (checkboxes in the filter UI).
/// </summary>
public class SearchScope : ObservableObject
{
    public MediaNode Node { get; }
    
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public SearchScope(MediaNode node)
    {
        Node = node;
    }
}

/// <summary>
/// ViewModel for the global search functionality.
/// Searches across selected scopes (platforms/folders).
/// </summary>
public partial class SearchAreaViewModel : ViewModelBase
{
    private const double DefaultItemWidth = 150.0;
    private readonly IEnumerable<MediaNode> _rootNodes;

    [ObservableProperty] 
    private string _searchText = string.Empty;
    
    [ObservableProperty] 
    private string _searchYear = string.Empty;
    
    [ObservableProperty] 
    private double _itemWidth = DefaultItemWidth;
    
    // Result collection optimized for bulk updates
    public RangeObservableCollection<MediaItem> SearchResults { get; } = new();

    // Required for the detail view on the right
    [ObservableProperty] 
    private MediaItem? _selectedMediaItem;

    // Filter scopes (platforms)
    public ObservableCollection<SearchScope> Scopes { get; } = new();
    
    // Commands
    public ICommand PlayRandomCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PlayCommand { get; } 
    
    public event Action<MediaItem>? RequestPlay;

    public SearchAreaViewModel(IEnumerable<MediaNode> rootNodes)
    {
        _rootNodes = rootNodes;

        InitializeScopes();

        // Initial empty search (clear results)
        ExecuteSearch();

        PlayRandomCommand = new RelayCommand(PlayRandom);
        
        PlayCommand = new RelayCommand<MediaItem>(item => 
        {
            if (item != null) RequestPlay?.Invoke(item);
        });
        
        // Triggered by Button or Enter key
        SearchCommand = new RelayCommand(ExecuteSearch);
    }
    
    private void InitializeScopes()
    {
        // Collect all direct children of root nodes as scopes
        foreach (var root in _rootNodes)
        {
            // If the root itself has items (rare, usually just a container), add it
            if (root.Items.Count > 0)
            {
                Scopes.Add(new SearchScope(root));
            }

            // Add all child groups (e.g. Consoles like "SNES", "Genesis")
            foreach (var child in root.Children)
            {
                Scopes.Add(new SearchScope(child));
            }
        }
    }

    private void ExecuteSearch()
    {
        // Which scopes are active?
        var activeScopes = Scopes.Where(s => s.IsSelected).Select(s => s.Node).ToList();
        
        // If query is empty, clear results and return (don't show ALL items globally, too much)
        if (string.IsNullOrWhiteSpace(SearchText) && string.IsNullOrWhiteSpace(SearchYear))
        {
            SearchResults.Clear();
            return; 
        }

        var resultsBuffer = new List<MediaItem>();

        foreach (var scope in activeScopes)
        {
            CollectMatchesRecursive(scope, resultsBuffer);
        }
        
        // Update UI in one go
        SearchResults.ReplaceAll(resultsBuffer);
    }

    private void CollectMatchesRecursive(MediaNode node, List<MediaItem> matches)
    {
        // Pre-parse year to avoid parsing for every item
        int? filterYear = null;
        if (!string.IsNullOrWhiteSpace(SearchYear) && int.TryParse(SearchYear, out int y))
        {
            filterYear = y;
        }

        foreach (var item in node.Items)
        {
            bool match = true;

            // 1. Text Filter
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                if (item.Title == null || !item.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    match = false;
            }

            // 2. Year Filter
            if (match && filterYear.HasValue)
            {
                if (item.ReleaseDate?.Year != filterYear.Value)
                    match = false;
            }

            if (match) matches.Add(item);
        }

        foreach (var child in node.Children)
        {
            CollectMatchesRecursive(child, matches);
        }
    }

    private void PlayRandom()
    {
        if (SearchResults.Count == 0) return;
        
        // Use Shared Random
        var index = Random.Shared.Next(SearchResults.Count);
        var item = SearchResults[index];
        
        SelectedMediaItem = item;
        RequestPlay?.Invoke(item);
    }

    public void OnDoubleClick()
    {
        if (SelectedMediaItem != null) RequestPlay?.Invoke(SelectedMediaItem);
    }
}