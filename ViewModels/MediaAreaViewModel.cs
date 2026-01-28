using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for the main content area (grid/list of items).
/// Focuses on fast filtering and lightweight user interactions.
/// </summary>
public partial class MediaAreaViewModel : ViewModelBase, IDisposable
{
    private const double DefaultItemWidth = 150.0;

    // Debounce to avoid re-filtering 30k items on every key stroke.
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(200);

    // Prevents rapid re-triggering of Play Random.
    private static readonly TimeSpan PlayRandomCooldown = TimeSpan.FromMilliseconds(1000);

    private readonly List<MediaItem> _allItems;

    private CancellationTokenSource? _searchDebounceCts;
    private bool _isDisposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayRandomCommand))]
    private bool _isPlayRandomEnabled = true;

    [ObservableProperty]
    private double _itemWidth = DefaultItemWidth;

    [ObservableProperty]
    private MediaNode _node;

    [ObservableProperty]
    private MediaItem? _selectedMediaItem;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>
    /// When true, only items marked as favorites are shown in the list
    /// This flag is combined with the text search filter
    /// </summary>
    [ObservableProperty]
    private bool _onlyFavorites;
    
    /// <summary>
    /// Optional status filter. When null, status is ignored.
    /// </summary>
    [ObservableProperty]
    private PlayStatus? _selectedStatus;

    [ObservableProperty]
    private StatusFilterOption? _selectedStatusOption;

    /// <summary>
    /// Available status values for the status filter combo box.
    /// </summary>
    public IReadOnlyList<StatusFilterOption> StatusOptions { get; } = StatusFilterOption.CreateDefault();

    /// <summary>
    /// Collection bound to the view. Range updates minimize UI stalls.
    /// </summary>
    public RangeObservableCollection<MediaItem> FilteredItems { get; } = new();

    /// <summary>
    /// Command to reset all filters to defaults.
    /// </summary>
    public IRelayCommand ResetFiltersCommand { get; }
    
    public MediaAreaViewModel(MediaNode node, double initialItemWidth)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ItemWidth = initialItemWidth;

        // Snapshot items for filtering. (If you later support live updates, we can sync this list.)
        _allItems = new List<MediaItem>(node.Items);
        foreach (var item in _allItems)
            item.PropertyChanged += OnItemPropertyChanged;

        PopulateItems(_allItems);

        DoubleClickCommand = new RelayCommand(OnDoubleClick);
        
        // Allow resetting all filters back to defaults
        ResetFiltersCommand = new RelayCommand(ResetFilters);
        SelectedStatusOption = StatusOptions[0];
    }

    public ICommand DoubleClickCommand { get; }

    /// <summary>
    /// Raised when the user requests playback (double click or random).
    /// Handled by the parent coordinator (MainWindowViewModel).
    /// </summary>
    public event Action<MediaItem>? RequestPlay;

    partial void OnSearchTextChanged(string value)
    {
        DebouncedApplyFilter(value);
    }

    partial void OnOnlyFavoritesChanged(bool value)
    {
        // Re-apply the current filter when the favorites toggle changes
        DebouncedApplyFilter(SearchText);
    }
    
    partial void OnSelectedStatusChanged(PlayStatus? value)
    {
        var option = StatusOptions.FirstOrDefault(o => o.Value == value) ?? StatusOptions[0];
        if (!ReferenceEquals(SelectedStatusOption, option))
            SelectedStatusOption = option;

        // Re-apply when status filter changes
        DebouncedApplyFilter(SearchText);
    }

    partial void OnSelectedStatusOptionChanged(StatusFilterOption? value)
    {
        if (SelectedStatus != value?.Value)
            SelectedStatus = value?.Value;
    }
    
    private void DebouncedApplyFilter(string querySnapshot)
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();

        var token = _searchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceDelay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                // 1) Compute matches off the UI thread
                var matches = BuildMatches(querySnapshot, token);

                if (token.IsCancellationRequested) return;

                // 2) Apply on UI thread (ReplaceAll triggers CollectionChanged!)
                UiThreadHelper.Post(() =>
                {
                    // If a newer search has started, ignore this result
                    if (_searchDebounceCts == null || _searchDebounceCts.IsCancellationRequested) return;

                    ApplyMatchesToUi(querySnapshot, matches);
                });
            }
            catch (OperationCanceledException)
            {
                // expected during debounce
            }
        }, token);
    }

    private void ResetFilters()
    {
        SearchText = string.Empty;
        OnlyFavorites = false;
        SelectedStatusOption = StatusOptions[0];
    }

    private List<MediaItem> BuildMatches(string querySnapshot, CancellationToken token)
    {
        var query = querySnapshot?.Trim();
        var favoritesOnly = OnlyFavorites;
        var statusFilter = SelectedStatus;

        if (string.IsNullOrWhiteSpace(query) && !favoritesOnly && statusFilter == null)
        {
            // Return a copy to keep the API consistent (List<MediaItem>),
            // while avoiding enumerating UI-bound collections in the background.
            return new List<MediaItem>(_allItems);
        }

        var matches = new List<MediaItem>(capacity: Math.Min(_allItems.Count, 256));

        for (int i = 0; i < _allItems.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var item = _allItems[i];
            
            if (favoritesOnly && !item.IsFavorite)
                continue;
            
            if (statusFilter.HasValue && item.Status != statusFilter.Value)
                continue;
            
            var title = item.Title;

            if (!string.IsNullOrWhiteSpace(query))
            {
                if (string.IsNullOrEmpty(title) ||
                    !title.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            matches.Add(item);
        }

        return matches;
    }

    private void ApplyMatchesToUi(string querySnapshot, List<MediaItem> matches)
    {
        var query = querySnapshot?.Trim();

        // Only skip updates when:
        //  - no text query,
        //  - no favorites filter,
        //  - no status filter,
        //  - and we already show all items.
        if (string.IsNullOrWhiteSpace(query) &&
            !OnlyFavorites &&
            SelectedStatus == null)
        {
            // Avoid unnecessary resets if we already show all items
            if (FilteredItems.Count == _allItems.Count)
                return;
        }

        PopulateItems(matches);
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaItem.IsFavorite) &&
            e.PropertyName != nameof(MediaItem.Status))
        {
            return;
        }

        if (!OnlyFavorites && SelectedStatus == null)
            return;

        DebouncedApplyFilter(SearchText);
    }
    
    /// <summary>
    /// Repopulates the list efficiently (single Reset notification)
    /// Also updates command availability without relying on CollectionChanged events
    /// </summary>
    private void PopulateItems(IEnumerable<MediaItem> items)
    {
        FilteredItems.ReplaceAll(items);
        PlayRandomCommand.NotifyCanExecuteChanged();
    }

    private bool CanPlayRandom() => IsPlayRandomEnabled && FilteredItems.Count > 0;

    [RelayCommand(CanExecute = nameof(CanPlayRandom))]
    private async Task PlayRandom()
    {
        // Disable immediately to prevent rapid re-triggering
        IsPlayRandomEnabled = false;

        try
        {
            if (FilteredItems.Count == 0) return;

            var index = Random.Shared.Next(FilteredItems.Count);
            var randomItem = FilteredItems[index];

            SelectedMediaItem = randomItem;
            RequestPlay?.Invoke(randomItem);
        }
        finally
        {
            // Small cooldown to avoid accidental double-activation
            await Task.Delay(PlayRandomCooldown).ConfigureAwait(false);
            
            // Re-enable on the UI thread so NotifyCanExecuteChanged and bindings are safe
            await UiThreadHelper.InvokeAsync(
                () => IsPlayRandomEnabled = true,
                DispatcherPriority.Background);
        }
    }

    private void OnDoubleClick()
    {
        if (SelectedMediaItem != null)
            RequestPlay?.Invoke(SelectedMediaItem);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;

        foreach (var item in _allItems)
            item.PropertyChanged -= OnItemPropertyChanged;
    }
}
