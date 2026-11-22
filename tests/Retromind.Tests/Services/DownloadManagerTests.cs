using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.Services;

public sealed class DownloadManagerTests
{
    [Fact]
    public async Task Restart_RequeuesDownloadAndFlagsInterruptedInstallation()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var download = Job("download", DownloadState.Downloading);
        var install = Job("install", DownloadState.Installing);
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([download, install]);
        }

        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new DownloadSettings(), (_, _) => throw new InvalidOperationException());
        Assert.False(manager.Enqueue("another", DownloadStore.Gog, DownloadKind.Update, automatic: true));
        await manager.StopAsync();

        Assert.Equal([DownloadState.Queued, DownloadState.RepairRequired],
            manager.Snapshot().Select(row => row.Job.State));
        Assert.Equal((null, true), manager.GetQueueStatus());
        manager.Release();
        using var restored = new DownloadQueueStore(path);
        Assert.Equal(DownloadReason.InstallInterrupted, restored.Load()[1].Reason);
    }

    [Fact]
    public async Task Restart_RecoversCancelledInstallationWithoutRequeuingIt()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var oldCancellation = Job("game", DownloadState.Cancelled) with
        {
            Automatic = false, InstallRequested = true, OverrideWindow = true, PayloadReady = true
        };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([oldCancellation, Job("cancelled-transfer", DownloadState.Cancelled)]);
        }

        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException("No job should run."));
        await manager.StopAsync();
        manager.Release();

        Assert.Collection(manager.Snapshot().Select(row => row.Job),
            job => AssertInstallReady(job, oldCancellation.Id, DownloadReason.ManualGogInstallation, payloadReady: true),
            job => Assert.Equal(DownloadState.Cancelled, job.State));
    }

    [Fact]
    public async Task RepairNotice_RequiresConfirmationAndCanBeDismissedPermanently()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var repair = Job("missing-game", DownloadState.RepairRequired);
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([repair]);
        }

        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException("A repair must require user action."));
        try
        {
            Assert.False(manager.Change(repair.Id, DownloadAction.Remove));
            AvaloniaTestDispatcher.EnsureStarted();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var confirm = false;
                var prompts = 0;
                var viewModelTitle = ViewModelBase.T("Downloads.UnavailableGame", "Unavailable game");
                using var viewModel = new DownloadManagerViewModel(manager, _ => null, null, () => null,
                    _ => false, (_, _) => { }, message =>
                    {
                        Assert.Contains(viewModelTitle, message);
                        prompts++;
                        return Task.FromResult(confirm);
                    });
                Assert.True(viewModel.Remove.CanExecute(null));
                await viewModel.Remove.ExecuteAsync(null);
                Assert.Equal(repair.Id, Assert.Single(manager.Snapshot()).Job.Id);

                confirm = true;
                await viewModel.Remove.ExecuteAsync(null);
                Assert.Empty(manager.Snapshot());
                Assert.Equal(2, prompts);
                Assert.False(manager.GetQueueStatus().NeedsRepair);
            });
        }
        finally
        {
            await manager.StopAsync();
            manager.Release();
        }

        using var restored = new DownloadQueueStore(path);
        Assert.Empty(restored.Load());
    }

    [Fact]
    public async Task Queue_DeduplicatesAndWaitsForCancelledWorkBeforeStartingNext()
    {
        using var temp = new TemporaryDirectory();
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var interrupted = Signal();
        var finish = Signal();
        var next = Signal();
        var resumed = Signal();
        var attempts = 0;
        manager.Start(new(), async (work, ct) =>
        {
            if (work.Job.ItemId == "first" && Interlocked.Increment(ref attempts) == 1)
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { interrupted.SetResult(); }
                await finish.Task;
            }
            else if (work.Job.ItemId == "second")
                next.SetResult();
            else
                resumed.SetResult();
            return new DownloadOutcome(DownloadState.Completed);
        });

        Assert.True(manager.Enqueue("first", DownloadStore.Gog, DownloadKind.Update));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(manager.Enqueue("first", DownloadStore.Gog, DownloadKind.Update));
        Assert.True(manager.Enqueue("second", DownloadStore.Epic, DownloadKind.Update));
        var first = manager.Snapshot()[0].Job.Id;
        Assert.True(manager.Change(first, DownloadAction.Pause));
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.Task.IsCompleted);
        finish.SetResult();
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadState.Paused, manager.Snapshot()[0].Job.State);
        Assert.True(manager.Change(first, DownloadAction.Resume));
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        manager.Release();
    }

    [Fact]
    public async Task PauseQueue_InterruptsTransferAndHoldsNewJobsUntilResumed()
    {
        using var temp = new TemporaryDirectory();
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var interrupted = Signal();
        var allowUnwind = Signal();
        var requeued = Signal();
        var lastJobStarted = Signal();
        var starts = new List<string>();
        manager.Start(new DownloadSettings { AutomaticUpdates = AutomaticUpdateMode.DownloadOnly }, async (work, ct) =>
        {
            var itemId = work.Job.ItemId;
            starts.Add(itemId);
            if (starts.Count == 1)
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    interrupted.SetResult();
                    await allowUnwind.Task;
                    throw;
                }
            }
            if (itemId == "new")
                lastJobStarted.SetResult();
            return new DownloadOutcome(DownloadState.Completed);
        });

        try
        {
            Assert.True(manager.Enqueue("transfer", DownloadStore.Gog, DownloadKind.Update));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var activeId = Assert.Single(manager.Snapshot()).Job.Id;
            manager.Changed += () =>
            {
                if (manager.Snapshot().Any(row => row.Job.Id == activeId && row.Job.State == DownloadState.Queued))
                    requeued.TrySetResult();
            };

            Assert.True(manager.Enqueue("individual", DownloadStore.Gog, DownloadKind.Update));
            var individual = manager.Snapshot().Single(row => row.Job.ItemId == "individual").Job;
            Assert.True(manager.Change(individual.Id, DownloadAction.Pause));

            manager.ToggleQueuePause();
            Assert.True(manager.IsQueuePaused);
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.Enqueue("new", DownloadStore.Epic, DownloadKind.Update, automatic: true));
            var pending = manager.Snapshot().Single(row => row.Job.ItemId == "new").Job;
            Assert.False(manager.Change(pending.Id, DownloadAction.Start));

            allowUnwind.SetResult();
            await requeued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([DownloadState.Queued, DownloadState.Paused, DownloadState.Queued],
                manager.Snapshot().Select(row => row.Job.State));

            manager.ToggleQueuePause();
            Assert.False(manager.IsQueuePaused);
            await lastJobStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            allowUnwind.TrySetResult();
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            manager.Release();
        }

        Assert.Equal(["transfer", "transfer", "new"], starts);
        Assert.Equal(DownloadState.Paused, manager.Snapshot().Single(row => row.Job.ItemId == "individual").Job.State);
    }

    [Fact]
    public async Task PauseQueue_LetsCriticalInstallationFinishAndHoldsNextJob()
    {
        using var temp = new TemporaryDirectory();
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var finish = Signal();
        var completed = Signal();
        var next = Signal();
        manager.Changed += () =>
        {
            if (manager.Snapshot().Any(row => row.Job.ItemId == "epic" && row.Job.State == DownloadState.Completed))
                completed.TrySetResult();
        };
        manager.Start(new DownloadSettings { AutomaticUpdates = AutomaticUpdateMode.DownloadAndInstall }, async (work, ct) =>
        {
            if (work.Job.ItemId == "epic")
            {
                Assert.True(work.BeginInstallation());
                entered.SetResult();
                await finish.Task.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
            }
            else
                next.SetResult();
            return new DownloadOutcome(DownloadState.Completed);
        });

        try
        {
            Assert.True(manager.Enqueue("epic", DownloadStore.Epic, DownloadKind.Update, automatic: true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.Enqueue("next", DownloadStore.Gog, DownloadKind.Update));

            manager.ToggleQueuePause();
            Assert.True(manager.IsInstalling);
            finish.SetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DownloadState.Queued, manager.Snapshot().Single(row => row.Job.ItemId == "next").Job.State);

            manager.ToggleQueuePause();
            await next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            finish.TrySetResult();
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            manager.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticExclusion_PausesActiveDownloadAndResumesTheSameJob(bool includeWhileUnwinding)
    {
        using var temp = new TemporaryDirectory();
        var errors = new RecordingErrorService();
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), errors, TimeProvider.System);
        var entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = Signal();
        var allowUnwind = Signal();
        var paused = Signal();
        manager.Changed += () =>
        {
            if (manager.Snapshot().Any(row => row.Job is { State: DownloadState.Paused, Reason: DownloadReason.Excluded }))
                paused.TrySetResult();
        };
        manager.Start(new DownloadSettings { AutomaticUpdates = AutomaticUpdateMode.DownloadOnly }, async (work, ct) =>
        {
            if (entered.TrySetResult(work.Job.Id))
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    interrupted.SetResult();
                    await allowUnwind.Task;
                    throw;
                }
            }
            else
            {
                resumed.SetResult(work.Job.Id);
            }
            return new DownloadOutcome(DownloadState.Completed);
        });

        try
        {
            Assert.True(manager.Enqueue("game", DownloadStore.Gog, DownloadKind.Update, automatic: true));
            var id = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.SetAutomaticUpdateExcluded("game", true);
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(manager.Enqueue("game", DownloadStore.Gog, DownloadKind.Update, automatic: true));

            if (includeWhileUnwinding)
                manager.SetAutomaticUpdateExcluded("game", false);

            allowUnwind.SetResult();
            if (!includeWhileUnwinding)
            {
                await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(manager.Change(id, DownloadAction.Resume));
                manager.SetAutomaticUpdateExcluded("game", false);
            }

            Assert.Equal(id, await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            allowUnwind.TrySetResult();
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            manager.Release();
        }

        Assert.Equal(DownloadState.Completed, Assert.Single(manager.Snapshot()).Job.State);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Restart_ReconcilesSavedExclusionPausesWithTheLibrary()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([
                Job("excluded", DownloadState.Queued),
                Job("included", DownloadState.Paused) with { Reason = DownloadReason.Excluded },
                Job("manual-pause", DownloadState.Paused),
            ]);
        }

        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException("Automatic updates are disabled."),
            excludedItemIds: ["excluded"]);
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        manager.Release();

        using var restored = new DownloadQueueStore(path);
        Assert.Equal(
            [(DownloadState.Paused, DownloadReason.Excluded), (DownloadState.Queued, DownloadReason.None),
                (DownloadState.Paused, DownloadReason.None)],
            restored.Load().Select(job => (job.State, job.Reason)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_QueuedInstallationCanBeRequestedAgain(bool isGog)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var reason = isGog ? DownloadReason.ManualGogInstallation : DownloadReason.EpicCombinedUpdate;
        var job = Job("game", DownloadState.InstallationPending) with
        {
            Store = isGog ? DownloadStore.Gog : DownloadStore.Epic, Reason = reason, PayloadReady = isGog
        };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([Job("busy", DownloadState.Queued) with { Automatic = false }, job]);
        }

        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        manager.Start(new(), async (work, ct) =>
        {
            Assert.Equal("busy", work.Job.ItemId);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new DownloadOutcome(DownloadState.Completed);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.Change(job.Id, DownloadAction.Install));
            Assert.True(manager.Change(job.Id, DownloadAction.Cancel));

            AssertInstallReady(manager.Snapshot()[1].Job, job.Id, reason, payloadReady: isGog);
            Assert.True(manager.Change(job.Id, DownloadAction.Install));
            Assert.Equal(DownloadState.Queued, manager.Snapshot()[1].Job.State);
        }
        finally
        {
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            manager.Release();
        }
    }

    [Fact]
    public async Task Cancel_PreflightPreservesInstallerAndPreventsEnteringCriticalPhase()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var job = Job("game", DownloadState.InstallationPending) with { PayloadReady = true };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([job]);
        }

        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var canEnterInstallation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Start(new(), async (work, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { canEnterInstallation.TrySetResult(work.BeginInstallation()); }
            return new DownloadOutcome(DownloadState.WaitingForGame, DownloadReason.GameActive);
        });
        try
        {
            Assert.True(manager.Change(job.Id, DownloadAction.Install));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(manager.Change(job.Id, DownloadAction.Cancel));
            Assert.False(await canEnterInstallation.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            manager.Release();
        }

        AssertInstallReady(Assert.Single(manager.Snapshot()).Job, job.Id, DownloadReason.ManualGogInstallation, payloadReady: true);
    }

    [Fact]
    public async Task Shutdown_WaitsForCriticalInstallationWithoutCancelling()
    {
        using var temp = new TemporaryDirectory();
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var finish = Signal();
        manager.Start(new DownloadSettings { AutomaticUpdates = AutomaticUpdateMode.DownloadAndInstall }, async (work, ct) =>
        {
            Assert.True(work.BeginInstallation());
            entered.SetResult();
            await finish.Task.WaitAsync(ct);
            return new DownloadOutcome(DownloadState.Completed);
        });
        Assert.True(manager.Enqueue("epic", DownloadStore.Epic, DownloadKind.Update, new string('A', 64), automatic: true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = manager.StopAsync();
        Assert.True(manager.IsInstalling);
        Assert.Equal(DownloadState.Installing, manager.GetQueueStatus().Activity);
        Assert.False(stop.IsCompleted);
        Assert.False(manager.Enqueue("other", DownloadStore.Gog, DownloadKind.Update));
        finish.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadState.Completed, Assert.Single(manager.Snapshot()).Job.State);
        Assert.Null(manager.GetQueueStatus().Activity);
        manager.Release();
    }

    [Fact]
    public async Task Shutdown_CancelsAndPersistsAnActiveDownloadForNextStart()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var drained = Signal();
        manager.Start(new(), async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { drained.SetResult(); }
            return new DownloadOutcome(DownloadState.Completed);
        });
        Assert.True(manager.Enqueue("game", DownloadStore.Gog, DownloadKind.Update));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(drained.Task.IsCompletedSuccessfully);
        manager.Release();
        using var store = new DownloadQueueStore(path);
        Assert.Equal(DownloadState.Queued, Assert.Single(store.Load()).State);
    }

    [Fact]
    public async Task WindowChange_StopsOnlyTheTransferAndAllowsManualOverride()
    {
        using var temp = new TemporaryDirectory();
        var clock = new LocalClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), new RecordingErrorService(), clock);
        var entered = Signal();
        var waiting = Signal();
        var overridden = Signal();
        manager.Changed += () =>
        {
            if (manager.Snapshot().Any(row => row.Job.State == DownloadState.WaitingForWindow))
                waiting.TrySetResult();
        };
        manager.Start(new(), async (work, ct) =>
        {
            if (work.Job.OverrideWindow)
            {
                overridden.TrySetResult();
                return new DownloadOutcome(DownloadState.Completed);
            }
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new DownloadOutcome(DownloadState.Completed);
        });
        Assert.True(manager.Enqueue("game", DownloadStore.Gog, DownloadKind.Update));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DownloadState.Downloading, manager.GetQueueStatus().Activity);
        manager.UpdateSettings(new DownloadSettings { LimitToTimeWindow = true });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(manager.GetQueueStatus().Activity);
        Assert.True(manager.Change(Assert.Single(manager.Snapshot()).Job.Id, DownloadAction.Start));

        await overridden.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        manager.Release();
    }

    private sealed class LocalClock(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory]
    [InlineData(19, false, 1)]
    [InlineData(20, true, 10)]
    [InlineData(0, true, 6)]
    [InlineData(6, false, 14)]
    public void OvernightWindow_UsesLocalBoundaries(int hour, bool allowed, int hoursUntilTransition)
    {
        var settings = new DownloadSettings
        {
            LimitToTimeWindow = true, WindowStart = new TimeOnly(20, 0), WindowEnd = new TimeOnly(6, 0)
        };
        var now = new DateTimeOffset(2026, 11, 21, hour, 0, 0, TimeSpan.Zero);

        Assert.Equal(allowed, DownloadSchedule.IsAllowed(settings, now));
        Assert.Equal(TimeSpan.FromHours(hoursUntilTransition), DownloadSchedule.UntilTransition(settings, now, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task CorruptQueue_IsPreservedAndStopsDispatch()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        await File.WriteAllTextAsync(path, "{broken");
        var errors = new RecordingErrorService();
        var manager = new DownloadManagerService(path, errors, TimeProvider.System);

        manager.Start(new(), (_, _) => throw new InvalidOperationException());

        Assert.True(manager.PersistenceFailed);
        Assert.False(manager.Enqueue("game", DownloadStore.Epic, DownloadKind.Update));
        Assert.Equal("Downloads.QueueUnavailable", Assert.Single(errors.Reports).Code);
        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        manager.Release();
    }

    private static DownloadJob Job(string id, DownloadState state) => new(Guid.NewGuid(), id, DownloadStore.Gog,
        DownloadKind.Update, "", true, false, false, state);

    private static void AssertInstallReady(DownloadJob job, Guid id, DownloadReason reason, bool payloadReady)
    {
        Assert.Equal(id, job.Id);
        Assert.Equal(DownloadState.InstallationPending, job.State);
        Assert.Equal(reason, job.Reason);
        Assert.Equal(payloadReady, job.PayloadReady);
        Assert.False(job.InstallRequested);
        Assert.False(job.OverrideWindow);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
