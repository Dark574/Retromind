using System;
using System.Collections.Generic; // Für List<T>
using System.Collections.ObjectModel; // Für ObservableCollection
using System.Linq; // Für LINQ (Where, ToList)
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

namespace Retromind.ViewModels;

// Erbt jetzt von ViewModelBase (oder ObservableObject), damit Bindings funktionieren
public partial class MediaAreaViewModel : ViewModelBase
{
    // Wir halten eine private Referenz auf ALLE Items, um filtern zu können
    private readonly List<MediaItem> _allItems;

    // Slider-Wert für die Kachelgröße (Standard 150)
    [ObservableProperty] private double _itemWidth = 150;

    [ObservableProperty] private MediaNode _node;

    // Das aktuell ausgewählte Spiel/Medium in der Liste
    [ObservableProperty] private MediaItem? _selectedMediaItem;

    // Suchtext für den Filter
    [ObservableProperty] 
    private string _searchText = string.Empty;

    // Die gefilterte Liste, die tatsächlich angezeigt wird
    public ObservableCollection<MediaItem> FilteredItems { get; } = new();

    // Dieser Konstruktor wird aufgerufen!
    public MediaAreaViewModel(MediaNode node, double initialItemWidth)
    {
        Node = node;
        ItemWidth = initialItemWidth;

        // Wir kopieren die Items in eine separate Liste für die Filter-Logik
        _allItems = new List<MediaItem>(node.Items);
        
        // Initial alle anzeigen
        ApplyFilter();

        // Command initialisieren
        DoubleClickCommand = new RelayCommand(OnDoubleClick);
        PlayRandomCommand = new RelayCommand(PlayRandom);
    }

    // Command, das von der View (Doppelklick) aufgerufen wird
    public ICommand DoubleClickCommand { get; }
    public ICommand PlayRandomCommand { get; }

    // Event, um dem Parent (MainWindowViewModel) zu sagen: "Start das Ding!"
    public event Action<MediaItem>? RequestPlay;

    // Wird automatisch aufgerufen, wenn sich SearchText ändert (Dank MVVM Toolkit)
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        FilteredItems.Clear();

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            // Filter leer -> Alle anzeigen
            foreach (var item in _allItems)
            {
                FilteredItems.Add(item);
            }
        }
        else
        {
            // Suchen (Case Insensitive)
            var matches = _allItems
                .Where(i => i.Title != null && i.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            foreach (var item in matches)
            {
                FilteredItems.Add(item);
            }
        }
    }

    private void PlayRandom()
    {
        // Wir wählen nur aus den SICHTBAREN (gefilterten) Items
        if (FilteredItems.Count == 0) return;

        var rnd = new Random();
        var index = rnd.Next(FilteredItems.Count);
        var randomItem = FilteredItems[index];

        // Item auswählen (optisch)
        SelectedMediaItem = randomItem;

        // Und starten
        RequestPlay?.Invoke(randomItem);
    }

    private void OnDoubleClick()
    {
        if (SelectedMediaItem != null)
            // Feuere das Event -> MainWindowViewModel fängt das und startet den Launcher
            RequestPlay?.Invoke(SelectedMediaItem);
    }
}