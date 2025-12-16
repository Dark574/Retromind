using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for the main content area (grid/list of items).
/// Focuses on fast filtering and lightweight user interactions.
/// </summary>
public partial class MediaAreaViewModel : ViewModelBase
{
    private const double DefaultItemWidth = 150.0;

    // Debounce to avoid re-filtering 30k items on every key stroke.
    private static readonly TimeSpan SearchDebounceDelay = TimeSpan.FromMilliseconds(200);

    // Prevents rapid re-triggering of Play Random.
    private static readonly TimeSpan PlayRandomCooldown = TimeSpan.FromMilliseconds(1000);

    private readonly List<MediaItem> _allItems;

    private CancellationTokenSource? _searchDebounceCts;

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
    /// Collection bound to the view. Range updates minimize UI stalls.
    /// </summary>
    public RangeObservableCollection<MediaItem> FilteredItems { get; } = new();

    public MediaAreaViewModel(MediaNode node, double initialItemWidth)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ItemWidth = initialItemWidth;

        // Snapshot items for filtering. (If you later support live updates, we can sync this list.)
        _allItems = new List<MediaItem>(node.Items);

        PopulateItems(_allItems);

        DoubleClickCommand = new RelayCommand(OnDoubleClick);
    }

    public ICommand DoubleClickCommand { get; }

    /// <summary>
    /// Raised when the user requests playback (double click or random).
    /// Handled by the parent coordinator (MainWindowViewModel).
    /// </summary>
    public event Action<MediaItem>? RequestPlay;

    partial void OnSearchTextChanged(string value)
    {
        DebouncedApplyFilter();
    }

    private void DebouncedApplyFilter()
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

                ApplyFilter();
            }
            catch (OperationCanceledException)
            {
                // expected during debounce
            }
        }, token);
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            // Avoid unnecessary resets if we already show all items.
            if (FilteredItems.Count == _allItems.Count)
                return;

            PopulateItems(_allItems);
            return;
        }

        // Allocation-light filtering: one pass, no LINQ iterator chain.
        var matches = new List<MediaItem>(capacity: Math.Min(_allItems.Count, 256));

        for (int i = 0; i < _allItems.Count; i++)
        {
            var item = _allItems[i];
            var title = item.Title;

            if (!string.IsNullOrEmpty(title) &&
                title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(item);
            }
        }

        PopulateItems(matches);
    }

    /// <summary>
    /// Repopulates the list efficiently (single Reset notification).
    /// Also updates command availability without relying on CollectionChanged events.
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
        // Disable immediately to prevent rapid re-triggering.
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
            // Small cooldown to avoid accidental double-activation.
            await Task.Delay(PlayRandomCooldown).ConfigureAwait(false);
            IsPlayRandomEnabled = true;
        }
    }

    private void OnDoubleClick()
    {
        if (SelectedMediaItem != null)
            RequestPlay?.Invoke(SelectedMediaItem);
    }
}