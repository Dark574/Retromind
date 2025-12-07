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
/// ViewModel representing the main content area where media items are displayed as a grid/list.
/// Handles filtering and user interaction (selection, playback).
/// </summary>
public partial class MediaAreaViewModel : ViewModelBase
{
    private const double DefaultItemWidth = 150.0;

    // We keep a private reference to ALL items to support filtering without losing data.
    private readonly List<MediaItem> _allItems;

    // Slider value for the tile size
    [ObservableProperty] 
    private double _itemWidth = DefaultItemWidth;

    [ObservableProperty] 
    private MediaNode _node;

    // The currently selected media item in the list
    [ObservableProperty] 
    private MediaItem? _selectedMediaItem;

    // Search query for filtering
    [ObservableProperty] 
    private string _searchText = string.Empty;

    // Optimized collection bound to the view
    // Using RangeObservableCollection prevents UI freezes during bulk updates.
    public RangeObservableCollection<MediaItem> FilteredItems { get; } = new();
    
    public MediaAreaViewModel(MediaNode node, double initialItemWidth)
    {
        Node = node;
        ItemWidth = initialItemWidth;

        // Copy items to a separate list for filtering logic
        _allItems = new List<MediaItem>(node.Items);
        
        // Initial population
        PopulateItems(_allItems);

        // Initialize Commands
        DoubleClickCommand = new RelayCommand(OnDoubleClick);
        PlayRandomCommand = new RelayCommand(PlayRandom);
    }

    public ICommand DoubleClickCommand { get; }
    public ICommand PlayRandomCommand { get; }

    // Event to request playback from the parent coordinator (MainWindowViewModel)
    public event Action<MediaItem>? RequestPlay;

    // Called automatically when SearchText changes
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // Filter is empty -> Show all
            if (FilteredItems.Count == _allItems.Count) return;
            
            PopulateItems(_allItems);
        }
        else
        {
            // Search (Case Insensitive)
            var matches = _allItems
                .Where(i => i.Title != null && i.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            PopulateItems(matches);
        }
    }

    /// <summary>
    /// Repopulates the collection efficiently using RangeObservableCollection.
    /// This triggers only ONE notification event for the UI, regardless of item count.
    /// </summary>
    private void PopulateItems(IEnumerable<MediaItem> items)
    {
        FilteredItems.ReplaceAll(items);
    }

    private void PlayRandom()
    {
        // Select only from VISIBLE (filtered) items
        if (FilteredItems.Count == 0) return;

        // Use Shared Random for better performance/randomness distribution
        var index = Random.Shared.Next(FilteredItems.Count);
        var randomItem = FilteredItems[index];

        // Visually select the item
        SelectedMediaItem = randomItem;

        // Request playback
        RequestPlay?.Invoke(randomItem);
    }

    private void OnDoubleClick()
    {
        if (SelectedMediaItem != null)
        {
            RequestPlay?.Invoke(SelectedMediaItem);
        }
    }
}