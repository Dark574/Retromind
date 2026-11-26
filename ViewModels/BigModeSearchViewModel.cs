using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Services.Search;

namespace Retromind.ViewModels;

public enum BigModeSearchFocus { Keyboard, Scope, ResultsButton, Results }
public enum BigModeSearchKeyKind { Text, Space, Backspace, Clear, Layer, Case }
public sealed record BigModeSearchKey(string Label, string Text, BigModeSearchKeyKind Kind = BigModeSearchKeyKind.Text);

/// <summary>Display-only values. The opaque item handle is revalidated before reveal/launch.</summary>
public sealed class BigModeSearchResult
{
    private static readonly Lazy<Geometry> NativeIcon = new(() => Geometry.Parse("M2,3 L22,3 L22,17 L2,17 Z M8,22 L16,22 M12,17 L12,22"));
    private static readonly Lazy<Geometry> EmulatorIcon = new(() => Geometry.Parse("M6,7 L18,7 C22,7 25,20 21,20 L16,16 L8,16 L3,20 C-1,20 2,7 6,7 Z M6,10 L6,14 M4,12 L8,12 M16,11 L17,11 M19,13 L20,13"));
    private static readonly Lazy<Geometry> CommandIcon = new(() => Geometry.Parse("M2,3 L22,3 L22,21 L2,21 Z M6,8 L10,12 L6,16 M13,16 L18,16"));
    internal MediaItem Item { get; }
    public string ItemId { get; }
    public string OwnerId { get; }
    public string Title { get; }
    public string Category { get; }
    public MediaType MediaType { get; }
    public Geometry TypeIcon => MediaType switch { MediaType.Emulator => EmulatorIcon.Value, MediaType.Command => CommandIcon.Value, _ => NativeIcon.Value };
    internal BigModeSearchResult(LibrarySearchHit hit, string category)
    { Item = hit.Item; ItemId = hit.ItemId; OwnerId = hit.OwnerId; Title = hit.Document.Title; MediaType = hit.MediaType; Category = category; }
}

/// <summary>One temporary search context; no desktop state, counters, image requests or library cursor.</summary>
public sealed partial class BigModeSearchViewModel : ObservableObject, IDisposable
{
    public const int VisibleResultCount = 5;
    public const int KeyboardColumns = 10;
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private readonly IReadOnlyList<MediaNode> _roots;
    private readonly MediaNode? _category;
    private readonly IErrorService? _errors;
    private bool _parentalFilter;
    private LibrarySearchChangeObserver? _observer;
    private CancellationTokenSource? _request;
    private long _revision;
    private bool _disposed;
    private string? _preferredItemId;
    internal TimeProvider SearchTimeProvider { get; init; } = TimeProvider.System;
    internal Func<LibrarySearchSnapshot, LibrarySearchQuery, CancellationToken, Task<IReadOnlyList<LibrarySearchHit>>> EvaluateAsync { private get; init; }
        = (snapshot, query, token) => LibrarySearchEvaluator.EvaluateAsync(snapshot, query, token);
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;
    internal bool IsObserving => _observer != null;
    public RangeObservableCollection<BigModeSearchResult> Results { get; } = new();
    public RangeObservableCollection<BigModeSearchKey> Keys { get; } = new();
    public IRelayCommand ToggleScopeCommand { get; }
    public IRelayCommand FocusResultsCommand { get; }
    public event Action<bool>? RequestResultAction;
    public event Action? RequestClose;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _currentCategoryOnly;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private BigModeSearchFocus _focusArea;
    [ObservableProperty] private int _selectedKeyIndex;
    [ObservableProperty] private int _selectedResultIndex = -1;
    [ObservableProperty] private int _firstVisibleResultIndex;
    [ObservableProperty] private int _caretIndex;
    [ObservableProperty] private int _selectionStart;
    [ObservableProperty] private int _selectionEnd;
    [ObservableProperty] private string _controlsText = string.Empty;
    private bool _symbols;
    private bool _upper;
    private bool _failed;

    public string Title => T("BigModeSearch_Title", "Search library");
    public string SearchWatermark => T("BigModeSearch_Watermark", "Title or search expression …");
    public string ResultsTitle => string.Format(T("BigModeSearch_Results", "Search results · {0}"), Results.Count);
    public string AllLibraryText => T("BigModeSearch_All", "Entire library");
    public string CategoryScopeText => string.Format(T("BigModeSearch_Category", "Category: {0}"), _category?.Name ?? T("BigModeSearch_NoCategory", "No current category"));
    public bool CanUseCategory => _category != null;
    public string ScopeText => CurrentCategoryOnly ? CategoryScopeText : AllLibraryText;
    public string ToResultsText => T("BigModeSearch_ToResults", "Go to results");
    public bool HasNoResults => Results.Count == 0;
    public string EmptyText => _failed ? T("BigModeSearch_Failed", "The search could not be completed.") : IsBusy ? T("BigModeSearch_Busy", "Searching …") : string.IsNullOrWhiteSpace(SearchText)
        ? T("BigModeSearch_Empty", "Enter a title or search expression.") : T("BigModeSearch_NoResults", "No matching media.");
    public string PositionText => Results.Count == 0 ? T("BigModeSearch_Zero", "0 results") : string.Format(
        T("BigModeSearch_Position", "{0}–{1} of {2}"), FirstVisibleResultIndex + 1,
        Math.Min(FirstVisibleResultIndex + VisibleResultCount, Results.Count), Results.Count);
    public bool IsKeyboardFocused => FocusArea == BigModeSearchFocus.Keyboard;
    public bool IsScopeFocused => FocusArea == BigModeSearchFocus.Scope;
    public bool IsResultsButtonFocused => FocusArea == BigModeSearchFocus.ResultsButton;
    public bool IsResultsFocused => FocusArea == BigModeSearchFocus.Results;
    public BigModeSearchResult? SelectedResult => SelectedResultIndex >= 0 && SelectedResultIndex < Results.Count ? Results[SelectedResultIndex] : null;

    public BigModeSearchViewModel(IReadOnlyList<MediaNode> roots, MediaNode? category, bool parentalFilter, IErrorService? errors = null)
    {
        _roots = roots; _category = category; _parentalFilter = parentalFilter; _errors = errors;
        ToggleScopeCommand = new RelayCommand(ToggleScope);
        FocusResultsCommand = new RelayCommand(FocusResults);
        BuildKeys();
        MediaSortHelper.TitleSortRuleChanged += Invalidate;
    }

    partial void OnSearchTextChanged(string value) { OnPropertyChanged(nameof(EmptyText)); Schedule(); }
    partial void OnCurrentCategoryOnlyChanged(bool value) { OnPropertyChanged(nameof(ScopeText)); Schedule(); }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(EmptyText));
    partial void OnFirstVisibleResultIndexChanged(int value) => OnPropertyChanged(nameof(PositionText));
    partial void OnFocusAreaChanged(BigModeSearchFocus value)
    {
        foreach (var name in new[] { nameof(IsKeyboardFocused), nameof(IsScopeFocused), nameof(IsResultsButtonFocused), nameof(IsResultsFocused) }) OnPropertyChanged(name);
    }
    partial void OnSelectedResultIndexChanged(int value)
    {
        if (SelectedResult is { } row) _preferredItemId = row.ItemId;
        if (value >= 0)
        {
            if (value < FirstVisibleResultIndex) FirstVisibleResultIndex = value;
            else if (value >= FirstVisibleResultIndex + VisibleResultCount) FirstVisibleResultIndex = value - VisibleResultCount + 1;
        }
        OnPropertyChanged(nameof(SelectedResult));
    }

    public void ToggleScope() { if (!_disposed && CanUseCategory) CurrentCategoryOnly = !CurrentCategoryOnly; }
    public void FocusResults() { if (!_disposed) FocusArea = BigModeSearchFocus.Results; }
    public void FocusKeyboard() { if (!_disposed) FocusArea = BigModeSearchFocus.Keyboard; }
    public void SetProtection(bool active)
    {
        if (_parentalFilter == active || _disposed) return;
        _parentalFilter = active; Invalidate();
    }
    public void Invalidate() => OnLibraryChanged(false);
    private void OnLibraryChanged(bool structure)
    {
        Interlocked.Increment(ref _revision);
        UiThreadHelper.Post(() =>
        {
            if (_disposed) return;
            // Immediately hide results whose existence/protection may have changed.
            var id = SelectedResult?.ItemId ?? _preferredItemId;
            Results.Clear(); SelectedResultIndex = -1; FirstVisibleResultIndex = 0; _preferredItemId = id;
            NotifyResults(); OnPropertyChanged(nameof(CategoryScopeText)); OnPropertyChanged(nameof(ScopeText));
            Schedule();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }
    private void Schedule()
    {
        if (_disposed) return;
        _request?.Cancel();
        var request = new CancellationTokenSource(); _request = request;
        _failed = false;
        var query = new LibrarySearchQuery(SearchText);
        if (!query.HasCriteria)
        {
            Results.Clear(); SelectedResultIndex = -1; FirstVisibleResultIndex = 0; NotifyResults();
        }
        var category = CurrentCategoryOnly ? _category?.Id : null;
        var revision = Interlocked.Read(ref _revision);
        var parental = _parentalFilter;
        IsBusy = query.HasCriteria;
        PendingSearch = RunAsync(query, category, parental, revision, request);
    }
    private async Task RunAsync(LibrarySearchQuery query, string? category, bool parental, long revision, CancellationTokenSource request)
    {
        var token = request.Token;
        bool Current() => !_disposed && !token.IsCancellationRequested && ReferenceEquals(_request, request) && revision == Interlocked.Read(ref _revision);
        try
        {
            await Task.Delay(Debounce, SearchTimeProvider, token).ConfigureAwait(false);
            LibrarySearchSnapshot? snapshot = null;
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!Current() || !query.HasCriteria) return;
                _observer ??= new LibrarySearchChangeObserver(_roots, OnLibraryChanged);
                _observer.RefreshSubscriptions();
                var visibility = new BigModeVisibilityPolicy(parental);
                snapshot = LibrarySearchSnapshot.Capture(_roots, category == null ? null : new HashSet<string>(StringComparer.Ordinal) { category },
                    parental, visibility.IsNodeVisible, token, query);
            });
            token.ThrowIfCancellationRequested();
            var hits = snapshot == null ? Array.Empty<LibrarySearchHit>() : await EvaluateAsync(snapshot, query, token).ConfigureAwait(false);
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!Current()) return;
                ApplyResults(hits);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!Current()) return;
                _failed = true; IsBusy = false; Results.Clear(); SelectedResultIndex = -1; FirstVisibleResultIndex = 0; NotifyResults();
                try { _errors?.Report(new ErrorReport("BigMode.SearchFailed", ErrorSeverity.Error, nameof(BigModeSearchViewModel),
                    "The library search could not be evaluated.", Context: new Dictionary<string,string> { ["FailureType"] = ex.GetType().Name })); }
                catch { }
            });
        }
        finally
        {
            await UiThreadHelper.InvokeAsync(() => { if (ReferenceEquals(_request, request)) { _request = null; PendingSearch = Task.CompletedTask; } request.Dispose(); });
        }
    }
    private void ApplyResults(IReadOnlyList<LibrarySearchHit> hits)
    {
        var selectedId = SelectedResult?.ItemId ?? _preferredItemId;
        var paths = CategoryPaths();
        var rows = hits.Select(hit => new BigModeSearchResult(hit, paths.GetValueOrDefault(hit.Owner, hit.Owner.Name))).ToList();
        Results.ReplaceAll(rows);
        var target = rows.FindIndex(row => row.ItemId == selectedId);
        SelectedResultIndex = target >= 0 ? target : rows.Count > 0 ? 0 : -1;
        FirstVisibleResultIndex = Math.Clamp(FirstVisibleResultIndex, 0, Math.Max(0, rows.Count - VisibleResultCount));
        IsBusy = false; NotifyResults();
    }

    private Dictionary<MediaNode,string> CategoryPaths()
    {
        var paths = new Dictionary<MediaNode,string>();
        var pending = new Stack<(MediaNode Node,string Parent)>();
        foreach (var root in _roots) pending.Push((root, string.Empty));
        while (pending.TryPop(out var entry))
        {
            if (paths.ContainsKey(entry.Node)) continue;
            var path = entry.Parent.Length == 0 ? entry.Node.Name : entry.Parent + " › " + entry.Node.Name;
            paths[entry.Node] = path;
            foreach (var child in entry.Node.Children) pending.Push((child, path));
        }
        return paths;
    }
    private void NotifyResults()
    {
        OnPropertyChanged(nameof(ResultsTitle)); OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(PositionText)); OnPropertyChanged(nameof(EmptyText));
    }

    internal bool TryResolveSelected(out MediaItem item, out MediaNode owner)
    {
        item = null!; owner = null!;
        if (_disposed || IsBusy || SelectedResult is not { } row) return false;
        if (TryResolveTarget(_roots, _parentalFilter, row, out item, out owner)) return true;
        Invalidate(); return false;
    }
    internal static bool TryResolveTarget(IReadOnlyList<MediaNode> roots, bool parentalFilter, BigModeSearchResult row,
        out MediaItem item, out MediaNode owner)
    {
        item = null!; owner = null!;
        var pending = new Stack<MediaNode>(roots); var visited = new HashSet<MediaNode>();
        var policy = new BigModeVisibilityPolicy(parentalFilter);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            foreach (var candidate in node.Items)
                if (candidate.Id == row.ItemId && ReferenceEquals(candidate, row.Item) && policy.IsItemVisible(candidate) && policy.IsNodeVisible(node))
                { item = candidate; owner = node; return true; }
            foreach (var child in node.Children) pending.Push(child);
        }
        return false;
    }

    public void Navigate(GamepadService.GamepadDirection direction)
    {
        if (_disposed) return;
        if (FocusArea == BigModeSearchFocus.Results)
        {
            var step = direction switch { GamepadService.GamepadDirection.Up => -1, GamepadService.GamepadDirection.Down => 1,
                GamepadService.GamepadDirection.Left => -VisibleResultCount, _ => VisibleResultCount };
            SelectedResultIndex = Results.Count == 0 ? -1 : Math.Clamp(SelectedResultIndex + step, 0, Results.Count - 1);
            return;
        }
        if (FocusArea == BigModeSearchFocus.Scope) { if (direction == GamepadService.GamepadDirection.Left) CurrentCategoryOnly = false; else if (direction == GamepadService.GamepadDirection.Right && CanUseCategory) CurrentCategoryOnly = true; else if (direction is GamepadService.GamepadDirection.Up or GamepadService.GamepadDirection.Down) FocusArea = BigModeSearchFocus.Keyboard; return; }
        if (FocusArea == BigModeSearchFocus.ResultsButton)
        { if (direction == GamepadService.GamepadDirection.Up) FocusKeyboard(); else FocusResults(); return; }
        var index = SelectedKeyIndex;
        if (direction == GamepadService.GamepadDirection.Up && index < KeyboardColumns) { FocusArea = BigModeSearchFocus.Scope; return; }
        if (direction == GamepadService.GamepadDirection.Down && index >= Keys.Count - KeyboardColumns) { FocusArea = BigModeSearchFocus.ResultsButton; return; }
        SelectedKeyIndex = direction switch
        {
            GamepadService.GamepadDirection.Left => (index + Keys.Count - 1) % Keys.Count,
            GamepadService.GamepadDirection.Right => (index + 1) % Keys.Count,
            GamepadService.GamepadDirection.Up => index - KeyboardColumns,
            _ => Math.Min(Keys.Count - 1, index + KeyboardColumns)
        };
    }
    public void Confirm()
    {
        if (_disposed) return;
        switch (FocusArea)
        {
            case BigModeSearchFocus.Keyboard: if (SelectedKeyIndex >= 0 && SelectedKeyIndex < Keys.Count) PressKey(Keys[SelectedKeyIndex]); break;
            case BigModeSearchFocus.Scope: ToggleScope(); break;
            case BigModeSearchFocus.ResultsButton: FocusResults(); break;
            case BigModeSearchFocus.Results: RequestResultAction?.Invoke(false); break;
        }
    }
    public void Details() { if (_disposed) return; if (IsResultsFocused) RequestResultAction?.Invoke(true); else ToggleLayer(); }
    public void Back() { if (_disposed) return; if (IsResultsFocused) FocusKeyboard(); else RequestClose?.Invoke(); }
    public void ToggleLayer() { if (_disposed) return; _symbols = !_symbols; BuildKeys(); }
    public void PressKey(BigModeSearchKey key)
    {
        if (_disposed) return;
        switch (key.Kind)
        {
            case BigModeSearchKeyKind.Space: Insert(" "); break;
            case BigModeSearchKeyKind.Backspace: Backspace(); break;
            case BigModeSearchKeyKind.Clear: SearchText = string.Empty; MoveCaret(0); break;
            case BigModeSearchKeyKind.Layer: ToggleLayer(); break;
            case BigModeSearchKeyKind.Case: _upper = !_upper; BuildKeys(); break;
            default: Insert(key.Text); break;
        }
    }
    public void Insert(string text)
    {
        if (_disposed) return;
        var start = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, SearchText.Length);
        var end = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), start, SearchText.Length);
        if (start == end) start = end = Math.Clamp(CaretIndex, 0, SearchText.Length);
        SearchText = SearchText[..start] + text + SearchText[end..]; MoveCaret(start + text.Length);
    }
    public void Backspace()
    {
        if (_disposed) return;
        var start = Math.Clamp(Math.Min(SelectionStart, SelectionEnd), 0, SearchText.Length);
        var end = Math.Clamp(Math.Max(SelectionStart, SelectionEnd), start, SearchText.Length);
        if (start == end)
        {
            end = Math.Clamp(CaretIndex, 0, SearchText.Length);
            if (end == 0) return;
            var elements = StringInfo.ParseCombiningCharacters(SearchText[..end]); start = elements[^1];
        }
        SearchText = SearchText.Remove(start, end - start); MoveCaret(start);
    }
    private void MoveCaret(int value) { CaretIndex = value; SelectionStart = value; SelectionEnd = value; }
    private void BuildKeys()
    {
        var german = (Strings.Culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "de";
        var text = _symbols ? "1234567890:\"'()=<>-._,/\\!?&|+*@#$%[]{};~" : german
            ? "qwertzuiopasdfghjklöyxcvbnmäüß:\"()=<>.-_" : "qwertyuiopasdfghjkl'zxcvbnm,-.äöüßéèáàñç";
        var keys = text.Select(c => new BigModeSearchKey(_upper ? c.ToString().ToUpperInvariant() : c.ToString(), _upper ? c.ToString().ToUpperInvariant() : c.ToString())).ToList();
        keys.Add(new("⇧", "", BigModeSearchKeyKind.Case));
        keys.Add(new(_symbols ? "ABC" : "123", "", BigModeSearchKeyKind.Layer));
        keys.Add(new(T("BigModeSearch_Space", "Space"), "", BigModeSearchKeyKind.Space));
        keys.Add(new("⌫", "", BigModeSearchKeyKind.Backspace));
        keys.Add(new(T("BigModeSearch_Clear", "Clear"), "", BigModeSearchKeyKind.Clear));
        foreach (var c in ":\"()=") keys.Add(new(c.ToString(), c.ToString()));
        var selected = SelectedKeyIndex;
        Keys.ReplaceAll(keys);
        SelectedKeyIndex = Math.Clamp(selected, 0, Keys.Count - 1);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Interlocked.Increment(ref _revision);
        _request?.Cancel(); _request = null; PendingSearch = Task.CompletedTask;
        _observer?.Dispose(); _observer = null;
        MediaSortHelper.TitleSortRuleChanged -= Invalidate;
        Results.Clear(); Keys.Clear(); SelectedResultIndex = -1; _preferredItemId = null;
        RequestResultAction = null; RequestClose = null;
    }
    internal static string T(string key, string fallback) => Strings.ResourceManager.GetString(key, Strings.Culture) is { Length: > 0 } value ? value : fallback;
}
