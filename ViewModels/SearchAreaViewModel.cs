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
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.Search;

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
    private readonly IErrorService _errorService;
    private readonly ObservableCollection<MediaNode>? _rootNodesObservable;
    private readonly LibrarySearchChangeObserver _libraryObserver;
    private long _libraryRevision;

    // Per-instance boundary also permits deterministic publication/cancellation tests.
    internal Func<LibrarySearchSnapshot, LibrarySearchQuery, CancellationToken, Action<LibrarySearchHit>?, Task<IReadOnlyList<LibrarySearchHit>>> EvaluateSearchAsync { private get; init; }
        = LibrarySearchEvaluator.EvaluateAsync;
    private readonly HashSet<string> _selectedScopeNodeIds = new(StringComparer.Ordinal);
    private bool _hasExplicitScopeSelection;
    private bool _parentalFilterActive;

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

    public MediaMultiSelectionState MultiSelection { get; } = new();

    /// <summary>
    /// Row grouping for a virtualized tile view.
    /// </summary>
    public RangeObservableCollection<MediaItemRow> ItemRows { get; } = new();

    public int ColumnCount => _columnCount;

    [ObservableProperty]
    private MediaItem? _selectedMediaItem;

    [ObservableProperty]
    private bool _isMultiSelectMode;

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
    public IRelayCommand ClearSearchTextCommand { get; }
    public IRelayCommand SelectAllVisibleItemsCommand { get; }
    public IRelayCommand ClearMultiSelectionCommand { get; }

    public string MultiSelectModeToolTip =>
        T("BulkEdit.SelectionMode.Tooltip", "Select multiple items");

    public string MultiSelectionSelectedLabel =>
        T("BulkEdit.Selection.SelectedLabel", "selected");

    public string SelectAllVisibleItemsText =>
        T("BulkEdit.Selection.SelectAllVisible", "Select all shown");

    public string ClearMultiSelectionText =>
        T("BulkEdit.Selection.Clear", "Clear selection");
    
    public event Action<MediaItem>? RequestPlay;
    public event Action<MediaItem>? RequestScrollIntoView;
    
    public bool HasResults => SearchResults.Count > 0;
    public bool HasNoResults => SearchResults.Count == 0;
    public bool ShowNoResults => HasNoResults && HasAnyRoots;
    public bool ShowEmptyLibraryHint => !HasAnyRoots;

    [ObservableProperty]
    private bool _hasAnyRoots;

    public SearchAreaViewModel(IEnumerable<MediaNode> rootNodes, IErrorService errorService, bool parentalFilterActive = false)
    {
        if (rootNodes == null)
            throw new ArgumentNullException(nameof(rootNodes));
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));

        _parentalFilterActive = parentalFilterActive;
        _rootNodesSnapshot = rootNodes.ToList();
        _rootNodesObservable = rootNodes as ObservableCollection<MediaNode>;
        _libraryObserver = new LibrarySearchChangeObserver(RootNodes, OnLibraryChanged);
        MediaSortHelper.TitleSortRuleChanged += OnSortRuleChanged;

        EnsureDefaultScopeSelection();
        UpdateRootAvailability();

        // Keep the initial state consistent (empty results until user enters criteria)
        SearchResults.Clear();

        PlayRandomCommand = new RelayCommand(
            PlayRandom,
            () => !IsMultiSelectMode && SearchResults.Count > 0);

        PlayCommand = new RelayCommand<MediaItem>(item =>
        {
            if (item != null) RequestPlay?.Invoke(item);
        });
        
        // Allow resetting all filters back to defaults
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        ClearSearchTextCommand = new RelayCommand(ClearSearchText);
        SelectAllVisibleItemsCommand = new RelayCommand(() => MultiSelection.ReplaceWith(SearchResults));
        ClearMultiSelectionCommand = new RelayCommand(MultiSelection.Clear);
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

    partial void OnItemWidthChanged(double value) => UpdateGridLayout();

    partial void OnViewportWidthChanged(double value) => UpdateGridLayout();

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();

        if (!value)
            MultiSelection.Clear();
    }

    public void SetParentalFilterActive(bool active)
    {
        if (_parentalFilterActive == active)
            return;

        _parentalFilterActive = active;
        RequestSearch();
    }

    /// <summary>
    /// Re-evaluates the current global search after an existing item was edited.
    /// The view model and its current search settings remain intact.
    /// </summary>
    public void RefreshResults() => RequestSearch(keepSelectionVisible: true);

    internal void RefreshAfterItemChange(string property)
    {
        if (!_disposed && SearchQueryMatcher.Create(SearchText)
            .IsAffectedByItemChange(property, OnlyFavorites, SelectedStatus != null))
            RefreshResults();
    }

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

    public IReadOnlyList<MediaNode> ScopeDialogRootNodesSnapshot
    {
        get
        {
            var roots = RootNodes;
            if (!_parentalFilterActive)
                return roots.ToList();

            return roots.Where(n => n.IsVisibleInTree).ToList();
        }
    }

    public HashSet<string> GetSelectedScopeIdsSnapshot()
        => new(_selectedScopeNodeIds, StringComparer.Ordinal);

    public SearchScopeDialogViewModel CreateScopeDialogViewModel() => new(
        RootNodes.Where(node => !_parentalFilterActive || node == null || node.IsVisibleInTree),
        GetSelectedScopeIdsSnapshot(), _errorService);

    public void ApplyScopeSelection(IReadOnlyCollection<string> selectedIds)
    {
        _selectedScopeNodeIds.Clear();
        foreach (var id in selectedIds)
            _selectedScopeNodeIds.Add(id);

        _hasExplicitScopeSelection = true;
        NotifyScopeSummaryChanged();
        RequestSearch();
    }

    private void OnSortRuleChanged() => OnLibraryChanged(structure: false);

    private void OnLibraryChanged(bool structure)
    {
        // Invalidate immediately, even if notification delivery must be marshaled to the UI.
        Interlocked.Increment(ref _libraryRevision);
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(() => RefreshAfterLibraryChange(structure));
            return;
        }
        RefreshAfterLibraryChange(structure);
    }

    private void RefreshAfterLibraryChange(bool structure)
    {
        if (_disposed) return;
        if (structure)
        {
            UpdateRootAvailability();
            SyncScopeSelection();
        }
        RequestSearch(keepSelectionVisible: true);
    }

    private void RequestSearch(bool keepSelectionVisible = false)
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(() => RequestSearch(keepSelectionVisible));
            return;
        }
        if (_disposed) return;
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        // Freeze request settings on the owning thread, never in the worker.
        var query = new LibrarySearchQuery(SearchText, SearchYear, OnlyFavorites, SelectedStatus);
        var scopes = GetSelectedScopeIdsSnapshot();
        var parentalFilterActive = _parentalFilterActive;
        var revision = Interlocked.Read(ref _libraryRevision);
        _ = RunSearchAsync(query, scopes, parentalFilterActive, revision, cts, keepSelectionVisible);
    }

    private async Task RunSearchAsync(LibrarySearchQuery query, IReadOnlySet<string> scopes,
        bool parentalFilterActive, long revision, CancellationTokenSource cts, bool keepSelectionVisible)
    {
        var token = cts.Token;
        bool IsCurrent() => !_disposed && !token.IsCancellationRequested &&
            ReferenceEquals(_searchCts, cts) && revision == Interlocked.Read(ref _libraryRevision);
        try
        {
            await Task.Delay(SearchDebounceDelay, token).ConfigureAwait(false);
            LibrarySearchSnapshot? snapshot = null;
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                _libraryObserver.RefreshSubscriptions();
                if (query.HasCriteria)
                    snapshot = LibrarySearchSnapshot.Capture(RootNodes, scopes, parentalFilterActive, token: token, query: query);
            });
            token.ThrowIfCancellationRequested();
            if (revision != Interlocked.Read(ref _libraryRevision)) return;
            var showNodeHitCounts = query.Matcher.IsActive || query.Year.HasValue || query.OnlyFavorites || query.Status.HasValue;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            void CountHit(LibrarySearchHit hit)
            {
                for (var index = hit.NodeIndex; index >= 0; index = snapshot!.Nodes[index].ParentIndex)
                    IncrementNodeHitCount(counts, snapshot.Nodes[index].Id);
            }
            var hits = snapshot == null ? Array.Empty<LibrarySearchHit>() :
                await EvaluateSearchAsync(snapshot, query, token, showNodeHitCounts ? CountHit : null).ConfigureAwait(false);
            // Read only the opaque references here; their mutable properties remain on the UI thread.
            var results = hits.Select(hit => hit.Item).ToList();
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!IsCurrent()) return;
                var resultsChanged = !MediaGridRows.HaveSameItemOrder(SearchResults, results);
                if (resultsChanged)
                {
                    SearchResults.ReplaceAll(results);
                    UpdateGridLayout(itemsChanged: true);
                }
                EnsureSelectionIsValid(SearchResults);
                ApplyNodeHitCountsToTree(RootNodes, counts, showNodeHitCounts);
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(HasNoResults));
                OnPropertyChanged(nameof(ShowNoResults));
                (PlayRandomCommand as RelayCommand)?.NotifyCanExecuteChanged();
                if (keepSelectionVisible && resultsChanged && SelectedMediaItem is { } selectedItem)
                    RequestScrollIntoView?.Invoke(selectedItem);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer request or disposal owns the UI now.
        }
        catch (Exception ex)
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (IsCurrent())
                {
                    try
                    {
                        _errorService.Report(new ErrorReport("Search.EvaluationFailed", ErrorSeverity.Error,
                            nameof(SearchAreaViewModel), "The library search could not be evaluated.",
                            Context: new Dictionary<string, string> { ["FailureType"] = ex.GetType().Name }));
                    }
                    catch { /* Diagnostic output must not fault an unobserved search task. */ }
                }
            });
        }
        finally
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
                cts.Dispose();
            });
        }
    }

    private void UpdateGridLayout(bool itemsChanged = false)
    {
        var layout = MediaGridLayoutHelper.Calculate(
            ViewportWidth,
            ViewportPadding,
            ItemWidth,
            ItemSpacing);
        var columnsChanged = _columnCount != layout.ColumnCount;
        _columnCount = layout.ColumnCount;
        EffectiveItemWidth = layout.EffectiveItemWidth;

        // Keep row and tile bindings intact while only their display width changes.
        if (itemsChanged || columnsChanged || (ItemRows.Count == 0 && SearchResults.Count > 0))
            RebuildRows();
    }

    private void RebuildRows()
    {
        var rows = MediaGridRows.Build(SearchResults, _columnCount);
        if (rows.Count == 0)
        {
            if (ItemRows.Count > 0)
                ItemRows.Clear();
            return;
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

        if (!MediaGridRows.Contains(items, selected))
            SelectedMediaItem = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _searchCts?.Cancel();
        _searchCts = null;

        _libraryObserver.Dispose();
        MediaSortHelper.TitleSortRuleChanged -= OnSortRuleChanged;

        ApplyNodeHitCountsToTree(RootNodes, counts: null, showCounts: false);
        ItemRows.Clear();
    }

    private static void IncrementNodeHitCount(IDictionary<string, int> counts, string nodeId)
    {
        if (!counts.TryAdd(nodeId, 1))
            counts[nodeId]++;
    }

    private static void ApplyNodeHitCountsToTree(
        IEnumerable<MediaNode> roots,
        IReadOnlyDictionary<string, int>? counts,
        bool showCounts)
    {
        var stack = new Stack<MediaNode>(roots);
        var visited = new HashSet<MediaNode>();
        while (stack.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            node.GlobalSearchHitCount = showCounts
                ? counts != null && counts.TryGetValue(node.Id, out var count) ? count : 0
                : null;
            node.ShowGlobalSearchHitCount = showCounts;
            foreach (var child in node.Children) stack.Push(child);
        }
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

    private void ClearSearchText()
    {
        SearchText = string.Empty;
        OnlyFavorites = false;
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
        var stack = new Stack<(MediaNode Node, bool Selected)>();
        foreach (var root in RootNodes) stack.Push((root, false));
        var visited = new HashSet<MediaNode>();
        while (stack.TryPop(out var current))
        {
            if (!visited.Add(current.Node)) continue;
            var selected = current.Selected || _selectedScopeNodeIds.Contains(current.Node.Id);
            if (selected) total++;
            foreach (var child in current.Node.Children) stack.Push((child, selected));
        }
        return total;
    }

    private static void CollectNodeIds(MediaNode root, HashSet<string> ids)
    {
        var stack = new Stack<MediaNode>();
        stack.Push(root);
        var visited = new HashSet<MediaNode>();
        while (stack.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            ids.Add(node.Id);
            foreach (var child in node.Children) stack.Push(child);
        }
    }
}
