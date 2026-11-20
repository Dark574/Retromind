using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

internal sealed class DesktopContentSession : IDisposable
{
    internal delegate Task<DesktopMediaProjectionBuilder.Projection> BuildProjection(
        MediaNode node, ObservableCollection<MediaNode> roots, bool filterProtected, CancellationToken token);

    private readonly BuildProjection _buildProjection;
    private readonly IErrorService _errorService;
    private readonly Dictionary<string, string> _selectedItemsByNode = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nodesWithClearedSelection = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loadCancellation;
    private TaskCompletionSource<bool>? _loadCompletion;
    private string _searchText = string.Empty;
    private bool _onlyFavorites;
    private IReadOnlyCollection<string>? _searchScope;
    private string? _searchReturnNodeId;
    private string? _searchReturnItemId;
    private string? _pendingSearchItemId;
    private bool _disposed;

    internal DesktopContentSession(IErrorService errorService, BuildProjection? buildProjection = null)
    {
        _errorService = errorService;
        _buildProjection = buildProjection ?? DesktopMediaProjectionBuilder.BuildAsync;
    }

    internal MediaAreaViewModel? MediaArea { get; private set; }
    internal SearchAreaViewModel? SearchArea { get; private set; }
    internal bool OnlyFavorites => SearchArea?.OnlyFavorites ?? MediaArea?.OnlyFavorites ?? _onlyFavorites;
    internal bool HasMediaLoadError { get; private set; }

    internal event Action<object?>? ContentChanged;
    internal event Action? MediaLoadErrorChanged;
    internal event Action<MediaItem>? PlayRequested;
    internal event PropertyChangedEventHandler? MediaPropertyChanged;
    internal event PropertyChangedEventHandler? SearchPropertyChanged;
    internal event Action? ItemsChanged;

    internal Task LoadMediaAsync(
        MediaNode? node, ObservableCollection<MediaNode> roots, bool filterProtected,
        Func<MediaNode, (double ItemWidth, string? FallbackItemId)?> getCurrentOptions)
    {
        if (_disposed)
            return Task.CompletedTask;

        CancelMediaLoad();
        var searchText = MediaArea?.SearchText ?? _searchText;
        var onlyFavorites = MediaArea?.OnlyFavorites ?? _onlyFavorites;
        var status = MediaArea?.Node.Id == node?.Id ? MediaArea?.SelectedStatus : null;
        DetachSearch();
        DetachMedia();

        if (node == null)
        {
            ContentChanged?.Invoke(null);
            return Task.CompletedTask;
        }

        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadCompletion = completion;

        return Task.Run(async () =>
        {
            var phase = "BuildProjection";
            try
            {
                token.ThrowIfCancellationRequested();
                var (items, plan) = await _buildProjection(node, roots, filterProtected, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                phase = "PublishMediaView";
                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (!IsCurrent(completion, token) || getCurrentOptions(node) is not { } options)
                    {
                        completion.TrySetCanceled();
                        return;
                    }

                    DesktopMediaProjectionBuilder.ApplyRandomizationPlan(plan);
                    if (!IsCurrent(completion, token))
                        return;

                    var displayNode = new MediaNode(node.Name)
                    {
                        Id = node.Id,
                        GameSystemId = node.GameSystemId,
                        Items = new ObservableCollection<MediaItem>(items),
                        Assets = node.Assets,
                        LogoFallbackEnabled = node.LogoFallbackEnabled,
                        WallpaperFallbackEnabled = node.WallpaperFallbackEnabled,
                        VideoFallbackEnabled = node.VideoFallbackEnabled,
                        MarqueeFallbackEnabled = node.MarqueeFallbackEnabled
                    };
                    var media = new MediaAreaViewModel(displayNode, options.ItemWidth, searchText, onlyFavorites, status);
                    MediaArea = media;
                    media.RequestPlay += OnPlayRequested;
                    media.PropertyChanged += OnMediaPropertyChanged;
                    media.FilteredItems.CollectionChanged += OnMediaItemsChanged;
                    RestoreMediaSelection(media, options.FallbackItemId);

                    if (!IsCurrent(completion, token))
                        return;

                    ContentChanged?.Invoke(media);
                    completion.TrySetResult(true);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                try
                {
                    var published = await UiThreadHelper.InvokeAsync(() =>
                    {
                        if (!IsCurrent(completion, token))
                            return false;

                        DetachMedia();
                        ContentChanged?.Invoke(null);
                        if (!IsCurrent(completion, token))
                            return false;

                        SetMediaLoadError(true);
                        return true;
                    }).ConfigureAwait(false);

                    if (published)
                        ReportMediaLoadFailure(node, phase, ex);
                }
                catch (Exception outputException)
                {
                    Debug.WriteLine($"[UpdateContent] Could not surface media load failure: {outputException.GetType().Name}");
                }
                finally
                {
                    completion.TrySetResult(false);
                }
            }
        });
    }

    internal async Task<bool> AwaitMediaLoadAsync()
    {
        if (_loadCompletion?.Task is not { } task)
            return false;

        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Debug.WriteLine("[UpdateContent] Timed out waiting for the current media view.");
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    internal void OpenSearch(string? returnNodeId, Func<SearchAreaViewModel> createSearch)
    {
        if (_disposed || SearchArea != null)
            return;

        CancelMediaLoad();
        if (MediaArea is { } media)
        {
            _searchText = media.SearchText ?? string.Empty;
            _onlyFavorites = media.OnlyFavorites;
        }
        _searchReturnNodeId = returnNodeId;
        _searchReturnItemId = MediaArea?.SelectedMediaItem?.Id;
        _pendingSearchItemId = _searchReturnItemId;
        DetachMedia();

        var search = createSearch();
        search.OnlyFavorites = _onlyFavorites;
        search.SearchText = _searchText;
        if (_searchScope != null)
            search.ApplyScopeSelection(_searchScope);
        SearchArea = search;
        search.RequestPlay += OnPlayRequested;
        search.PropertyChanged += OnSearchPropertyChanged;
        search.SearchResults.CollectionChanged += OnSearchResultsChanged;
        ContentChanged?.Invoke(search);
    }

    internal (string? NodeId, string? SelectedItemId, string? DesiredItemId) CloseSearch()
    {
        var itemId = SearchArea?.SelectedMediaItem?.Id;
        var destination = (_searchReturnNodeId, itemId, itemId ?? _searchReturnItemId);
        DetachSearch();
        _searchReturnNodeId = null;
        _searchReturnItemId = null;
        return destination;
    }

    internal void SetMediaFilters(string text, bool onlyFavorites)
    {
        _searchText = text;
        _onlyFavorites = onlyFavorites;
        if (MediaArea is { } media)
        {
            media.SearchText = text;
            media.OnlyFavorites = onlyFavorites;
            media.SelectedStatus = null;
        }
    }

    internal void ClearPendingSearchSelection() => _pendingSearchItemId = null;

    internal bool TryClearSelection()
    {
        if (MediaArea?.SelectedMediaItem != null)
        {
            MediaArea.SelectedMediaItem = null;
            return true;
        }
        if (SearchArea?.SelectedMediaItem != null)
        {
            SearchArea.SelectedMediaItem = null;
            return true;
        }
        return false;
    }

    internal void RememberNodeSelection(string nodeId, string itemId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(itemId))
            return;

        _selectedItemsByNode[nodeId] = itemId;
        _nodesWithClearedSelection.Remove(nodeId);
    }

    internal void RememberNodeDeselection(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return;

        _selectedItemsByNode.Remove(nodeId);
        _nodesWithClearedSelection.Add(nodeId);
    }

    internal void ForgetItemSelection(string itemId)
    {
        foreach (var nodeId in _selectedItemsByNode
                     .Where(pair => string.Equals(pair.Value, itemId, StringComparison.Ordinal))
                     .Select(pair => pair.Key).ToList())
        {
            _selectedItemsByNode.Remove(nodeId);
        }
    }

    private void RestoreMediaSelection(MediaAreaViewModel media, string? fallbackItemId)
    {
        var node = media.Node;
        if (_nodesWithClearedSelection.Contains(node.Id))
            return;

        var hasBookmark = _selectedItemsByNode.TryGetValue(node.Id, out var bookmark);
        var itemId = hasBookmark ? bookmark : fallbackItemId;
        if (string.IsNullOrEmpty(itemId))
            return;

        var item = node.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item != null)
            media.SelectedMediaItem = item;
        else if (hasBookmark)
            _selectedItemsByNode.Remove(node.Id);
    }

    private void ReportMediaLoadFailure(MediaNode node, string phase, Exception exception)
    {
        var context = new Dictionary<string, string>
        {
            ["Phase"] = phase,
            ["ExceptionType"] = exception.GetType().Name
        };
        if (Guid.TryParse(node.Id, out var nodeId))
            context["NodeId"] = nodeId.ToString();

        _errorService.Report(new ErrorReport(
            "Desktop.MediaViewLoadFailed",
            ErrorSeverity.Error,
            nameof(DesktopContentSession),
            "The desktop media view could not be loaded.",
            Context: context));
    }

    private bool IsCurrent(TaskCompletionSource<bool> completion, CancellationToken token) =>
        ReferenceEquals(_loadCompletion, completion) && !token.IsCancellationRequested;

    private void CancelMediaLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _loadCompletion?.TrySetCanceled();
        _loadCompletion = null;
        SetMediaLoadError(false);
    }

    private void SetMediaLoadError(bool value)
    {
        if (HasMediaLoadError == value)
            return;

        HasMediaLoadError = value;
        MediaLoadErrorChanged?.Invoke();
    }

    private void OnPlayRequested(MediaItem item) => PlayRequested?.Invoke(item);

    private void OnMediaItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ItemsChanged?.Invoke();

    private void OnMediaPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (MediaArea is not { } media || !ReferenceEquals(media, sender))
            return;

        if (e.PropertyName == nameof(MediaAreaViewModel.SearchText))
            _searchText = media.SearchText ?? string.Empty;
        else if (e.PropertyName == nameof(MediaAreaViewModel.OnlyFavorites))
            _onlyFavorites = media.OnlyFavorites;

        MediaPropertyChanged?.Invoke(sender, e);
    }

    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SearchArea is not { } search || !ReferenceEquals(search, sender))
            return;

        if (e.PropertyName == nameof(SearchAreaViewModel.SearchText))
            _searchText = search.SearchText ?? string.Empty;
        else if (e.PropertyName == nameof(SearchAreaViewModel.OnlyFavorites))
            _onlyFavorites = search.OnlyFavorites;
        else if (e.PropertyName == nameof(SearchAreaViewModel.SelectedMediaItem) && search.SelectedMediaItem != null)
            ClearPendingSearchSelection();

        SearchPropertyChanged?.Invoke(sender, e);
    }

    private void OnSearchResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SearchArea is { SelectedMediaItem: null } search && _pendingSearchItemId is { Length: > 0 } pendingId)
        {
            var candidate = search.SearchResults.FirstOrDefault(item => item.Id == pendingId);
            if (candidate != null)
                search.SelectedMediaItem = candidate;
        }
        ItemsChanged?.Invoke();
    }

    private void DetachMedia()
    {
        if (MediaArea is not { } media)
            return;

        media.RequestPlay -= OnPlayRequested;
        media.PropertyChanged -= OnMediaPropertyChanged;
        media.FilteredItems.CollectionChanged -= OnMediaItemsChanged;
        media.Dispose();
        MediaArea = null;
    }

    private void DetachSearch()
    {
        if (SearchArea is not { } search)
            return;

        _searchText = search.SearchText ?? string.Empty;
        _onlyFavorites = search.OnlyFavorites;
        _searchScope = search.GetSelectedScopeIdsSnapshot();
        ClearPendingSearchSelection();
        search.RequestPlay -= OnPlayRequested;
        search.PropertyChanged -= OnSearchPropertyChanged;
        search.SearchResults.CollectionChanged -= OnSearchResultsChanged;
        search.Dispose();
        SearchArea = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelMediaLoad();
        DetachSearch();
        DetachMedia();
    }
}
