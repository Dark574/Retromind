using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Linq;
using System.Text;
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
    private enum PowerQueryField
    {
        Title,
        SortTitle,
        Description,
        Developer,
        Publisher,
        Platform,
        Source,
        Genre,
        Series,
        ReleaseType,
        PlayMode,
        MaxPlayers,
        Status,
        Year,
        ReleaseDate,
        Tag,
        CustomAny,
        CustomKey,
        CustomValue,
        CustomNamedKey,
        Id,
        Favorite
    }

    private sealed record PowerQueryTerm(PowerQueryField Field, string Value, string? CustomKey = null);

    private const double DefaultItemWidth = 150.0;
    private const double ItemSpacing = 0.0;
    private const double ViewportPadding = 0.0;

    private int _columnCount = 1;

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
    private double _effectiveItemWidth = DefaultItemWidth;

    [ObservableProperty]
    private double _viewportWidth;

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

    partial void OnItemWidthChanged(double value) => RebuildRows();

    partial void OnViewportWidthChanged(double value) => RebuildRows();
    
    private void DebouncedApplyFilter(string querySnapshot)
    {
        _searchDebounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;

        var token = cts.Token;

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
                    if (!ReferenceEquals(_searchDebounceCts, cts) || token.IsCancellationRequested)
                        return;

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
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        List<PowerQueryTerm> powerTerms = [];
        var hasPowerQuery = hasQuery && TryParsePowerQuery(query!, out powerTerms);

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
            
            if (hasQuery)
            {
                if (hasPowerQuery)
                {
                    if (!MatchesPowerQuery(item, powerTerms))
                        continue;
                }
                else
                {
                    var title = item.Title;
                    if (string.IsNullOrEmpty(title) ||
                        !title.Contains(query!, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
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

    private static bool TryParsePowerQuery(string query, out List<PowerQueryTerm> terms)
    {
        terms = new List<PowerQueryTerm>();
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var tokens = TokenizeQuery(query);
        if (tokens.Count == 0)
            return false;

        var hasPowerSyntax = false;
        foreach (var token in tokens)
        {
            if (!TrySplitPowerToken(token, out var rawKey, out var rawValue))
                continue;

            if (TryParsePowerQueryField(rawKey, out var field, out var customKey))
            {
                hasPowerSyntax = true;
                terms.Add(new PowerQueryTerm(field, rawValue, customKey));
            }
        }

        // No recognized key:value terms -> plain title-only search
        if (!hasPowerSyntax)
        {
            terms.Clear();
            return false;
        }

        // Keep plain words as additional title terms to allow mixed queries:
        // e.g. "zelda dev:nintendo"
        foreach (var token in tokens)
        {
            if (TrySplitPowerToken(token, out var rawKey, out _))
            {
                if (TryParsePowerQueryField(rawKey, out _, out _))
                    continue;
            }

            terms.Add(new PowerQueryTerm(PowerQueryField.Title, token));
        }

        return true;
    }

    private static List<string> TokenizeQuery(string query)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in query)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }

    private static bool TryParsePowerQueryField(string rawKey, out PowerQueryField field, out string? customKey)
    {
        field = default;
        customKey = null;

        if (string.IsNullOrWhiteSpace(rawKey))
            return false;

        var key = rawKey.Trim();
        if (key.StartsWith("cf.", StringComparison.OrdinalIgnoreCase) && key.Length > 3)
        {
            field = PowerQueryField.CustomNamedKey;
            customKey = key[3..];
            return !string.IsNullOrWhiteSpace(customKey);
        }

        switch (key.ToLowerInvariant())
        {
            case "title":
            case "t":
                field = PowerQueryField.Title;
                return true;
            case "sort":
            case "sorttitle":
            case "st":
                field = PowerQueryField.SortTitle;
                return true;
            case "desc":
            case "description":
            case "notes":
                field = PowerQueryField.Description;
                return true;
            case "dev":
            case "developer":
                field = PowerQueryField.Developer;
                return true;
            case "pub":
            case "publisher":
                field = PowerQueryField.Publisher;
                return true;
            case "platform":
            case "plat":
                field = PowerQueryField.Platform;
                return true;
            case "source":
            case "src":
                field = PowerQueryField.Source;
                return true;
            case "genre":
                field = PowerQueryField.Genre;
                return true;
            case "series":
                field = PowerQueryField.Series;
                return true;
            case "release":
            case "releasetype":
            case "rt":
                field = PowerQueryField.ReleaseType;
                return true;
            case "mode":
            case "playmode":
                field = PowerQueryField.PlayMode;
                return true;
            case "max":
            case "players":
            case "maxplayers":
                field = PowerQueryField.MaxPlayers;
                return true;
            case "status":
            case "state":
                field = PowerQueryField.Status;
                return true;
            case "year":
                field = PowerQueryField.Year;
                return true;
            case "date":
            case "released":
                field = PowerQueryField.ReleaseDate;
                return true;
            case "tag":
            case "tags":
                field = PowerQueryField.Tag;
                return true;
            case "cf":
            case "custom":
            case "customfield":
                field = PowerQueryField.CustomAny;
                return true;
            case "cfk":
            case "customkey":
                field = PowerQueryField.CustomKey;
                return true;
            case "cfv":
            case "customvalue":
                field = PowerQueryField.CustomValue;
                return true;
            case "id":
                field = PowerQueryField.Id;
                return true;
            case "fav":
            case "favorite":
            case "favourite":
                field = PowerQueryField.Favorite;
                return true;
            default:
                return false;
        }
    }

    private static bool TrySplitPowerToken(string token, out string rawKey, out string rawValue)
    {
        rawKey = string.Empty;
        rawValue = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var separator = token.IndexOfAny([':', '=']);
        if (separator <= 0 || separator >= token.Length - 1)
            return false;

        rawKey = token[..separator].Trim();
        rawValue = token[(separator + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(rawKey) && !string.IsNullOrWhiteSpace(rawValue);
    }

    private static bool MatchesPowerQuery(MediaItem item, IReadOnlyList<PowerQueryTerm> terms)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            if (!MatchesPowerQueryTerm(item, terms[i]))
                return false;
        }

        return true;
    }

    private static bool MatchesPowerQueryTerm(MediaItem item, PowerQueryTerm term)
    {
        var value = term.Value;
        switch (term.Field)
        {
            case PowerQueryField.Title:
                return ContainsIgnoreCase(item.Title, value);
            case PowerQueryField.SortTitle:
                return ContainsIgnoreCase(item.SortTitle, value);
            case PowerQueryField.Description:
                return ContainsIgnoreCase(item.Description, value);
            case PowerQueryField.Developer:
                return ContainsIgnoreCase(item.Developer, value);
            case PowerQueryField.Publisher:
                return ContainsIgnoreCase(item.Publisher, value);
            case PowerQueryField.Platform:
                return ContainsIgnoreCase(item.Platform, value);
            case PowerQueryField.Source:
                return ContainsIgnoreCase(item.Source, value);
            case PowerQueryField.Genre:
                return ContainsIgnoreCase(item.Genre, value);
            case PowerQueryField.Series:
                return ContainsIgnoreCase(item.Series, value);
            case PowerQueryField.ReleaseType:
                return ContainsIgnoreCase(item.ReleaseType, value);
            case PowerQueryField.PlayMode:
                return ContainsIgnoreCase(item.PlayMode, value);
            case PowerQueryField.MaxPlayers:
                return MatchesMaxPlayers(item.MaxPlayers, value);
            case PowerQueryField.Status:
                return MatchesStatus(item.Status, value);
            case PowerQueryField.Year:
                return MatchesYear(item.ReleaseDate, value);
            case PowerQueryField.ReleaseDate:
                return item.ReleaseDate.HasValue &&
                       ContainsIgnoreCase(item.ReleaseDate.Value.ToString("yyyy-MM-dd"), value);
            case PowerQueryField.Tag:
                return item.Tags.Any(tag => ContainsIgnoreCase(tag, value));
            case PowerQueryField.CustomAny:
                return MatchesCustomAny(item.CustomFields, value);
            case PowerQueryField.CustomKey:
                return item.CustomFields.Keys.Any(key => ContainsIgnoreCase(key, value));
            case PowerQueryField.CustomValue:
                return item.CustomFields.Values.Any(v => ContainsIgnoreCase(v, value));
            case PowerQueryField.CustomNamedKey:
                return MatchesCustomNamedKey(item.CustomFields, term.CustomKey, value);
            case PowerQueryField.Id:
                return ContainsIgnoreCase(item.Id, value);
            case PowerQueryField.Favorite:
                return MatchesFavorite(item.IsFavorite, value);
            default:
                return false;
        }
    }

    private static bool MatchesStatus(PlayStatus status, string rawValue)
    {
        if (Enum.TryParse<PlayStatus>(rawValue, ignoreCase: true, out var parsed))
            return status == parsed;

        return ContainsIgnoreCase(status.ToString(), rawValue);
    }

    private static bool MatchesYear(DateTime? releaseDate, string rawValue)
    {
        if (!releaseDate.HasValue)
            return false;

        if (int.TryParse(rawValue, out var year))
            return releaseDate.Value.Year == year;

        return ContainsIgnoreCase(releaseDate.Value.ToString("yyyy-MM-dd"), rawValue);
    }

    private static bool MatchesFavorite(bool isFavorite, string rawValue)
    {
        var normalized = rawValue.Trim().ToLowerInvariant();
        if (normalized is "1" or "true" or "yes" or "y")
            return isFavorite;
        if (normalized is "0" or "false" or "no" or "n")
            return !isFavorite;

        var favoriteText = isFavorite ? "true" : "false";
        return ContainsIgnoreCase(favoriteText, rawValue);
    }

    private static bool MatchesMaxPlayers(string? maxPlayers, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(maxPlayers))
            return false;

        if (int.TryParse(rawValue, out var expected) &&
            int.TryParse(maxPlayers.Trim(), out var actual))
        {
            return actual == expected;
        }

        return ContainsIgnoreCase(maxPlayers, rawValue);
    }

    private static bool MatchesCustomAny(Dictionary<string, string>? customFields, string rawValue)
    {
        if (customFields == null || customFields.Count == 0)
            return false;

        foreach (var kv in customFields)
        {
            if (ContainsIgnoreCase(kv.Key, rawValue) || ContainsIgnoreCase(kv.Value, rawValue))
                return true;
        }

        return false;
    }

    private static bool MatchesCustomNamedKey(Dictionary<string, string>? customFields, string? customKey, string rawValue)
    {
        if (customFields == null || customFields.Count == 0 || string.IsNullOrWhiteSpace(customKey))
            return false;

        foreach (var kv in customFields)
        {
            if (!string.Equals(kv.Key, customKey, StringComparison.OrdinalIgnoreCase))
                continue;

            return ContainsIgnoreCase(kv.Value, rawValue);
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string? value, string query)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains(query, StringComparison.OrdinalIgnoreCase);
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
        EnsureSelectionIsValid(FilteredItems);
        RebuildRows();
        PlayRandomCommand.NotifyCanExecuteChanged();
    }

    private void RebuildRows()
    {
        var columnCount = ComputeColumnCount();
        if (columnCount < 1) columnCount = 1;
        _columnCount = columnCount;

        EffectiveItemWidth = ComputeEffectiveItemWidth(columnCount);

        if (FilteredItems.Count == 0)
        {
            ItemRows.Clear();
            return;
        }

        var rows = new List<MediaItemRow>();
        for (var i = 0; i < FilteredItems.Count; i += columnCount)
        {
            var rowCount = Math.Min(columnCount, FilteredItems.Count - i);
            var row = new List<MediaItem>(rowCount);
            for (var j = 0; j < rowCount; j++)
                row.Add(FilteredItems[i + j]);

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

        ItemRows.Clear();
    }
}
