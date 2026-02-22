using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for the global search functionality.
/// Optimized for large libraries (30k+ items):
/// - debounced search input
/// - background evaluation
/// - single UI update (ReplaceAll)
/// </summary>
public partial class SearchAreaViewModel : ViewModelBase, IDisposable
{
    private const double DefaultItemWidth = 150.0;
    private const double ItemSpacing = 0.0;
    private const double ViewportPadding = 0.0;

    private int _columnCount = 1;

    // Debounce typing/checkbox toggles to avoid re-scanning the entire library per keystroke.
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(250);

    private readonly IReadOnlyList<MediaNode> _rootNodesSnapshot;
    private readonly ObservableCollection<MediaNode>? _rootNodesObservable;
    private readonly HashSet<MediaNode> _trackedRootNodes = new();
    private readonly HashSet<string> _selectedScopeNodeIds = new(StringComparer.Ordinal);
    private bool _hasExplicitScopeSelection;

    private CancellationTokenSource? _searchCts;
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _searchYear = string.Empty;

    [ObservableProperty]
    private double _itemWidth = DefaultItemWidth;

    [ObservableProperty]
    private double _effectiveItemWidth = DefaultItemWidth;

    [ObservableProperty]
    private double _viewportWidth;

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

    public sealed class MediaItemRow
    {
        public IReadOnlyList<MediaItem> Items { get; }

        public MediaItemRow(IReadOnlyList<MediaItem> items)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
        }
    }

    /// <summary>
    /// Row grouping for a virtualized tile view.
    /// </summary>
    public RangeObservableCollection<MediaItemRow> ItemRows { get; } = new();

    public int ColumnCount => _columnCount;

    [ObservableProperty]
    private MediaItem? _selectedMediaItem;

    public int SelectedScopeCount => CountSelectedNodes();

    public string SelectedScopeSummary =>
        SelectedScopeCount == 0
            ? Strings.Search_ScopesNoneSelected
            : string.Format(Strings.Search_ScopesSelectedFormat, SelectedScopeCount);

    public ICommand PlayRandomCommand { get; }
    public ICommand PlayCommand { get; }

    /// <summary>
    /// Command to reset all filters to defaults.
    /// </summary>
    public IRelayCommand ResetFiltersCommand { get; }
    
    public event Action<MediaItem>? RequestPlay;
    
    public bool HasResults => SearchResults.Count > 0;
    public bool HasNoResults => SearchResults.Count == 0;
    public bool ShowNoResults => HasNoResults && HasAnyRoots;
    public bool ShowEmptyLibraryHint => !HasAnyRoots;

    [ObservableProperty]
    private bool _hasAnyRoots;

    public SearchAreaViewModel(IEnumerable<MediaNode> rootNodes)
    {
        if (rootNodes == null)
            throw new ArgumentNullException(nameof(rootNodes));

        _rootNodesSnapshot = rootNodes.ToList();
        _rootNodesObservable = rootNodes as ObservableCollection<MediaNode>;
        if (_rootNodesObservable != null)
        {
            _rootNodesObservable.CollectionChanged += OnRootNodesChanged;
            foreach (var node in _rootNodesObservable)
                TrackRootNode(node);
        }

        EnsureDefaultScopeSelection();
        UpdateRootAvailability();

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

    partial void OnItemWidthChanged(double value) => RebuildRows();

    partial void OnViewportWidthChanged(double value) => RebuildRows();

    private IEnumerable<MediaNode> RootNodes => _rootNodesObservable ?? _rootNodesSnapshot;

    private void UpdateRootAvailability()
    {
        var hasRoots = RootNodes.Any();
        if (hasRoots != HasAnyRoots)
        {
            HasAnyRoots = hasRoots;
            OnPropertyChanged(nameof(ShowNoResults));
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
        }
    }

    public IReadOnlyList<MediaNode> RootNodesSnapshot => RootNodes.ToList();

    public HashSet<string> GetSelectedScopeIdsSnapshot()
        => new(_selectedScopeNodeIds, StringComparer.Ordinal);

    public void ApplyScopeSelection(IReadOnlyCollection<string> selectedIds)
    {
        _selectedScopeNodeIds.Clear();
        foreach (var id in selectedIds)
            _selectedScopeNodeIds.Add(id);

        _hasExplicitScopeSelection = true;
        NotifyScopeSummaryChanged();
        RequestSearch();
    }

    private void TrackRootNode(MediaNode node)
    {
        if (!_trackedRootNodes.Add(node))
            return;

        node.Children.CollectionChanged += OnRootChildrenChanged;
        node.Items.CollectionChanged += OnRootItemsChanged;
    }

    private void UntrackRootNode(MediaNode node)
    {
        if (!_trackedRootNodes.Remove(node))
            return;

        node.Children.CollectionChanged -= OnRootChildrenChanged;
        node.Items.CollectionChanged -= OnRootItemsChanged;
    }

    private void OnRootNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(() => OnRootNodesChanged(sender, e));
            return;
        }

        if (e.OldItems != null)
        {
            foreach (var oldItem in e.OldItems.OfType<MediaNode>())
                UntrackRootNode(oldItem);
        }

        if (e.NewItems != null)
        {
            foreach (var newItem in e.NewItems.OfType<MediaNode>())
                TrackRootNode(newItem);
        }

        RefreshScopesAndSearch();
    }

    private void OnRootChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(() => OnRootChildrenChanged(sender, e));
            return;
        }

        RefreshScopesAndSearch();
    }

    private void OnRootItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(() => OnRootItemsChanged(sender, e));
            return;
        }

        RefreshScopesAndSearch();
    }

    private void RefreshScopesAndSearch()
    {
        if (_disposed)
            return;

        UpdateRootAvailability();
        SyncScopeSelection();

        if (!string.IsNullOrWhiteSpace(SearchText) ||
            !string.IsNullOrWhiteSpace(SearchYear) ||
            OnlyFavorites ||
            SelectedStatus != null)
        {
            RequestSearch();
        }
    }

    private void RequestSearch()
    {
        if (_disposed)
            return;

        // Cancel previous pending search (debounce + background evaluation)
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                await ExecuteSearchAsync(token, cts).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the user keeps typing or changes scopes quickly
            }
        }, token);
    }

    private async Task ExecuteSearchAsync(CancellationToken token, CancellationTokenSource cts)
    {
        if (_disposed)
            return;

        var query = SearchText?.Trim();
        var yearText = SearchYear?.Trim();
        var favoritesOnly = OnlyFavorites;
        var statusFilter = SelectedStatus;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var hasYearText = !string.IsNullOrWhiteSpace(yearText);
        var parsedYear = 0;
        var hasValidYear = hasYearText && int.TryParse(yearText, out parsedYear);

        // If all filters are empty and "favorites only" is off, do not show the entire library
        if (!hasQuery &&
            !hasValidYear &&
            !favoritesOnly &&
            statusFilter == null)
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_searchCts, cts) || token.IsCancellationRequested)
                    return;

                SearchResults.Clear();
                ItemRows.Clear();
                SelectedMediaItem = null;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(HasNoResults));
                OnPropertyChanged(nameof(ShowNoResults));
                (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
            return;
        }

        int? filterYear = hasValidYear ? parsedYear : null;

        // Snapshot active scopes on the UI thread (avoid cross-thread collection access).
        var activeScopes = new List<MediaNode>();
        await UiThreadHelper.InvokeAsync(() =>
        {
            foreach (var root in RootNodes)
                CollectActiveScopes(root, _selectedScopeNodeIds, activeScopes);
        });

        if (activeScopes.Count == 0)
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_searchCts, cts) || token.IsCancellationRequested)
                    return;

                SearchResults.Clear();
                ItemRows.Clear();
                SelectedMediaItem = null;
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(HasNoResults));
                OnPropertyChanged(nameof(ShowNoResults));
                (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
            });
            return;
        }

        var results = new List<MediaItem>(capacity: 256);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Evaluate in background; only UI assignment is marshaled
        for (int i = 0; i < activeScopes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            await CollectMatchesRecursiveAsync(
                activeScopes[i],
                results,
                seen,
                query,
                filterYear,
                favoritesOnly,
                statusFilter,
                token);
        }

        // Global sort across all scopes, same as main MediaArea view:
        // alphabetical by Title (case-insensitive)
        results.Sort(static (a, b) =>
            string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        
        await UiThreadHelper.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_searchCts, cts) || token.IsCancellationRequested)
                return;

            SearchResults.ReplaceAll(results);
            EnsureSelectionIsValid(SearchResults);
            RebuildRows();

            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasNoResults));
            OnPropertyChanged(nameof(ShowNoResults));

            // Keep "Play random" enabled state accurate
            (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
        });
    }

    private void RebuildRows()
    {
        var columnCount = ComputeColumnCount();
        if (columnCount < 1) columnCount = 1;
        _columnCount = columnCount;

        EffectiveItemWidth = ComputeEffectiveItemWidth(columnCount);

        if (SearchResults.Count == 0)
        {
            ItemRows.Clear();
            return;
        }

        var rows = new List<MediaItemRow>();
        for (var i = 0; i < SearchResults.Count; i += columnCount)
        {
            var rowCount = Math.Min(columnCount, SearchResults.Count - i);
            var row = new List<MediaItem>(rowCount);
            for (var j = 0; j < rowCount; j++)
                row.Add(SearchResults[i + j]);

            rows.Add(new MediaItemRow(row));
        }

        ItemRows.ReplaceAll(rows);
    }

    private void EnsureSelectionIsValid(IList<MediaItem> items)
    {
        if (items.Count == 0)
        {
            SelectedMediaItem = null;
            return;
        }

        var selected = SelectedMediaItem;
        if (selected == null)
            return;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ReferenceEquals(item, selected) ||
                string.Equals(item.Id, selected.Id, StringComparison.Ordinal))
            {
                return;
            }
        }

        SelectedMediaItem = null;
    }

    private int ComputeColumnCount()
    {
        var availableWidth = ViewportWidth - ViewportPadding;
        if (availableWidth <= 0 || ItemWidth <= 0)
            return 1;

        var totalItemWidth = ItemWidth + ItemSpacing;
        if (totalItemWidth <= 0)
            return 1;

        return Math.Max(1, (int)Math.Floor((availableWidth + ItemSpacing) / totalItemWidth));
    }

    private double ComputeEffectiveItemWidth(int columnCount)
    {
        if (columnCount < 1)
            return ItemWidth;

        var availableWidth = ViewportWidth - ViewportPadding;
        if (availableWidth <= 0)
            return ItemWidth;

        var totalSpacing = ItemSpacing * (columnCount - 1);
        var width = (availableWidth - totalSpacing) / columnCount;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return ItemWidth;

        return width;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;

        if (_rootNodesObservable != null)
            _rootNodesObservable.CollectionChanged -= OnRootNodesChanged;

        foreach (var node in _trackedRootNodes.ToList())
            UntrackRootNode(node);

        ItemRows.Clear();
    }

    private static async Task CollectMatchesRecursiveAsync(
        MediaNode node,
        List<MediaItem> matches,
        HashSet<string> seen,
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

            if (string.IsNullOrWhiteSpace(item.Id))
            {
                matches.Add(item);
            }
            else if (seen.Add(item.Id))
            {
                matches.Add(item);
            }
        }

        // Recurse into child nodes.
        for (int i = 0; i < children.Count; i++)
        {
            await CollectMatchesRecursiveAsync(
                children[i],
                matches,
                seen,
                query,
                filterYear,
                favoritesOnly,
                statusFilter,
                token);
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
        ResetScopeSelectionToDefault();
    }

    private void EnsureDefaultScopeSelection()
    {
        if (_hasExplicitScopeSelection || _selectedScopeNodeIds.Count > 0)
            return;

        foreach (var root in RootNodes)
            _selectedScopeNodeIds.Add(root.Id);

        NotifyScopeSummaryChanged();
    }

    private void ResetScopeSelectionToDefault()
    {
        _selectedScopeNodeIds.Clear();
        foreach (var root in RootNodes)
            _selectedScopeNodeIds.Add(root.Id);

        _hasExplicitScopeSelection = false;
        NotifyScopeSummaryChanged();
        RequestSearch();
    }

    private void SyncScopeSelection()
    {
        var allIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in RootNodes)
            CollectNodeIds(root, allIds);

        _selectedScopeNodeIds.RemoveWhere(id => !allIds.Contains(id));

        if (!_hasExplicitScopeSelection)
        {
            _selectedScopeNodeIds.Clear();
            foreach (var root in RootNodes)
                _selectedScopeNodeIds.Add(root.Id);
        }

        NotifyScopeSummaryChanged();
    }

    private void NotifyScopeSummaryChanged()
    {
        OnPropertyChanged(nameof(SelectedScopeCount));
        OnPropertyChanged(nameof(SelectedScopeSummary));
    }

    private int CountSelectedNodes()
    {
        var total = 0;
        foreach (var root in RootNodes)
            total += CountSelectedNodesRecursive(root, _selectedScopeNodeIds);

        return total;
    }

    private static int CountSelectedNodesRecursive(MediaNode node, HashSet<string> selectedIds)
    {
        if (selectedIds.Contains(node.Id))
            return CountAllNodes(node);

        var count = 0;
        foreach (var child in node.Children)
            count += CountSelectedNodesRecursive(child, selectedIds);

        return count;
    }

    private static int CountAllNodes(MediaNode node)
    {
        var count = 1;
        foreach (var child in node.Children)
            count += CountAllNodes(child);

        return count;
    }

    private static void CollectNodeIds(MediaNode node, HashSet<string> ids)
    {
        ids.Add(node.Id);
        foreach (var child in node.Children)
            CollectNodeIds(child, ids);
    }

    private static void CollectActiveScopes(MediaNode node, HashSet<string> selectedIds, List<MediaNode> active)
    {
        if (selectedIds.Contains(node.Id))
        {
            active.Add(node);
            return;
        }

        foreach (var child in node.Children)
            CollectActiveScopes(child, selectedIds, active);
    }
}
