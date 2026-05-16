using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models.Stores;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class GogPickerDialogViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<GogPickerGameEntry> _allEntries;
    private CancellationTokenSource? _filterCts;
    private int _selectedCount;
    private bool _disposed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _onlyNewInNode = true;

    public RangeObservableCollection<GogPickerGameEntry> FilteredGames { get; } = new();

    public string DialogTitleText => T("Gog.Picker.DialogTitle", "Select GOG media");
    public string HeaderText => T("Gog.Picker.Header", "GOG library");
    public string OnlyNewInNodeText => T("Gog.Picker.OnlyNewInNode", "Only new in this node");
    public string SearchPlaceholderText => T("Gog.Picker.SearchPlaceholder", "Search by title, GOG ID, or platform...");
    public string SelectAllFilteredText => T("Gog.Picker.SelectAllFiltered", "Select all filtered");
    public string ClearFilteredSelectionText => T("Gog.Picker.ClearSelection", "Clear selection");
    public string EmptyStateText => T("Gog.Picker.EmptyState", "No matches for the current filter.");
    public string AddSelectionText => T("Gog.Picker.AddSelection", "Add selection");
    public string CancelText => Strings.Button_Cancel;

    public int TotalCount => _allEntries.Count;
    public int FilteredCount => FilteredGames.Count;
    public int SelectedCount => _selectedCount;
    public bool ShowEmptyState => FilteredCount == 0;
    public string CounterText => string.Format(
        CultureInfo.CurrentCulture,
        T("Gog.Picker.CounterFormat", "Filtered: {0:N0} / {1:N0}   Selected: {2:N0}"),
        FilteredCount,
        TotalCount,
        SelectedCount);

    public IRelayCommand SelectAllFilteredCommand { get; }
    public IRelayCommand ClearFilteredSelectionCommand { get; }
    public IRelayCommand ApplyCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<bool>? RequestClose;

    public GogPickerDialogViewModel(
        IEnumerable<StoreGameRecord> games,
        IReadOnlySet<string> existingInTargetNode,
        IReadOnlyDictionary<string, int> usageByGameId)
    {
        if (games == null) throw new ArgumentNullException(nameof(games));

        var existingIds = existingInTargetNode ?? new HashSet<string>(StringComparer.Ordinal);
        var usage = usageByGameId ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var statusAlreadyInNode = T("Gog.Picker.StatusAlreadyInNode", "Already in this node");
        var statusLinkedFormat = T("Gog.Picker.StatusLinkedFormat", "Already linked {0}x");
        var statusNew = T("Gog.Picker.StatusNew", "New");

        _allEntries = games
            .Where(g => string.Equals(g.ProviderId, "gog", StringComparison.OrdinalIgnoreCase))
            .Where(g => !string.IsNullOrWhiteSpace(g.StoreGameId))
            .GroupBy(g => g.StoreGameId, StringComparer.Ordinal)
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(g => !string.IsNullOrWhiteSpace(g.Title))
                    .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                    .First();

                var usageCount = usage.TryGetValue(group.Key, out var count) ? count : 0;
                var isInCurrentNode = existingIds.Contains(group.Key);
                return new GogPickerGameEntry(
                    best,
                    isInCurrentNode,
                    usageCount,
                    statusAlreadyInNode,
                    statusLinkedFormat,
                    statusNew);
            })
            .OrderBy(e => e.SortTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.StoreGameId, StringComparer.Ordinal)
            .ToArray();

        foreach (var entry in _allEntries)
            entry.SelectionChanged += OnEntrySelectionChanged;

        SelectAllFilteredCommand = new RelayCommand(SelectAllFiltered);
        ClearFilteredSelectionCommand = new RelayCommand(ClearFilteredSelection);
        ApplyCommand = new RelayCommand(() => RequestClose?.Invoke(true), () => SelectedCount > 0);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));

        ApplyFilterCore();
    }

    partial void OnSearchTextChanged(string value) => ScheduleFilter();

    partial void OnOnlyNewInNodeChanged(bool value) => ScheduleFilter();

    public IReadOnlyList<StoreGameRecord> GetSelectedGames()
        => _allEntries.Where(e => e.IsSelected).Select(e => e.Game).ToList();

    private void OnEntrySelectionChanged(GogPickerGameEntry entry)
    {
        if (entry.IsSelected)
            _selectedCount++;
        else if (_selectedCount > 0)
            _selectedCount--;

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CounterText));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void SelectAllFiltered()
    {
        foreach (var entry in FilteredGames)
        {
            if (entry.IsSelectionEnabled && !entry.IsSelected)
                entry.IsSelected = true;
        }
    }

    private void ClearFilteredSelection()
    {
        foreach (var entry in FilteredGames)
        {
            if (entry.IsSelected)
                entry.IsSelected = false;
        }
    }

    private void ScheduleFilter()
    {
        if (_disposed)
            return;

        _ = ApplyFilterDebouncedAsync();
    }

    private async Task ApplyFilterDebouncedAsync()
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        _filterCts = new CancellationTokenSource();

        var ct = _filterCts.Token;
        try
        {
            await Task.Delay(140, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed || ct.IsCancellationRequested)
            return;

        await UiThreadHelper.InvokeAsync(() =>
        {
            if (_disposed || ct.IsCancellationRequested)
                return;

            ApplyFilterCore();
        });
    }

    private void ApplyFilterCore()
    {
        IEnumerable<GogPickerGameEntry> query = _allEntries;

        if (OnlyNewInNode)
            query = query.Where(entry => !entry.ExistsInTargetNode);

        var filter = SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var normalized = filter.ToUpperInvariant();
            query = query.Where(entry => entry.Matches(normalized));
        }

        FilteredGames.ReplaceAll(query);
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(CounterText));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var entry in _allEntries)
            entry.SelectionChanged -= OnEntrySelectionChanged;

        if (_filterCts != null)
        {
            _filterCts.Cancel();
            _filterCts.Dispose();
            _filterCts = null;
        }
    }

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;
}

public partial class GogPickerGameEntry : ObservableObject
{
    private readonly string _searchIndex;

    public StoreGameRecord Game { get; }
    public string StoreGameId => Game.StoreGameId;
    public string SortTitle => string.IsNullOrWhiteSpace(Game.Title) ? $"GOG {StoreGameId}" : Game.Title;
    public string DisplayTitle => SortTitle;
    public string Platform => string.IsNullOrWhiteSpace(Game.Platform) ? "-" : Game.Platform!;
    public bool ExistsInTargetNode { get; }
    public bool IsSelectionEnabled => !ExistsInTargetNode;
    public int ExistingUsageCount { get; }
    public string StatusText { get; }

    [ObservableProperty]
    private bool _isSelected;

    public event Action<GogPickerGameEntry>? SelectionChanged;

    public GogPickerGameEntry(
        StoreGameRecord game,
        bool existsInTargetNode,
        int existingUsageCount,
        string statusAlreadyInNode,
        string statusLinkedFormat,
        string statusNew)
    {
        Game = game;
        ExistsInTargetNode = existsInTargetNode;
        ExistingUsageCount = Math.Max(0, existingUsageCount);

        StatusText = existsInTargetNode
            ? statusAlreadyInNode
            : ExistingUsageCount > 0
                ? string.Format(CultureInfo.CurrentCulture, statusLinkedFormat, ExistingUsageCount)
                : statusNew;

        _searchIndex = $"{DisplayTitle}\n{StoreGameId}\n{Platform}".ToUpperInvariant();
    }

    public bool Matches(string normalizedFilter)
    {
        if (string.IsNullOrWhiteSpace(normalizedFilter))
            return true;

        return _searchIndex.Contains(normalizedFilter, StringComparison.Ordinal);
    }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this);
}
