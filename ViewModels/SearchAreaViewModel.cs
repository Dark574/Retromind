using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// When true, only items marked as favorites are included in the global search results
    /// </summary>
    [ObservableProperty]
    private bool _onlyFavorites;
    
    /// <summary>
    /// Optional status filter. When null, status is ignored
    /// When set, only items with the given PlayStatus are included
    /// </summary>
    [ObservableProperty]
    private PlayStatus? _selectedStatus;

    /// <summary>
    /// Available status values for the status filter combo box
    /// </summary>
    public IReadOnlyList<StatusFilterOption> StatusOptions { get; } = StatusFilterOption.CreateDefault();

    [ObservableProperty]
    private StatusFilterOption? _selectedStatusOption;

    /// <summary>
    /// Result collection optimized for bulk updates
    /// </summary>
    public RangeObservableCollection<MediaItem> SearchResults { get; } = new();

    [ObservableProperty]
    private MediaItem? _selectedMediaItem;

    public ObservableCollection<SearchScope> Scopes { get; } = new();

    public ICommand PlayRandomCommand { get; }
    public ICommand PlayCommand { get; }

    /// <summary>
    /// Command to reset all filters to defaults.
    /// </summary>
    public IRelayCommand ResetFiltersCommand { get; }
    
    public event Action<MediaItem>? RequestPlay;
    
    public bool HasResults => SearchResults.Count > 0;
    public bool HasNoResults => SearchResults.Count == 0;

    public SearchAreaViewModel(IEnumerable<MediaNode> rootNodes)
    {
        _rootNodes = (rootNodes ?? throw new ArgumentNullException(nameof(rootNodes))).ToList();

        InitializeScopes();

        // Keep the initial state consistent (empty results until user enters criteria)
        SearchResults.Clear();

        PlayRandomCommand = new RelayCommand(PlayRandom, () => SearchResults.Count > 0);

        PlayCommand = new RelayCommand<MediaItem>(item =>
        {
            if (item != null) RequestPlay?.Invoke(item);
        });
        
        // Allow resetting all filters back to defaults
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        SelectedStatusOption = StatusOptions[0];
    }

    partial void OnSearchTextChanged(string value) => RequestSearch();
    partial void OnSearchYearChanged(string value) => RequestSearch();
    partial void OnOnlyFavoritesChanged(bool value) => RequestSearch();
    partial void OnSelectedStatusChanged(PlayStatus? value)
    {
        var option = StatusOptions.FirstOrDefault(o => o.Value == value) ?? StatusOptions[0];
        if (!ReferenceEquals(SelectedStatusOption, option))
            SelectedStatusOption = option;

        RequestSearch();
    }
    partial void OnSelectedStatusOptionChanged(StatusFilterOption? value)
    {
        if (SelectedStatus != value?.Value)
            SelectedStatus = value?.Value;
    }

    private void InitializeScopes()
    {
        Scopes.Clear();

        // We keep the scope list shallow (root + direct children) for UI simplicity
        // The actual item search is still recursive within each selected scope
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
        // Cancel previous pending search (debounce + background evaluation)
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
                // Expected when the user keeps typing or changes scopes quickly
            }
        }, token);
    }

    private async Task ExecuteSearchAsync(CancellationToken token)
    {
        var query = SearchText?.Trim();
        var yearText = SearchYear?.Trim();
        var favoritesOnly = OnlyFavorites;
        var statusFilter = SelectedStatus;

        // If all filters are empty and "favorites only" is off, do not show the entire library
        if (string.IsNullOrWhiteSpace(query) &&
            string.IsNullOrWhiteSpace(yearText) &&
            !favoritesOnly &&
            statusFilter == null)
        {
            await UiThreadHelper.InvokeAsync(() =>
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

        // Snapshot active scopes on the UI thread (avoid cross-thread collection access).
        var activeScopes = new List<MediaNode>();
        await UiThreadHelper.InvokeAsync(() =>
        {
            for (int i = 0; i < Scopes.Count; i++)
            {
                if (Scopes[i].IsSelected)
                    activeScopes.Add(Scopes[i].Node);
            }
        });

        var results = new List<MediaItem>(capacity: 256);

        // Evaluate in background; only UI assignment is marshaled
        for (int i = 0; i < activeScopes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            await CollectMatchesRecursiveAsync(activeScopes[i], results, query, filterYear, favoritesOnly, statusFilter, token);
        }

        // Global sort across all scopes, same as main MediaArea view:
        // alphabetical by Title (case-insensitive)
        results.Sort(static (a, b) =>
            string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        
        await UiThreadHelper.InvokeAsync(() =>
        {
            SearchResults.ReplaceAll(results);

            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasNoResults));

            // Keep "Play random" enabled state accurate
            (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
        });
    }

    private static async Task CollectMatchesRecursiveAsync(
        MediaNode node,
        List<MediaItem> matches,
        string? query,
        int? filterYear,
        bool favoritesOnly,
        PlayStatus? statusFilter,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // Scan items in this node
        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            items = node.Items.ToList();
            children = node.Children.ToList();
        });

        for (int i = 0; i < items.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var item = items[i];

            if (!Matches(item, query, filterYear, favoritesOnly, statusFilter))
                continue;

            matches.Add(item);
        }

        // Recurse into child nodes.
        for (int i = 0; i < children.Count; i++)
        {
            await CollectMatchesRecursiveAsync(children[i], matches, query, filterYear, favoritesOnly, statusFilter, token);
        }
    }

    private static bool Matches(
        MediaItem item,
        string? query,
        int? filterYear,
        bool favoritesOnly,
        PlayStatus? statusFilter)
    {
        // 1) Favorites filter
        if (favoritesOnly && !item.IsFavorite)
            return false;

        // 2) Status filter
        if (statusFilter.HasValue && item.Status != statusFilter.Value)
            return false;

        // 3) Text filter
        if (!string.IsNullOrWhiteSpace(query))
        {
            var title = item.Title;
            if (string.IsNullOrEmpty(title) || !title.Contains(query, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 4) Year filter
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

    private void ResetFilters()
    {
        SearchText = string.Empty;
        SearchYear = string.Empty;
        OnlyFavorites = false;
        SelectedStatusOption = StatusOptions[0];

        for (int i = 0; i < Scopes.Count; i++)
        {
            if (!Scopes[i].IsSelected)
                Scopes[i].IsSelected = true;
        }
    }
}
