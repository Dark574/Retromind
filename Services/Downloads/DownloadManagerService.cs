using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Downloads;

public sealed class DownloadManagerService
{
    private static readonly TimeSpan BlockedRetryDelay = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly DownloadQueueStore _store;
    private readonly IErrorService _errors;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, DateTimeOffset> _blockedUntil = new();
    private readonly Dictionary<Guid, DownloadProgress> _progress = new();
    private List<DownloadJob> _jobs = [];
    private DownloadSettings _settings = new();
    private ExecuteDownloadAsync? _execute;
    private Task _worker = Task.CompletedTask;
    private ActiveJob? _active;
    private bool _stopping;
    private bool _loaded;
    private bool _persistenceFailed;

    public DownloadManagerService(IErrorService errors)
        : this(Path.Combine(AppPaths.DataRoot, "State", "downloads.json"), errors, TimeProvider.System) { }

    internal DownloadManagerService(string path, IErrorService errors, TimeProvider time)
    {
        _store = new DownloadQueueStore(path);
        _errors = errors;
        _time = time;
    }

    internal event Action? Changed;
    internal bool PersistenceFailed { get { lock (_gate) return _persistenceFailed; } }
    internal bool IsInstalling { get { lock (_gate) return _active?.Installing == true; } }
    internal (DownloadState? Activity, bool NeedsRepair) GetQueueStatus()
    {
        lock (_gate)
        {
            var activity = _persistenceFailed
                ? null
                : _jobs.Find(job => job.State is DownloadState.Downloading or DownloadState.Installing)?.State;
            return (activity, _jobs.Exists(job => job.State == DownloadState.RepairRequired));
        }
    }

    internal (DownloadJob Job, DownloadProgress Progress)[] Snapshot()
    {
        lock (_gate)
            return _jobs.Select(job => (job, _progress.GetValueOrDefault(job.Id))).ToArray();
    }

    internal void Start(DownloadSettings? settings, ExecuteDownloadAsync execute)
    {
        lock (_gate)
        {
            if (!_worker.IsCompleted || _persistenceFailed)
                return;

            _settings = settings ?? new();
            _execute = execute;
            _stopping = false;
            if (!_loaded)
            {
                try
                {
                    var saved = _store.Load();
                    _jobs = saved.Select(Recover).ToList();
                    if (!_jobs.SequenceEqual(saved))
                        _store.Save(_jobs);
                    _loaded = true;
                }
                catch (Exception ex)
                {
                    FailPersistence(ex);
                    _store.Dispose();
                    return;
                }
            }

            _worker = Task.Run(RunAsync);
            Notify();
        }
    }

    internal void UpdateSettings(DownloadSettings? settings)
    {
        lock (_gate)
        {
            _settings = settings ?? new();
            if (_settings.AutomaticUpdates == AutomaticUpdateMode.DownloadAndInstall &&
                _jobs.Any(IsWaitingForAutomaticEpicInstallation))
            {
                Save(_jobs.Select(job => IsWaitingForAutomaticEpicInstallation(job)
                    ? job with { State = DownloadState.Queued, Reason = DownloadReason.None }
                    : job).ToList());
            }
            Notify();
        }
    }

    private static bool IsWaitingForAutomaticEpicInstallation(DownloadJob job) =>
        job.Automatic && job.State == DownloadState.InstallationPending && job.Reason == DownloadReason.EpicCombinedUpdate;

    internal bool Enqueue(string itemId, DownloadStore store, DownloadKind kind, string revision = "",
        bool automatic = false, string? platform = null, string? storeGameId = null)
    {
        lock (_gate)
        {
            if (!_loaded || _stopping || _persistenceFailed ||
                automatic && _settings.AutomaticUpdates == AutomaticUpdateMode.Disabled)
                return false;

            var job = new DownloadJob(Guid.NewGuid(), itemId, store, kind, revision, automatic,
                false, false, DownloadState.Queued, Platform: platform, StoreGameId: storeGameId);
            if (!DownloadQueueStore.IsValid(job))
                return false;

            var matches = _jobs.Where(existing => existing.ItemId == itemId && existing.Store == store &&
                existing.Kind == kind && existing.Platform == platform).ToArray();
            if (matches.Any(existing => !IsHistory(existing.State) ||
                automatic && existing.Revision == revision))
                return false;

            // Retain the latest terminal result for each title and operation.
            var next = _jobs.Except(matches).ToList();
            if (next.Count == DownloadQueueStore.Capacity)
            {
                var oldestHistory = next.FindIndex(existing => IsHistory(existing.State));
                if (oldestHistory < 0)
                    return false;
                next.RemoveAt(oldestHistory);
            }

            next.Add(job);
            return Save(next);
        }
    }

    internal bool Change(Guid id, DownloadAction action)
    {
        lock (_gate)
        {
            if (_stopping || _persistenceFailed)
                return false;
            var index = _jobs.FindIndex(job => job.Id == id);
            if (index < 0)
                return false;
            var job = _jobs[index];
            if (_active is { } active && active.Id == id)
            {
                if (active.Installing || action is not (DownloadAction.Pause or DownloadAction.Cancel))
                    return false;
                var interrupted = action == DownloadAction.Pause
                    ? job with { State = DownloadState.Paused, Reason = DownloadReason.None }
                    : CancelJob(job);
                if (!Update(interrupted))
                    return false;
                active.InterruptedJob = interrupted;
                Notify();
                return true;
            }

            var next = new List<DownloadJob>(_jobs);
            switch (action)
            {
                case DownloadAction.Earlier when index > 0:
                    (next[index - 1], next[index]) = (next[index], next[index - 1]);
                    break;
                case DownloadAction.Later when index + 1 < next.Count:
                    (next[index + 1], next[index]) = (next[index], next[index + 1]);
                    break;
                case DownloadAction.Remove when IsHistory(job.State):
                case DownloadAction.DiscardRepair when job.State == DownloadState.RepairRequired:
                    next.RemoveAt(index);
                    break;
                case DownloadAction.Pause when IsWaiting(job.State):
                    next[index] = job with { State = DownloadState.Paused, Reason = DownloadReason.None };
                    break;
                case DownloadAction.Cancel when IsWaiting(job.State) ||
                    job.State is DownloadState.Paused or DownloadState.InstallationPending or DownloadState.Failed:
                    next[index] = CancelJob(job);
                    break;
                case DownloadAction.Install when job.State == DownloadState.InstallationPending:
                    next[index] = job with { State = DownloadState.Queued, Reason = DownloadReason.None,
                        Automatic = false, OverrideWindow = true, InstallRequested = true };
                    break;
                case DownloadAction.Repair when job.State == DownloadState.RepairRequired:
                    next[index] = job with { State = DownloadState.Queued, Reason = DownloadReason.None,
                        Automatic = false, OverrideWindow = true, InstallRequested = true, RepairRequested = true };
                    break;
                case DownloadAction.Start when IsWaiting(job.State) || job.State == DownloadState.Paused:
                    next[index] = job with { State = DownloadState.Queued, Reason = DownloadReason.None,
                        Automatic = false, OverrideWindow = true };
                    break;
                case DownloadAction.Resume when job.State == DownloadState.Paused:
                case DownloadAction.Retry when job.State == DownloadState.Failed || IsWaiting(job.State):
                    next[index] = job with { State = DownloadState.Queued, Reason = DownloadReason.None };
                    break;
                default:
                    return false;
            }

            if (!Save(next))
                return false;
            _blockedUntil.Remove(id);
            return true;
        }
    }

    // Calling this method also prevents an in-flight preflight from entering the critical phase.
    internal Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            Notify();
            return _worker;
        }
    }

    internal void Release()
    {
        lock (_gate)
        {
            if (_worker.IsCompleted)
            {
                _store.Dispose();
                _loaded = false;
            }
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                DownloadJob? next;
                DownloadSettings settings;
                TimeSpan? wait;
                lock (_gate)
                {
                    if (_stopping || _persistenceFailed)
                        return;
                    settings = _settings;
                    (next, wait) = SelectJob(settings);
                    if (next != null)
                    {
                        Update(next with { State = DownloadState.Downloading, Reason = DownloadReason.None });
                        if (_persistenceFailed)
                            return;
                        _active = new ActiveJob(next.Id);
                    }
                }

                if (next == null)
                    await WaitForWakeAsync(wait).ConfigureAwait(false);
                else
                    await RunJobAsync(next, settings).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
                FailPersistence(ex);
        }
    }

    private (DownloadJob?, TimeSpan?) SelectJob(DownloadSettings settings)
    {
        var now = _time.GetUtcNow();
        var allowed = IsWindowOpen(settings);
        TimeSpan? wait = null;
        var waitingForWindow = new HashSet<Guid>();
        var hasWindowBlockedJobs = false;
        foreach (var job in _jobs)
        {
            if (!IsWaiting(job.State) || job.Automatic && settings.AutomaticUpdates == AutomaticUpdateMode.Disabled)
                continue;
            if (_blockedUntil.TryGetValue(job.Id, out var retry) && retry > now)
            {
                wait = Earlier(wait, retry - now);
                continue;
            }
            if (allowed || job.OverrideWindow || job.PayloadReady)
                return (job, null);

            hasWindowBlockedJobs = true;
            if (job.State != DownloadState.WaitingForWindow)
                waitingForWindow.Add(job.Id);
        }

        if (waitingForWindow.Count > 0)
        {
            Save(_jobs.Select(job => waitingForWindow.Contains(job.Id)
                ? job with { State = DownloadState.WaitingForWindow, Reason = DownloadReason.None }
                : job).ToList());
        }
        if (hasWindowBlockedJobs)
            wait = Earlier(wait, DownloadSchedule.UntilTransition(settings, now, _time.LocalTimeZone));
        return (null, wait);
    }

    private async Task RunJobAsync(DownloadJob job, DownloadSettings settings)
    {
        using var cancellation = new CancellationTokenSource();
        var active = _active!;
        var mayInstall = job.InstallRequested ||
                         job.Automatic && settings.AutomaticUpdates == AutomaticUpdateMode.DownloadAndInstall;
        var execution = new DownloadExecution(job, settings, mayInstall,
            progress => ReportProgress(active, progress), () => BeginInstallation(active, job, cancellation.Token));
        var work = ExecuteAsync(execution, cancellation.Token);
        while (!work.IsCompleted)
        {
            TimeSpan? wait = null;
            var interrupt = false;
            lock (_gate)
            {
                if (!active.Installing)
                {
                    var state = active.InterruptedJob?.State;
                    if (state == null && (_stopping || _persistenceFailed || job.Automatic &&
                        _settings.AutomaticUpdates == AutomaticUpdateMode.Disabled))
                        state = DownloadState.Queued;
                    if (state == null && !job.OverrideWindow && !job.PayloadReady && !IsWindowOpen(_settings))
                        state = DownloadState.WaitingForWindow;
                    if (state != null)
                    {
                        active.InterruptedJob ??= job with { State = state.Value, Reason = DownloadReason.None };
                        interrupt = true;
                    }
                    else if (!job.OverrideWindow && !job.PayloadReady)
                    {
                        wait = DownloadSchedule.UntilTransition(_settings, _time.GetUtcNow(), _time.LocalTimeZone);
                    }
                }
            }
            if (interrupt)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                break;
            }
            await WaitForWakeAsync(wait, work).ConfigureAwait(false);
        }

        var outcome = await work.ConfigureAwait(false);
        lock (_gate)
        {
            _active = null;
            var finished = active.InterruptedJob ?? (job with { State = outcome.State, Reason = outcome.Reason });
            if (finished is { State: DownloadState.InstallationPending, InstallRequested: true })
                finished = RestorePendingInstallation(finished);
            if (finished.State is DownloadState.WaitingForSpace or DownloadState.WaitingForGame)
                _blockedUntil[job.Id] = _time.GetUtcNow() + BlockedRetryDelay;
            else
                _blockedUntil.Remove(job.Id);

            Update(finished with { PayloadReady = finished.PayloadReady || outcome.PayloadReady });
            Notify();
        }
    }

    private async Task<DownloadOutcome> ExecuteAsync(DownloadExecution execution, CancellationToken token)
    {
        try
        {
            return await _execute!(execution, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new DownloadOutcome(DownloadState.Queued);
        }
        catch (Exception ex)
        {
            Report("Downloads.JobFailed", ex, execution.Job);
            return IsInstalling
                ? new DownloadOutcome(DownloadState.RepairRequired, DownloadReason.InstallInterrupted)
                : new DownloadOutcome(DownloadState.Failed, DownloadReason.OperationFailed);
        }
    }

    private bool BeginInstallation(ActiveJob active, DownloadJob job, CancellationToken token)
    {
        lock (_gate)
        {
            if (_stopping || _persistenceFailed || active.InterruptedJob != null || token.IsCancellationRequested ||
                job.Automatic && _settings.AutomaticUpdates != AutomaticUpdateMode.DownloadAndInstall ||
                job.Store == DownloadStore.Epic && !job.OverrideWindow && !IsWindowOpen(_settings))
                return false;

            if (!Update(job with { State = DownloadState.Installing, Reason = DownloadReason.None }))
                return false;
            active.Installing = true;
            Notify();
            return true;
        }
    }

    private void ReportProgress(ActiveJob active, DownloadProgress progress)
    {
        lock (_gate)
        {
            if (_active != active)
                return;
            _progress[active.Id] = progress;
            var now = _time.GetUtcNow();
            if (now - active.LastNotification >= TimeSpan.FromMilliseconds(250))
            {
                active.LastNotification = now;
                PublishChanged();
            }
        }
    }

    private async Task WaitForWakeAsync(TimeSpan? delay, Task? work = null)
    {
        using var cancellation = new CancellationTokenSource();
        var wake = _wake.WaitAsync(cancellation.Token);
        var timer = delay.HasValue ? Task.Delay(delay.Value, _time, cancellation.Token) : null;
        var triggers = new List<Task> { wake };
        if (timer != null) triggers.Add(timer);
        if (work != null) triggers.Add(work);
        await Task.WhenAny(triggers).ConfigureAwait(false);
        cancellation.Cancel();
        try { await wake.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private bool IsWindowOpen(DownloadSettings settings) => DownloadSchedule.IsAllowed(settings, _time.GetLocalNow());
    private static TimeSpan? Earlier(TimeSpan? first, TimeSpan? second) =>
        !first.HasValue ? second : !second.HasValue || first.Value < second.Value ? first : second;

    private bool Update(DownloadJob job) => Save(_jobs.Select(entry => entry.Id == job.Id ? job : entry).ToList());

    private bool Save(List<DownloadJob> next)
    {
        if (!_loaded || _persistenceFailed)
            return false;
        try
        {
            _store.Save(next);
            _jobs = next;
            var ids = next.Select(job => job.Id).ToHashSet();
            foreach (var id in _progress.Keys.Where(id => !ids.Contains(id)).ToArray())
                _progress.Remove(id);
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            FailPersistence(ex);
            return false;
        }
    }

    private void FailPersistence(Exception ex)
    {
        _persistenceFailed = true;
        Report("Downloads.QueueUnavailable", ex);
        Notify();
    }

    private void Report(string code, Exception ex, DownloadJob? job = null)
    {
        var context = new Dictionary<string, string> { ["ExceptionType"] = ex.GetType().Name };
        if (job != null)
        {
            context["Store"] = job.Store.ToString();
            context["Kind"] = job.Kind.ToString();
            context["JobId"] = job.Id.ToString("N");
        }
        try
        {
            _errors.Report(new ErrorReport(code, ErrorSeverity.Warning, nameof(DownloadManagerService),
                job == null ? "The download queue could not be accessed." : "A download or update job failed.",
                Context: context));
        }
        catch { /* Diagnostics must not interrupt shutdown or recovery. */ }
    }

    private void Notify()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
        PublishChanged();
    }

    private void PublishChanged()
    {
        try { Changed?.Invoke(); }
        catch { /* Presentation must not stop the queue. */ }
    }

    private static bool IsWaiting(DownloadState state) =>
        state is DownloadState.Queued or DownloadState.WaitingForWindow or DownloadState.WaitingForSpace or DownloadState.WaitingForGame;
    private static bool IsHistory(DownloadState state) =>
        state is DownloadState.Completed or DownloadState.Cancelled;

    private static DownloadJob CancelJob(DownloadJob job)
    {
        if (job.InstallRequested && job.State != DownloadState.InstallationPending)
            return RestorePendingInstallation(job);

        return job with
        {
            State = DownloadState.Cancelled, Reason = DownloadReason.None,
            InstallRequested = false, RepairRequested = false
        };
    }

    private static DownloadJob RestorePendingInstallation(DownloadJob job)
    {
        var state = job.RepairRequested ? DownloadState.RepairRequired : DownloadState.InstallationPending;
        var reason = job.RepairRequested ? DownloadReason.InstallInterrupted
            : job.Store == DownloadStore.Gog ? DownloadReason.ManualGogInstallation : DownloadReason.EpicCombinedUpdate;
        return job with
        {
            State = state, Reason = reason, OverrideWindow = false,
            InstallRequested = false, RepairRequested = false
        };
    }

    private static DownloadJob Recover(DownloadJob job) => job.State switch
    {
        DownloadState.Downloading => job with { State = DownloadState.Queued },
        DownloadState.Installing => job with { State = DownloadState.RepairRequired, Reason = DownloadReason.InstallInterrupted },
        DownloadState.Cancelled when job.InstallRequested => RestorePendingInstallation(job),
        _ => job
    };

    private sealed class ActiveJob(Guid id)
    {
        internal Guid Id { get; } = id;
        internal bool Installing { get; set; }
        internal DownloadJob? InterruptedJob { get; set; }
        internal DateTimeOffset LastNotification { get; set; } = DateTimeOffset.MinValue;
    }
}
