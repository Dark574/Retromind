using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.GameSystems;
using Retromind.Services.Scrapers;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel handling the bulk scraping process for a collection of media items.
/// </summary>
public partial class BulkScrapeViewModel : ViewModelBase, IDisposable
{
    private const int MaxConcurrentRequests = 4;
    private const int RequestDelayMs = 250;
    
    private readonly Func<string, CancellationToken, Task<IMetadataProvider?>> _getProviderAsync;
    private readonly Func<Func<Task>, Task> _invokeOnUiAsync;
    private readonly Action<Action> _postToUi;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly object _runGate = new();
    private bool _runActive;
    private int _runId;
    private readonly MediaNode _rootNode;
    private readonly ObservableCollection<MediaNode> _libraryRoots;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _cancellationTokenSource;
    private volatile bool _disposed;

    // Buffer for logs to prevent UI flooding
    private readonly StringBuilder _logBuffer = new();
    private readonly Lock _logLock = new(); // .NET 9 Lock object

    [ObservableProperty] 
    private ScraperConfig? _selectedScraper;

    private bool _isBusy;

    // Dispose/final cleanup can clear the backing state silently after the view has detached.
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    [ObservableProperty] 
    private double _progressValue;

    [ObservableProperty] 
    private string _statusMessage = T("BulkScrape.Ready", "Ready.");

    [ObservableProperty] 
    private string _logText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private BulkScrapeRunSummary? _summary;

    [ObservableProperty]
    private BulkScrapeRunStatus _runStatus = BulkScrapeRunStatus.NotStarted;

    public bool HasSummary => Summary != null;
    public string SummaryText => Summary is not { } summary ? "" : string.Format(
        T(summary.CollectionComplete ? "BulkScrape.Summary" : "BulkScrape.PartialSummary",
            summary.CollectionComplete
                ? "Found: {0} · Completed: {1} · Not processed: {2}\nImported: {3} · No safe match: {4} · Skipped: {5} · Failed: {6}"
                : "Found so far (collection incomplete): {0} · Completed: {1} · Not processed: {2}\nImported: {3} · No safe match: {4} · Skipped: {5} · Failed: {6}"),
        summary.Total, summary.Completed, summary.NotProcessed, summary.Imported,
        summary.NoSafeMatch, summary.Skipped, summary.Failed);

    public BulkScrapeViewModel(
        MediaNode node,
        AppSettings settings,
        MetadataService metadataService,
        ObservableCollection<MediaNode>? libraryRoots = null)
        : this(node, settings, metadataService.GetProviderAsync, UiThreadHelper.InvokeAsync, UiThreadHelper.Post,
            libraryRoots: libraryRoots)
    {
    }

    internal BulkScrapeViewModel(
        MediaNode node, AppSettings settings,
        Func<string, CancellationToken, Task<IMetadataProvider?>> getProviderAsync,
        Func<Func<Task>, Task> invokeOnUiAsync, Action<Action> postToUi,
        Func<int, CancellationToken, Task>? delayAsync = null,
        ObservableCollection<MediaNode>? libraryRoots = null)
    {
        _rootNode = node;
        _settings = settings;
        _getProviderAsync = getProviderAsync;
        _invokeOnUiAsync = invokeOnUiAsync;
        _postToUi = postToUi;
        _delayAsync = delayAsync ?? Task.Delay;
        _libraryRoots = libraryRoots ?? new ObservableCollection<MediaNode> { node };

        InitializeScrapers();

        StartCommand = new AsyncRelayCommand(StartScrapingAsync);
        CancelCommand = new RelayCommand(CancelOperation);
    }

    public ObservableCollection<ScraperConfig> AvailableScrapers { get; } = new();

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }

    // Applying a safe match must explicitly distinguish changes, policy/no-change skips and failures.
    public Func<MediaItem, ScraperSearchResult, Task<BulkScrapeImportOutcome>>? OnItemScrapedAsync;

    // Called only after the user presses Start and before the first item can be changed.
    public Func<Task<bool>>? OnBeforeStartAsync;

    private void InitializeScrapers()
    {
        AvailableScrapers.Clear();
        foreach (var scraper in _settings.Scrapers.Where(s =>
                     s.Type != ScraperType.None &&
                     s.Type != ScraperType.EmuMovies))
        {
            AvailableScrapers.Add(scraper);
        }

        if (AvailableScrapers.Count > 0)
        {
            SelectedScraper = AvailableScrapers[0];
        }
    }

    private async Task StartScrapingAsync()
    {
        var selectedScraper = SelectedScraper;
        if (selectedScraper == null) return;
        CancellationTokenSource cancellationTokenSource;
        int runId;
        lock (_runGate)
        {
            if (_disposed || _runActive) return;
            _runActive = true;
            runId = ++_runId;
            _cancellationTokenSource = cancellationTokenSource = new CancellationTokenSource();
        }

        var token = cancellationTokenSource.Token;
        var allItems = new List<BulkScrapeTarget>();
        var progress = new BulkScrapeRunProgress();
        var collectionComplete = false;
        var completion = BulkScrapeRunStatus.NotStarted;
        string? providerStopMessage = null;
        var phase = "Preparation";
        Timer? logUpdateTimer = null;
        try
        {
            await UpdateUiAsync(() =>
            {
                IsBusy = true;
                ProgressValue = 0;
                Summary = null;
                RunStatus = BulkScrapeRunStatus.Running;
                StatusMessage = T("BulkScrape.Preparing", "Preparing...");
            });
            ClearLog(runId);
            AppendLog($"Starting bulk scrape with {selectedScraper.Name}...", runId);
            token.ThrowIfCancellationRequested();
            if (OnBeforeStartAsync != null && !await OnBeforeStartAsync())
                return;
            token.ThrowIfCancellationRequested();

            completion = BulkScrapeRunStatus.Running;
            phase = "Collecting items";
            await CollectItemsRecursiveSnapshotAsync(_rootNode, allItems,
                new HashSet<MediaNode>(ReferenceEqualityComparer.Instance),
                new HashSet<MediaItem>(ReferenceEqualityComparer.Instance), token);
            collectionComplete = true;
            AppendLog($"Found {allItems.Count} media items in total.", runId);
            await UpdateUiAsync(() =>
            {
                Summary = progress.Snapshot(allItems.Count, collectionComplete);
                StatusMessage = T("BulkScrape.Connecting", "Connecting to provider...");
            });

            phase = "Connecting to provider";
            var provider = await _getProviderAsync(selectedScraper.Id, token);
            token.ThrowIfCancellationRequested();
            if (provider == null)
                throw new InvalidOperationException("Could not load or connect to the provider.");

            phase = "Processing items";
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = selectedScraper.Type is ScraperType.ScreenScraper or ScraperType.TheGamesDB
                    ? 1 : MaxConcurrentRequests,
                CancellationToken = token
            };
            logUpdateTimer = new Timer(_ => FlushLogBufferToUi(runId), null, 500, 500);
            await Parallel.ForEachAsync(allItems, options, async (target, itemToken) =>
            {
                BulkScrapeItemOutcome? outcome;
                try
                {
                    if (progress.Completed % 5 == 0)
                        await UpdateUiAsync(() => StatusMessage = string.Format(
                            T("BulkScrape.Processing", "Processing: {0}"), target.Item.Title));
                    outcome = await ProcessTargetAsync(provider, target, itemToken);
                }
                catch (OperationCanceledException) when (itemToken.IsCancellationRequested)
                {
                    return; // Unfinished, including an interrupted search/import; never count as a technical failure.
                }
                catch (MetadataQuotaExceededException ex)
                {
                    Interlocked.CompareExchange(ref providerStopMessage, ex.Message, null);
                    AppendLogBuffer($"[LIMIT] {ex.Message}");
                    cancellationTokenSource.Cancel();
                    return;
                }
                catch (Exception ex)
                {
                    AppendLogBuffer($"[ERROR] {target.Item.Title}: {ex.Message}");
                    outcome = BulkScrapeItemOutcome.Failed;
                }

                // A callback that really finished keeps its outcome even if cancellation arrived during it.
                if (outcome.HasValue)
                {
                    var current = progress.Complete(target.Item, outcome.Value);
                    if (current % 10 == 0 || current == allItems.Count)
                        await UpdateUiAsync(() => UpdateProgress(progress, allItems.Count, collectionComplete));
                }
                if (itemToken.IsCancellationRequested) return;
                try { await _delayAsync(RequestDelayMs, itemToken); }
                catch (OperationCanceledException) when (itemToken.IsCancellationRequested) { }
            });
            completion = token.IsCancellationRequested ? BulkScrapeRunStatus.Cancelled
                : progress.Snapshot(allItems.Count, collectionComplete).Failed > 0
                    ? BulkScrapeRunStatus.CompletedWithErrors : BulkScrapeRunStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            completion = BulkScrapeRunStatus.Cancelled;
            if (providerStopMessage == null) AppendLogBuffer("Scraping operation cancelled by user.");
        }
        catch (MetadataQuotaExceededException ex)
        {
            providerStopMessage = ex.Message;
            AppendLogBuffer($"[LIMIT] {ex.Message}");
            completion = BulkScrapeRunStatus.ProviderLimitReached;
        }
        catch (Exception ex)
        {
            completion = BulkScrapeRunStatus.Failed;
            AppendLogBuffer($"Critical Error ({phase}): {ex.Message}");
        }
        finally
        {
            try
            {
                logUpdateTimer?.Dispose();
                if (providerStopMessage != null && completion != BulkScrapeRunStatus.Failed)
                    completion = BulkScrapeRunStatus.ProviderLimitReached;
                FlushLogBufferToUi(runId);
                await UpdateUiAsync(() =>
                {
                    RunStatus = completion;
                    Summary = completion == BulkScrapeRunStatus.NotStarted ? null : progress.Snapshot(allItems.Count, collectionComplete);
                    if (Summary != null) ProgressValue = Summary.Total == 0 ? 0 : 100d * Summary.Completed / Summary.Total;
                    StatusMessage = CompletionMessage(completion);
                    IsBusy = false;
                });
            }
            finally
            {
                // The run owns disposal, even if the dialog closes or a UI update itself fails.
                lock (_runGate)
                {
                    _isBusy = false;
                    if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                        _cancellationTokenSource = null;
                    cancellationTokenSource.Dispose();
                    _runActive = false;
                }
            }
        }
    }

    private void UpdateProgress(BulkScrapeRunProgress progress, int total, bool collectionComplete)
    {
        Summary = progress.Snapshot(total, collectionComplete);
        ProgressValue = total == 0 ? 0 : 100d * Summary.Completed / total;
    }

    private static string CompletionMessage(BulkScrapeRunStatus status) => status switch
    {
        BulkScrapeRunStatus.Completed => T("BulkScrape.Completed", "Processing finished."),
        BulkScrapeRunStatus.CompletedWithErrors => T("BulkScrape.CompletedWithErrors", "Processing finished with technical errors."),
        BulkScrapeRunStatus.Cancelled => T("BulkScrape.Cancelled", "Processing cancelled by user."),
        BulkScrapeRunStatus.ProviderLimitReached => T("BulkScrape.ProviderLimit", "Stopped because the provider limit was reached."),
        BulkScrapeRunStatus.Failed => T("BulkScrape.Failed", "Processing failed. See the log for details."),
        _ => T("BulkScrape.NotStarted", "Processing was not started.")
    };

    private async Task<BulkScrapeItemOutcome?> ProcessTargetAsync(
        IMetadataProvider provider, BulkScrapeTarget target, CancellationToken token)
    {
        var item = target.Item;
        var gameSystemId = GameSystemResolver.ResolveForItem(item, target.ParentNode, _libraryRoots);
        var lookup = await GameMetadataLookupHelper.SearchAsync(provider, item.Title, gameSystemId,
            item.GetPrimaryLaunchPath(), token, useBulkSearch: true);
        token.ThrowIfCancellationRequested();
        var results = lookup.Results;
        var decision = lookup.IsExactMatch ? null : ScraperMatchEvaluator.SelectBestMatch(
            item.Title, results, item.Platform, item.ReleaseDate);
        var match = lookup.IsExactMatch ? results.SingleOrDefault()
            : decision?.Status == ScraperMatchStatus.Match ? decision.BestCandidate : null;
        if (match != null)
        {
            if (provider is IMetadataResultEnricher enricher)
                await enricher.EnrichAsync(match, MetadataEnrichmentRequest.ForBulk(item, _settings.ScraperImport), token);
            AppendLogBuffer(lookup.IsExactMatch ? $"[HASH] {item.Title} -> {match.Title}"
                : $"[MATCH] {item.Title} -> {match.Title} (confidence: {Math.Min(decision!.Score, 1):P0})");
            BulkScrapeImportOutcome? imported = null;
            await _invokeOnUiAsync(async () =>
            {
                if (_disposed) return;
                token.ThrowIfCancellationRequested();
                if (OnItemScrapedAsync == null)
                    throw new InvalidOperationException("No import callback is attached.");
                imported = await OnItemScrapedAsync(item, match);
            });
            if (!imported.HasValue) return null;
            AppendLogBuffer($"[{imported.Value.ToString().ToUpperInvariant()}] {item.Title}");
            return imported.Value switch
            {
                BulkScrapeImportOutcome.Imported => BulkScrapeItemOutcome.Imported,
                BulkScrapeImportOutcome.Skipped => BulkScrapeItemOutcome.Skipped,
                BulkScrapeImportOutcome.Failed => BulkScrapeItemOutcome.Failed,
                _ => throw new InvalidOperationException("Unknown import outcome.")
            };
        }

        var candidateTitle = decision?.BestCandidate?.Title;
        if (decision?.Status == ScraperMatchStatus.Ambiguous && !string.IsNullOrWhiteSpace(candidateTitle))
            AppendLogBuffer($"[AMBIGUOUS] {item.Title}: {candidateTitle} {Math.Min(decision.Score, 1):P0} | " +
                $"{decision.RunnerUpCandidate?.Title ?? "?"} {Math.Min(decision.RunnerUpScore ?? 0, 1):P0}");
        else if (!string.IsNullOrWhiteSpace(candidateTitle))
            AppendLogBuffer($"[MISS] No safe match for: {item.Title} (best: {candidateTitle})");
        else
            AppendLogBuffer($"[MISS] No results for: {item.Title}");
        return BulkScrapeItemOutcome.NoSafeMatch;
    }

    private Task UpdateUiAsync(Action action)
    {
        if (_disposed) return Task.CompletedTask;
        return _invokeOnUiAsync(() =>
        {
            if (!_disposed) action();
            return Task.CompletedTask;
        });
    }

    private void PostUi(Action action, int runId) => _postToUi(() =>
    {
        if (!_disposed && runId == _runId) action();
    });

    private void CancelOperation()
    {
        int runId;
        lock (_runGate)
        {
            if (_disposed || _cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;
            runId = _runId;
            _cancellationTokenSource.Cancel();
        }
        PostUi(() =>
        {
            if (IsBusy && RunStatus == BulkScrapeRunStatus.Running)
                StatusMessage = T("BulkScrape.Stopping", "Stopping...");
        }, runId);
        AppendLog("Cancellation requested...", runId);
    }

    private async Task CollectItemsRecursiveSnapshotAsync(MediaNode node, List<BulkScrapeTarget> list,
        HashSet<MediaNode> visited, HashSet<MediaItem> itemsSeen, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!visited.Add(node)) return;
        List<MediaItem> items = new();
        List<MediaNode> children = new();
        await UpdateUiAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            items = node.Items.ToList();
            children = node.Children.ToList();
        });
        token.ThrowIfCancellationRequested();
        foreach (var item in items)
            if (itemsSeen.Add(item)) list.Add(new BulkScrapeTarget(item, node));
        foreach (var child in children)
            await CollectItemsRecursiveSnapshotAsync(child, list, visited, itemsSeen, token);
    }

    private sealed record BulkScrapeTarget(MediaItem Item, MediaNode ParentNode);

    // --- High Performance Logging ---

    private void ClearLog(int runId)
    {
        lock (_logLock)
        {
            _logBuffer.Clear();
        }

        // ensure UI thread
        PostUi(() => LogText = "", runId);
    }

    /// <summary>
    /// Direct log update (use only for low-frequency messages like Start/End)
    /// </summary>
    private void AppendLog(string msg, int runId)
    {
        AppendLogBuffer(msg);
        FlushLogBufferToUi(runId);
    }

    /// <summary>
    /// Buffered log update (safe to call from high-frequency loops)
    /// </summary>
    private void AppendLogBuffer(string msg)
    {
        lock (_logLock)
        {
            if (_disposed) return;
            _logBuffer.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        }
    }

    /// <summary>
    /// Pushes the buffered logs to the UI property.
    /// Should be called via Timer or Dispatcher.
    /// </summary>
    private void FlushLogBufferToUi(int runId)
    {
        if (_disposed || runId != _runId)
            return;

        string newLogChunk = "";
        lock (_logLock)
        {
            if (_disposed || runId != _runId || _logBuffer.Length == 0) return;
            newLogChunk = _logBuffer.ToString();
            _logBuffer.Clear();
        }

        if (string.IsNullOrEmpty(newLogChunk))
            return;

        PostUi(() =>
        {
            // Consider limiting total log size here to avoid OOM with 30k items
            if (LogText.Length > 50000)
            {
                LogText = "..." + LogText.Substring(LogText.Length - 40000);
            }

            LogText += newLogChunk;
        }, runId);
    }

    public void Dispose()
    {
        lock (_runGate)
        {
            if (_disposed) return;
            _disposed = true;
            _isBusy = false; // Detached UI must receive no further property notifications.
            _cancellationTokenSource?.Cancel(); // The owning run disposes it in finally.
        }
        lock (_logLock) _logBuffer.Clear();
    }
}
