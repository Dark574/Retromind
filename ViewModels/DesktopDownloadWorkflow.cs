using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.Downloads;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

internal sealed partial class DesktopDownloadWorkflow(
    DesktopStoreWorkspace workspace,
    DesktopGogWorkflow gog,
    DesktopEpicWorkflow epic,
    DownloadManagerService manager,
    GogQueuedDownloadService gogDownloads,
    IErrorService errors)
{
    private CancellationTokenSource? _checkCancellation;
    private Task _checks = Task.CompletedTask;
    private Task _stopTask = Task.CompletedTask;
    private readonly SemaphoreSlim _checkWake = new(0, 1);
    private bool _running;
    private AutomaticUpdateMode _automaticUpdateMode;

    internal void Start()
    {
        if (_running || !_stopTask.IsCompleted)
            return;
        _running = true;
        var settings = workspace.Settings.Downloads;
        _automaticUpdateMode = settings?.AutomaticUpdates ?? AutomaticUpdateMode.Disabled;
        manager.Start(settings, ExecuteAsync, AllItems().Where(IsExcluded).Select(item => item.Id));
        gog.UpdateFound += EnqueueKnownUpdate;
        epic.UpdateFound += EnqueueKnownUpdate;
        StartChecks();
    }

    internal void SettingsChanged()
    {
        var settings = workspace.Settings.Downloads;
        var previousMode = _automaticUpdateMode;
        _automaticUpdateMode = settings?.AutomaticUpdates ?? AutomaticUpdateMode.Disabled;
        manager.UpdateSettings(settings);
        if (_running)
        {
            TransferKnownUpdatesAndStartGogChecks(requestImmediateSweep: previousMode == AutomaticUpdateMode.Disabled);
            if (_checkWake.CurrentCount == 0)
                _checkWake.Release();
        }
    }

    private void StartChecks()
    {
        TransferKnownUpdatesAndStartGogChecks();
        _checkCancellation = new CancellationTokenSource();
        _checks = CheckEpicUpdatesAsync(_checkCancellation.Token);
    }

    private void TransferKnownUpdatesAndStartGogChecks(bool requestImmediateSweep = false)
    {
        if (!IsAutoEnabled)
            return;
        foreach (var item in AllItems())
        {
            if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreAvailableRevision, out var revision) &&
                !string.IsNullOrWhiteSpace(revision))
                EnqueueKnownUpdate(item, revision);
        }
        gog.StartGogUpdateBackgroundLoop(requestImmediateSweep);
    }

    internal event Action QueueChanged
    {
        add => manager.Changed += value;
        remove => manager.Changed -= value;
    }

    internal (DownloadState? Activity, bool NeedsRepair,
        IReadOnlySet<(string ItemId, string? StoreGameId)> PendingGogInstallations) GetQueueStatus() =>
        manager.GetQueueStatus();
    internal bool IsInstalling => manager.IsInstalling;
    internal bool HasStoreActivity => IsInstalling || gog.HasForegroundOperation || epic.HasForegroundOperation;

    internal Task StopAsync() => _running ? _stopTask = StopCoreAsync() : _stopTask;

    private async Task StopCoreAsync()
    {
        _running = false;
        gog.UpdateFound -= EnqueueKnownUpdate;
        epic.UpdateFound -= EnqueueKnownUpdate;
        var stopJobs = manager.StopAsync();
        var stopGog = gog.StopBackgroundChecksAsync();
        _checkCancellation?.Cancel();
        await Task.WhenAll(stopJobs, stopGog, _checks);
        _checkCancellation?.Dispose();
        _checkCancellation = null;
    }

    internal void Release() => manager.Release();

    private async Task CheckEpicUpdatesAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMinutes(5);
                try
                {
                    var eligible = await UiThreadHelper.InvokeAsync(() =>
                        IsAutoEnabled && !workspace.GetOperationInProgress() &&
                        AllItems().Any(item => EpicMediaItemStateHelper.CanCheckUpdates(item) && !IsExcluded(item)));
                    if (eligible && !await epic.HasUpdateSessionAsync(ct).ConfigureAwait(false))
                        delay = TimeSpan.FromMinutes(30);
                    else if (eligible)
                    {
                        var statuses = await epic.GetQueuedUpdateCatalogAsync(ct).ConfigureAwait(false);
                        var missing = await UiThreadHelper.InvokeAsync(() =>
                        {
                            var incomplete = false;
                            if (!IsAutoEnabled || ct.IsCancellationRequested)
                                return false;
                            foreach (var item in AllItems())
                            {
                                if (IsExcluded(item) || !EpicMediaItemStateHelper.CanCheckUpdates(item) ||
                                    EpicMediaItemStateHelper.TryGetGameId(item) is not { } app)
                                    continue;
                                if (!statuses.TryGetValue(app, out var status))
                                {
                                    incomplete = true;
                                    continue;
                                }

                                if (epic.ApplyEpicUpdateCheckMetadata(item, status))
                                {
                                    workspace.MarkLibraryDirtyAndSaveSoon();
                                    workspace.RefreshActions();
                                }
                            }
                            return incomplete;
                        });
                        delay = missing ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(24);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    delay = TimeSpan.FromMinutes(15);
                    try
                    {
                        errors.Report(new ErrorReport("Downloads.EpicUpdateCheckFailed", ErrorSeverity.Warning,
                            nameof(DesktopDownloadWorkflow), "The Epic update catalog could not be checked.",
                            Context: new Dictionary<string, string> { ["ExceptionType"] = ex.GetType().Name }));
                    }
                    catch { /* Diagnostics cannot stop future checks. */ }
                }
                await _checkWake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private bool IsAutoEnabled => _running && _automaticUpdateMode != AutomaticUpdateMode.Disabled;

    private void EnqueueKnownUpdate(MediaItem item, string revision)
    {
        if (!IsAutoEnabled || IsExcluded(item))
            return;

        if (GogMediaItemStateHelper.HasUpdateAvailable(item) &&
            GogMediaItemStateHelper.IsRetromindManagedInstallation(item) &&
            GogMediaItemStateHelper.TryGetGameId(item) is { } gameId)
            manager.Enqueue(item.Id, DownloadStore.Gog, DownloadKind.Update, revision, automatic: true, storeGameId: gameId);
        else if (EpicMediaItemStateHelper.HasUpdateAvailable(item) &&
                 EpicMediaItemStateHelper.TryGetGameId(item) is { } appName)
            manager.Enqueue(item.Id, DownloadStore.Epic, DownloadKind.Update, revision, automatic: true, storeGameId: appName);
    }

    private Task<DownloadOutcome> ExecuteAsync(DownloadExecution execution, CancellationToken ct) =>
        UiThreadHelper.InvokeAsync(async () =>
        {
            var job = execution.Job;
            var item = AllItems().FirstOrDefault(item => item.Id == job.ItemId);
            if (item == null || workspace.FindParentNode(item) == null)
                return new DownloadOutcome(DownloadState.Failed, DownloadReason.GameUnavailable);
            var gameId = job.Store == DownloadStore.Gog
                ? GogMediaItemStateHelper.TryGetGameId(item)
                : EpicMediaItemStateHelper.TryGetGameId(item);
            if (gameId == null || job.StoreGameId != null && job.StoreGameId != gameId)
                return new DownloadOutcome(DownloadState.Failed, DownloadReason.GameUnavailable);
            if (job.Automatic && IsExcluded(item))
            {
                manager.SetAutomaticUpdateExcluded(item.Id, true);
                return new DownloadOutcome(DownloadState.Paused, DownloadReason.Excluded);
            }
            ct.ThrowIfCancellationRequested();
            return job.Store == DownloadStore.Gog
                ? await gog.RunQueuedDownloadAsync(gogDownloads, item, execution, ct)
                : await epic.RunQueuedUpdateAsync(item, execution, ct);
        }).Unwrap();

    private IEnumerable<MediaItem> AllItems()
    {
        var pending = new Stack<MediaNode>(workspace.GetRootItems());
        while (pending.TryPop(out var node))
        {
            foreach (var item in node.Items)
                yield return item;
            foreach (var child in node.Children)
                pending.Push(child);
        }
    }

    private static bool IsExcluded(MediaItem item) =>
        item.CustomFields.GetValueOrDefault(CustomFieldKeyHelper.StoreAutomaticUpdatesExcluded) == "true";
}
