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
        var status = manager.GetQueueStatus();
        Assert.Null(status.Activity);
        Assert.True(status.NeedsRepair);
        Assert.Empty(status.PendingGogInstallations);
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
            var awaitingInstallation = manager.GetQueueStatus().PendingGogInstallations;
            if (isGog)
                Assert.Equal(("game", null), Assert.Single(awaitingInstallation));
            else
                Assert.Empty(awaitingInstallation);

            Assert.True(manager.Change(job.Id, DownloadAction.Install));
            Assert.Empty(manager.GetQueueStatus().PendingGogInstallations);
            Assert.True(manager.Change(job.Id, DownloadAction.Cancel));

            AssertInstallReady(manager.Snapshot()[1].Job, job.Id, reason, payloadReady: isGog);
            Assert.Equal(isGog, manager.GetQueueStatus().PendingGogInstallations.Contains(("game", null)));
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

    [Fact]
    public async Task ChangedInstaller_RespectsTheWindowAndRequiresFreshApproval()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var previousRevision = new string('A', 64);
        var latestRevision = new string('B', 64);
        var original = Job("game", DownloadState.InstallationPending) with
        {
            Automatic = false, Revision = previousRevision, PayloadReady = true, PayloadRevision = previousRevision,
            StoreGameId = "gog-game"
        };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([original]);
        }

        var clock = new LocalClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var manager = new DownloadManagerService(path, new RecordingErrorService(), clock);
        var waiting = Signal();
        var ready = Signal();
        var executions = 0;
        manager.Changed += () =>
        {
            var job = Assert.Single(manager.Snapshot()).Job;
            if (job.State == DownloadState.WaitingForWindow)
                waiting.TrySetResult();
            if (job is { State: DownloadState.InstallationPending } && job.PayloadRevision == latestRevision)
                ready.TrySetResult();
        };
        manager.Start(new DownloadSettings { LimitToTimeWindow = true }, (work, _) =>
        {
            Interlocked.Increment(ref executions);
            if (work.Job.InstallRequested)
            {
                Assert.Equal(previousRevision, work.Job.PayloadRevision);
                Assert.True(work.BeginInstallation());
                return Task.FromResult(new DownloadOutcome(
                    DownloadState.Queued, DownloadReason.GogPayloadChanged, RedownloadRequired: true));
            }

            Assert.Null(work.Job.PayloadRevision);
            Assert.False(work.Job.PayloadReady || work.Job.OverrideWindow);
            return Task.FromResult(new DownloadOutcome(DownloadState.InstallationPending,
                DownloadReason.ManualGogInstallation, PayloadReady: true, PayloadRevision: latestRevision));
        });
        try
        {
            Assert.True(manager.Change(original.Id, DownloadAction.Install));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, executions);
            Assert.Equal(DownloadReason.GogPayloadChanged, Assert.Single(manager.Snapshot()).Job.Reason);
            Assert.Empty(manager.GetQueueStatus().PendingGogInstallations);

            manager.UpdateSettings(new DownloadSettings());
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, executions);
            Assert.Equal(("game", "gog-game"), Assert.Single(manager.GetQueueStatus().PendingGogInstallations));
        }
        finally
        {
            await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            manager.Release();
        }

        using var restored = new DownloadQueueStore(path);
        var job = Assert.Single(restored.Load());
        AssertInstallReady(job, original.Id, DownloadReason.ManualGogInstallation, payloadReady: true);
        Assert.Equal(latestRevision, job.PayloadRevision);
        Assert.Equal(latestRevision, job.Revision);
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

    [Theory]
    [InlineData((int)DownloadStore.Gog, (int)DownloadState.InstallationPending, true)]
    [InlineData((int)DownloadStore.Epic, (int)DownloadState.InstallationPending, true)]
    [InlineData((int)DownloadStore.Gog, (int)DownloadState.RepairRequired, true)]
    [InlineData((int)DownloadStore.Epic, (int)DownloadState.RepairRequired, true)]
    [InlineData((int)DownloadStore.Gog, (int)DownloadState.Paused, true)]
    [InlineData((int)DownloadStore.Epic, (int)DownloadState.Queued, true)]
    [InlineData((int)DownloadStore.Gog, (int)DownloadState.Failed, false)]
    public async Task ManualInstallation_CompletesOnlyMatchingUpdatesAndPersistsTheResult(
        int providerValue, int stateValue, bool librarySaved)
    {
        var provider = (DownloadStore)providerValue;
        var state = (DownloadState)stateValue;
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var revision = new string('A', 64);
        var job = Job("game", state) with
        {
            Store = provider, StoreGameId = "store-game", Revision = revision, Platform = "windows",
            PayloadReady = provider == DownloadStore.Gog, InstallRequested = true,
            RepairRequested = true, OverrideWindow = true
        };
        var unrelated = new[]
        {
            job with { Id = Guid.NewGuid(), Revision = new string('B', 64) },
            job with { Id = Guid.NewGuid(), StoreGameId = "other-game" },
            job with { Id = Guid.NewGuid(), ItemId = "other-item" },
            job with { Id = Guid.NewGuid(), Platform = "linux" },
            job with { Id = Guid.NewGuid(), Store = provider == DownloadStore.Gog ? DownloadStore.Epic : DownloadStore.Gog },
            job with { Id = Guid.NewGuid(), State = DownloadState.Cancelled, InstallRequested = false },
            job with { Id = Guid.NewGuid(), State = DownloadState.Completed }
        };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([job, .. unrelated]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException("Automatic updates are disabled."));
        var before = manager.Snapshot().Select(row => row.Job).ToArray();
        try
        {
            manager.CompleteInstalledUpdate(new("game", provider, "store-game", revision, "windows"), librarySaved);

            var completed = manager.Snapshot()[0].Job;
            Assert.Equal(DownloadState.Completed, completed.State);
            Assert.Equal(librarySaved ? DownloadReason.None : DownloadReason.SavePending, completed.Reason);
            Assert.False(completed.InstallRequested || completed.RepairRequested || completed.OverrideWindow);
            Assert.Equal(before.Skip(1), manager.Snapshot().Skip(1).Select(row => row.Job));
        }
        finally { await manager.StopAsync(); manager.Release(); }

        using var restored = new DownloadQueueStore(path);
        Assert.Equal(DownloadState.Completed, restored.Load()[0].State);
    }

    [Fact]
    public async Task ManualInstallation_PreservesOtherPlatformsAndOfflineInstallerJobs()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var revision = new string('A', 64);
        var pending = Job("game", DownloadState.InstallationPending) with { Revision = revision, PayloadReady = true };
        var offline = pending with { Id = Guid.NewGuid(), Kind = DownloadKind.OfflineInstaller, Platform = "linux" };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([pending, offline]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException());
        try
        {
            manager.CompleteInstalledUpdate(new("game", DownloadStore.Gog, "store-game", revision, "linux", "windows"), true);
            Assert.Equal([pending, offline], manager.Snapshot().Select(row => row.Job));
            Assert.NotEmpty(manager.GetQueueStatus().PendingGogInstallations);

            manager.CompleteInstalledUpdate(new("game", DownloadStore.Gog, "store-game", revision, "windows", "windows"), true);
            Assert.Equal(DownloadState.Completed, manager.Snapshot()[0].Job.State);
            Assert.Equal(offline, manager.Snapshot()[1].Job);
            Assert.Empty(manager.GetQueueStatus().PendingGogInstallations);
        }
        finally { await manager.StopAsync(); manager.Release(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ManualInstallation_DuringDownloadReconcilesItsActualPayloadRevision(bool isEpic, bool newerPayload)
    {
        using var temp = new TemporaryDirectory();
        var revision = new string('A', 64);
        var downloadedRevision = newerPayload ? new string('B', 64) : revision;
        var reason = isEpic ? DownloadReason.EpicCombinedUpdate : DownloadReason.ManualGogInstallation;
        var manager = new DownloadManagerService(temp.GetPath("downloads.json"), new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var finish = Signal();
        var settled = Signal();
        manager.Changed += () =>
        {
            if (manager.Snapshot() is [{ Job.State: DownloadState.Completed or DownloadState.InstallationPending }])
                settled.TrySetResult();
        };
        manager.Start(new(), async (execution, _) =>
        {
            entered.TrySetResult();
            await finish.Task;
            // A preflight that was overtaken by a manual install must not install again.
            Assert.False(execution.BeginInstallation());
            return new(DownloadState.InstallationPending, reason,
                PayloadReady: !isEpic, PayloadRevision: isEpic ? null : downloadedRevision,
                ResolvedRevision: isEpic ? downloadedRevision : null);
        });
        try
        {
            var store = isEpic ? DownloadStore.Epic : DownloadStore.Gog;
            Assert.True(manager.Enqueue("game", store, DownloadKind.Update, revision, storeGameId: "store-game"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.CompleteInstalledUpdate(new("game", store, "store-game", revision, "windows"), false);
            finish.TrySetResult();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var result = Assert.Single(manager.Snapshot()).Job;
            Assert.Equal(downloadedRevision, result.Revision);
            Assert.Equal(newerPayload ? DownloadState.InstallationPending : DownloadState.Completed, result.State);
            Assert.Equal(newerPayload ? reason : DownloadReason.SavePending, result.Reason);
            Assert.Equal(!isEpic && newerPayload, manager.GetQueueStatus().PendingGogInstallations.Count > 0);
        }
        finally { finish.TrySetResult(); await manager.StopAsync(); manager.Release(); }
    }

    [Fact]
    public async Task ManualInstallation_DoesNotClaimCompletionWhenQueueCannotBeSaved()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var revision = new string('A', 64);
        var pending = Job("game", DownloadState.InstallationPending) with { Revision = revision, PayloadReady = true };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([pending]);
        }
        var errors = new RecordingErrorService();
        var manager = new DownloadManagerService(path, errors, TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException());
        try
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
            manager.CompleteInstalledUpdate(new("game", DownloadStore.Gog, "store-game", revision, "windows"), true);

            Assert.True(manager.PersistenceFailed);
            Assert.Equal(pending, Assert.Single(manager.Snapshot()).Job);
            Assert.NotEmpty(manager.GetQueueStatus().PendingGogInstallations);
            Assert.Contains(errors.Reports, report => report.Code == "Downloads.QueueUnavailable");
        }
        finally { await manager.StopAsync(); manager.Release(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualInstallation_UnversionedUpdatesAreCompletedByInstallOrUpdateButNotAnUnverifiedRepair(bool repair)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var pending = Job("game", DownloadState.InstallationPending) with { Store = DownloadStore.Epic };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([pending]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException());
        try
        {
            manager.CompleteInstalledUpdate(new("game", DownloadStore.Epic, "game-id", new string('A', 64),
                "windows", CompletesUnversionedUpdates: !repair), true);
            var result = Assert.Single(manager.Snapshot()).Job;
            Assert.Equal(repair ? DownloadState.InstallationPending : DownloadState.Completed, result.State);
        }
        finally { await manager.StopAsync(); manager.Release(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PausedDownloads_AllowExplicitPreparedGogInstallationAndKeepOtherJobsHeld(bool pauseBeforeStart)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var installer = PreparedGogJob("install");
        var automatic = PreparedGogJob("automatic") with { State = DownloadState.Paused };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([installer, automatic]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var begin = Signal();
        var installed = Signal();
        var starts = 0;
        manager.Changed += () =>
        {
            if (manager.Snapshot().Any(row => row.Job.Id == installer.Id && row.Job.State == DownloadState.Completed))
                installed.TrySetResult();
        };
        manager.Start(new() { AutomaticUpdates = AutomaticUpdateMode.DownloadAndInstall }, async (work, ct) =>
        {
            Assert.Equal("install", work.Job.ItemId);
            Interlocked.Increment(ref starts);
            entered.TrySetResult();
            await begin.Task.WaitAsync(ct);
            Assert.True(manager.IsQueuePaused);
            Assert.True(work.MayInstall);
            Assert.True(work.BeginInstallation());
            return new(DownloadState.Completed);
        });
        try
        {
            if (pauseBeforeStart)
                manager.ToggleQueuePause();
            Assert.True(manager.Change(installer.Id, DownloadAction.Install));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!pauseBeforeStart)
                manager.ToggleQueuePause();
            Assert.True(manager.Enqueue("download", DownloadStore.Gog, DownloadKind.Update));
            Assert.True(manager.Change(automatic.Id, DownloadAction.Resume));

            begin.TrySetResult();
            await installed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.IsQueuePaused);
            Assert.Equal(1, starts);
            Assert.All(manager.Snapshot().Where(row => row.Job.Id != installer.Id),
                row => Assert.Equal(DownloadState.Queued, row.Job.State));
        }
        finally { begin.TrySetResult(); await manager.StopAsync(); manager.Release(); }
    }

    [Fact]
    public async Task PausedDownloads_PreparedInstallationWaitsForInterruptedTransferToUnwind()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var installer = PreparedGogJob("install");
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([installer]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var downloading = Signal();
        var interrupted = Signal();
        var unwind = Signal();
        var installed = Signal();
        manager.Start(new(), async (work, ct) =>
        {
            if (work.Job.ItemId == "download")
            {
                downloading.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    interrupted.TrySetResult();
                    await unwind.Task;
                    throw;
                }
            }
            Assert.Equal("install", work.Job.ItemId);
            Assert.True(work.BeginInstallation());
            installed.TrySetResult();
            return new(DownloadState.Completed);
        });
        try
        {
            Assert.True(manager.Enqueue("download", DownloadStore.Gog, DownloadKind.Update));
            await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.ToggleQueuePause();
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.Change(installer.Id, DownloadAction.Install));
            Assert.False(installed.Task.IsCompleted);
            unwind.TrySetResult();
            await installed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(manager.IsQueuePaused);
        }
        finally { unwind.TrySetResult(); await manager.StopAsync(); manager.Release(); }
        Assert.Equal(DownloadState.Queued, manager.Snapshot().Single(row => row.Job.ItemId == "download").Job.State);
    }

    [Fact]
    public async Task PausedDownloads_ChangedOrMissingPayloadReturnsToQueueWithoutStartingAnotherDownload()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var installer = PreparedGogJob("install");
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([installer]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var requeued = Signal();
        var attempts = 0;
        manager.Changed += () =>
        {
            if (manager.Snapshot().Any(row => row.Job.Reason == DownloadReason.GogPayloadChanged))
                requeued.TrySetResult();
        };
        manager.Start(new(), (work, _) =>
        {
            Interlocked.Increment(ref attempts);
            Assert.True(work.BeginInstallation());
            return Task.FromResult(new DownloadOutcome(DownloadState.Queued, DownloadReason.GogPayloadChanged,
                RedownloadRequired: true));
        });
        try
        {
            manager.ToggleQueuePause();
            Assert.True(manager.Change(installer.Id, DownloadAction.Install));
            await requeued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await manager.StopAsync(); manager.Release(); }
        var job = Assert.Single(manager.Snapshot()).Job;
        Assert.True(manager.IsQueuePaused);
        Assert.Equal(1, attempts);
        Assert.Equal(DownloadState.Queued, job.State);
        Assert.Equal(DownloadReason.GogPayloadChanged, job.Reason);
        Assert.False(job.InstallRequested || job.PayloadReady);
        Assert.Null(job.PayloadRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PausedDownloads_PreparedInstallationCanStillBeCancelledOrStoppedBeforeInstallation(bool stop)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var installer = PreparedGogJob("install");
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([installer]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        var entered = Signal();
        var cancelled = Signal();
        manager.Start(new(), async (_, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("The preflight must be cancelled.");
        });
        try
        {
            manager.ToggleQueuePause();
            Assert.True(manager.Change(installer.Id, DownloadAction.Install));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop)
                await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            else
                Assert.True(manager.Change(installer.Id, DownloadAction.Cancel));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await manager.StopAsync(); manager.Release(); }
        Assert.True(manager.IsQueuePaused);
        Assert.False(manager.IsInstalling);
        if (!stop)
            AssertInstallReady(Assert.Single(manager.Snapshot()).Job, installer.Id, DownloadReason.ManualGogInstallation, true);
    }

    [Fact]
    public async Task PausedDownloads_CommandsOnlyAllowPreparedGogInstallers()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("downloads.json");
        var installer = PreparedGogJob("install");
        var epic = Job("epic", DownloadState.InstallationPending) with { Store = DownloadStore.Epic };
        var incomplete = PreparedGogJob("incomplete") with { PayloadRevision = null };
        var repair = PreparedGogJob("repair") with { State = DownloadState.RepairRequired };
        using (var store = new DownloadQueueStore(path))
        {
            store.Load();
            store.Save([installer, epic, incomplete, repair]);
        }
        var manager = new DownloadManagerService(path, new RecordingErrorService(), TimeProvider.System);
        manager.Start(new(), (_, _) => throw new InvalidOperationException("No unapproved operation may run."));
        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
            {
                using var vm = new DownloadManagerViewModel(manager, id => id, null, () => null,
                    _ => false, (_, _) => { }, _ => Task.FromResult(false));
                vm.Selected = vm.Entries.Single(row => row.Job.Id == epic.Id);
                Assert.True(vm.Install.CanExecute(null));
                vm.Selected = vm.Entries.Single(row => row.Job.Id == repair.Id);
                Assert.True(vm.Repair.CanExecute(null));
                vm.ToggleQueuePause.Execute(null);
                dispatcher.RunJobs();

                foreach (var blocked in new[] { epic, incomplete })
                {
                    vm.Selected = vm.Entries.Single(row => row.Job.Id == blocked.Id);
                    Assert.False(vm.Install.CanExecute(null));
                    Assert.False(manager.Change(blocked.Id, DownloadAction.Install));
                }
                vm.Selected = vm.Entries.Single(row => row.Job.Id == repair.Id);
                Assert.False(vm.Repair.CanExecute(null));
                Assert.False(manager.Change(repair.Id, DownloadAction.Repair));
                vm.Selected = vm.Entries.Single(row => row.Job.Id == installer.Id);
                Assert.True(vm.Install.CanExecute(null));
                Assert.True(manager.IsQueuePaused);
            });
        }
        finally { await manager.StopAsync(); manager.Release(); }
    }

    private static DownloadJob PreparedGogJob(string id) => Job(id, DownloadState.InstallationPending) with
    {
        Revision = new string('A', 64), PayloadRevision = new string('A', 64), PayloadReady = true,
        Reason = DownloadReason.ManualGogInstallation
    };

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
