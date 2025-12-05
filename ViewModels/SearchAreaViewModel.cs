using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;

namespace Retromind.ViewModels;

// Hilfsklasse für die Checkboxen
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

public partial class SearchAreaViewModel : ViewModelBase
{
    private readonly IEnumerable<MediaNode> _rootNodes;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _searchYear = string.Empty;
    [ObservableProperty] private double _itemWidth = 150;
    
    // Das Ergebnis der Suche
    public ObservableCollection<MediaItem> SearchResults { get; } = new();

    // Damit die Detailansicht rechts funktioniert
    [ObservableProperty] private MediaItem? _selectedMediaItem;

    // Bereiche (Plattformen), die durchsucht werden sollen
    public ObservableCollection<SearchScope> Scopes { get; } = new();
    
    // Events / Commands
    public event Action<MediaItem>? RequestPlay;
    public ICommand PlayRandomCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PlayCommand { get; } 

    public SearchAreaViewModel(IEnumerable<MediaNode> rootNodes)
    {
        _rootNodes = rootNodes;

        // Wir sammeln alle direkten Kinder aller Root-Nodes ein.
        foreach (var root in _rootNodes)
        {
            // Falls der Root selbst Items hat, fügen wir ihn hinzu
            // (Das ist eher selten, meist ist Root nur Container)
            if (root.Items.Count > 0)
            {
                Scopes.Add(new SearchScope(root));
            }

            // Füge alle Kinder (Filme, Spiele, etc.) hinzu
            foreach (var child in root.Children)
            {
                Scopes.Add(new SearchScope(child));
            }
        }

        // Initial einmal leer suchen (zeigt ggf. nichts oder alles an)
        ExecuteSearch();

        PlayRandomCommand = new RelayCommand(PlayRandom);
        
        // Command initialisieren
        PlayCommand = new RelayCommand<MediaItem>(item => 
        {
            if (item != null) RequestPlay?.Invoke(item);
        });
        
        // SearchCommand wird z.B. beim Drücken von Enter oder Button getriggert
        SearchCommand = new RelayCommand(ExecuteSearch);
    }
    
    // Wenn sich Filter ändern, können wir automatisch suchen oder warten (hier warten wir auf Enter/Button für Performance)
    // Alternativ: partial void OnSearchTextChanged(string value) => ExecuteSearch(); 

    private void ExecuteSearch()
    {
        SearchResults.Clear();
        
        // Welche Bereiche sind aktiv?
        var activeScopes = Scopes.Where(s => s.IsSelected).Select(s => s.Node).ToList();
        
        // Wenn keine Filter gesetzt sind, zeigen wir nichts an (oder alles? Besser nichts bei globaler Suche)
        if (string.IsNullOrWhiteSpace(SearchText) && string.IsNullOrWhiteSpace(SearchYear))
        {
            return; 
        }

        foreach (var scope in activeScopes)
        {
            SearchRecursive(scope);
        }
    }

    private void SearchRecursive(MediaNode node)
    {
        foreach (var item in node.Items)
        {
            bool match = true;

            // 1. Text Filter
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                if (!item.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
                    match = false;
            }

            // 2. Jahr Filter
            if (match && !string.IsNullOrWhiteSpace(SearchYear))
            {
                if (!int.TryParse(SearchYear, out int year) || item.ReleaseDate?.Year != year)
                    match = false;
            }

            if (match) SearchResults.Add(item);
        }

        foreach (var child in node.Children)
        {
            SearchRecursive(child);
        }
    }

    private void PlayRandom()
    {
        if (SearchResults.Count == 0) return;
        var rnd = new Random();
        var item = SearchResults[rnd.Next(SearchResults.Count)];
        SelectedMediaItem = item;
        RequestPlay?.Invoke(item);
    }

    public void OnDoubleClick()
    {
        if (SelectedMediaItem != null) RequestPlay?.Invoke(SelectedMediaItem);
    }
}