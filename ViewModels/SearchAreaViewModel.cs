using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
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
        Node = node ?? throw new ArgumentNullException(nameof(node));
    }
}

/// <summary>
/// ViewModel for the global search functionality.
/// Optimized for large libraries (30k+ items):
/// - debounced search input
/// - background evaluation
/// - single UI update (ReplaceAll)
/// </summary>
public partial class SearchAreaViewModel : ViewModelBase
{
    private const double DefaultItemWidth = 150.0;

    // Debounce typing/checkbox toggles to avoid re-scanning the entire library per keystroke.
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<MediaNode> _rootNodes;

    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _searchYear = string.Empty;

    [ObservableProperty]
    private double _itemWidth = DefaultItemWidth;

    /// <summary>
    /// Result collection optimized for bulk updates.
    /// </summary>
    public RangeObservableCollection<MediaItem> SearchResults { get; } = new();

    [ObservableProperty]
    private MediaItem? _selectedMediaItem;

    public ObservableCollection<SearchScope> Scopes { get; } = new();

    public ICommand PlayRandomCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PlayCommand { get; }

    public event Action<MediaItem>? RequestPlay;
    
    public bool HasResults => SearchResults.Count > 0;
    public bool HasNoResults => SearchResults.Count == 0;

    public SearchAreaViewModel(IEnumerable<MediaNode> rootNodes)
    {
        _rootNodes = (rootNodes ?? throw new ArgumentNullException(nameof(rootNodes))).ToList();

        InitializeScopes();

        // Keep the initial state consistent (empty results until user enters criteria).
        SearchResults.Clear();

        PlayRandomCommand = new RelayCommand(PlayRandom, () => SearchResults.Count > 0);
        SearchCommand = new RelayCommand(RequestSearch);

        PlayCommand = new RelayCommand<MediaItem>(item =>
        {
            if (item != null) RequestPlay?.Invoke(item);
        });
    }

    partial void OnSearchTextChanged(string value) => RequestSearch();
    partial void OnSearchYearChanged(string value) => RequestSearch();

    private void InitializeScopes()
    {
        Scopes.Clear();

        // We keep the scope list shallow (root + direct children) for UI simplicity.
        // The actual item search is still recursive within each selected scope.
        foreach (var root in _rootNodes)
        {
            if (root.Items.Count > 0)
                AddScope(root);

            foreach (var child in root.Children)
                AddScope(child);
        }

        void AddScope(MediaNode node)
        {
            var scope = new SearchScope(node);
            scope.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SearchScope.IsSelected))
                    RequestSearch();
            };
            Scopes.Add(scope);
        }
    }

    private void RequestSearch()
    {
        // Cancel previous pending search (debounce + background evaluation).
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();

        var token = _searchCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                await ExecuteSearchAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the user keeps typing or changes scopes quickly.
            }
        }, token);
    }

    private async Task ExecuteSearchAsync(CancellationToken token)
    {
        var query = SearchText?.Trim();
        var yearText = SearchYear?.Trim();

        // If both filters are empty, do not show the entire library (too expensive/noisy).
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(yearText))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SearchResults.Clear();
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(HasNoResults));
                (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
            return;
        }

        int? filterYear = null;
        if (!string.IsNullOrWhiteSpace(yearText) && int.TryParse(yearText, out var parsedYear))
            filterYear = parsedYear;

        // Snapshot active scopes (avoid enumerating ObservableCollection from background threads).
        var activeScopes = new List<MediaNode>(capacity: Scopes.Count);
        for (int i = 0; i < Scopes.Count; i++)
        {
            if (Scopes[i].IsSelected)
                activeScopes.Add(Scopes[i].Node);
        }

        var results = new List<MediaItem>(capacity: 256);

        // Evaluate in background; only UI assignment is marshaled.
        for (int i = 0; i < activeScopes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            CollectMatchesRecursive(activeScopes[i], results, query, filterYear, token);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SearchResults.ReplaceAll(results);

            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasNoResults));
            
            // Keep "Play random" enabled state accurate.
            (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
        });
    }

    private static void CollectMatchesRecursive(
        MediaNode node,
        List<MediaItem> matches,
        string? query,
        int? filterYear,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // Scan items in this node.
        var items = node.Items;
        for (int i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var item = items[i];

            if (!Matches(item, query, filterYear))
                continue;

            matches.Add(item);
        }

        // Recurse into child nodes.
        var children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
            CollectMatchesRecursive(children[i], matches, query, filterYear, token);
        }
    }

    private static bool Matches(MediaItem item, string? query, int? filterYear)
    {
        // 1) Text filter
        if (!string.IsNullOrWhiteSpace(query))
        {
            var title = item.Title;
            if (string.IsNullOrEmpty(title) || !title.Contains(query, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 2) Year filter
        if (filterYear.HasValue)
        {
            if (item.ReleaseDate?.Year != filterYear.Value)
                return false;
        }

        return true;
    }

    private void PlayRandom()
    {
        if (SearchResults.Count == 0) return;

        var index = Random.Shared.Next(SearchResults.Count);
        var item = SearchResults[index];

        SelectedMediaItem = item;
        RequestPlay?.Invoke(item);
    }

    public void OnDoubleClick()
    {
        if (SelectedMediaItem != null)
            RequestPlay?.Invoke(SelectedMediaItem);
    }
}