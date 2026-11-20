using System.Threading.Channels;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogUpdateCheckServiceTests
{
    private const string LegacySignature = "1C3E813E38EA062A0015F054EACA9FFE2BAB9E1DC637B11AEF5E8390C1999D92";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly GogInstallerPackage Remote = new(
        "42", GogInstallPlatform.Linux, "Game", "1.0", [new("", "z.bin", 20), new("", "A.sh", 10)]);

    [Theory]
    [InlineData(" 1.0 ", null, "UpToDate", "up_to_date", false)]
    [InlineData("0.9", null, "UpdateAvailable", "available", true)]
    [InlineData(null, LegacySignature, "UpToDate", "up_to_date", false)]
    [InlineData("1.0", "different-signature", "UpdateAvailable", "available", true)]
    [InlineData(null, null, "NoBaseline", "no_baseline", false)]
    public async Task CheckAsync_ComparesVersionAndPersistedInstallerSignature(
        string? version, string? signature, string expectedResult, string expectedStatus, bool expectedAvailable)
    {
        var clock = new ManualClock();
        var service = CreateService(clock: clock);
        var snapshot = Snapshot() with { InstalledVersion = version, InstalledSignature = signature };
        var updates = new List<GogUpdateMetadata>();
        var dlcAvailability = new List<bool>();

        var result = await service.CheckAsync(snapshot, false, metadata =>
        {
            updates.Add(metadata);
            return Task.CompletedTask;
        }, available =>
        {
            dlcAvailability.Add(available);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(expectedResult, result.ToString());
        Assert.Equal(new GogUpdateMetadata(expectedStatus, expectedAvailable, clock.GetUtcNow(),
            AvailableRevision: expectedAvailable ? GogInstallerArchive.GetRevision(Remote) : null), Assert.Single(updates));
        Assert.False(Assert.Single(dlcAvailability));
    }

    [Fact]
    public async Task CheckAsync_SkipsRecentAutomaticChecksAndReusesAuthUntilItExpires()
    {
        var clock = new ManualClock();
        var auth = new StubAuth();
        var requests = 0;
        var service = CreateService(auth, (_, _, _) =>
        {
            requests++;
            return Task.FromResult<GogInstallerPackage?>(Remote);
        }, clock: clock);
        var recent = Snapshot() with { LastCheckedUtc = clock.GetUtcNow().AddHours(-1) };

        Assert.Equal(GogUpdateResult.NotDue, await CheckAsync(service, recent));
        Assert.Equal(0, auth.StateReads);
        Assert.Equal(0, requests);

        Assert.Equal(GogUpdateResult.UpToDate, await CheckAsync(service, recent, force: true));
        Assert.Equal(GogUpdateResult.UpToDate, await CheckAsync(service, recent, force: true));
        Assert.Equal(1, auth.StateReads);

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(GogUpdateResult.UpToDate, await CheckAsync(service, recent, force: true));
        Assert.Equal(2, auth.StateReads);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task CheckAsync_BackfillsMissingTargetRevisionBeforeTheDailyCheckAndRespectsRetryDeadline()
    {
        var clock = new ManualClock();
        var service = CreateService(clock: clock);
        var snapshot = Snapshot() with
        {
            InstalledVersion = "0.9",
            LastCheckedUtc = clock.GetUtcNow().AddHours(-1),
            LastStatus = "available",
            NeedsAvailableRevision = true
        };
        var pendingRetry = snapshot with
        {
            RetryAfterUtc = clock.GetUtcNow().AddMinutes(15),
            LastStatus = "error"
        };

        Assert.Equal(GogUpdateResult.NotDue, await CheckAsync(service, pendingRetry));

        var updates = new List<GogUpdateMetadata>();
        var result = await service.CheckAsync(snapshot, false, metadata =>
        {
            updates.Add(metadata);
            return Task.CompletedTask;
        }, IgnoreDlc, CancellationToken.None);

        Assert.Equal(GogUpdateResult.UpdateAvailable, result);
        Assert.Equal(GogInstallerArchive.GetRevision(Remote), Assert.Single(updates).AvailableRevision);
        Assert.Equal(GogUpdateResult.NotDue, await CheckAsync(service, snapshot with { NeedsAvailableRevision = false }));
    }

    [Fact]
    public async Task CheckAsync_RetriesUnauthorizedInstallerRequestAfterRefreshingAuth()
    {
        var auth = new StubAuth { AuthenticatedOnFirstReadOnly = true };
        var requests = 0;
        var service = CreateService(auth, (_, _, _) => ++requests == 1
            ? Task.FromException<GogInstallerPackage?>(new InvalidOperationException("GOG product request failed (401)."))
            : Task.FromResult<GogInstallerPackage?>(Remote));

        Assert.Equal(GogUpdateResult.UpToDate, await CheckAsync(service, Snapshot()));
        Assert.Equal(2, requests);
        Assert.Equal(2, auth.StateReads);
        Assert.Equal(1, auth.Refreshes);
    }

    [Theory]
    [InlineData("error", "error", 15, "Failed")]
    [InlineData("timeout", "error", 15, "Failed")]
    [InlineData("no_installer", "unknown", 60, "Failed")]
    [InlineData("no_session", "auth_missing", 30, "NoAuth")]
    [InlineData("expired_session", "auth_missing", 30, "NoAuth")]
    public async Task FailedCheck_PublishesRetryWithoutReplacingLastCompletedCheck(
        string failure, string expectedStatus, int retryMinutes, string expectedResult)
    {
        var clock = new ManualClock();
        var auth = new StubAuth
        {
            HasSession = failure != "no_session",
            AuthenticatedOnFirstReadOnly = failure == "expired_session",
            RefreshSucceeds = failure is not ("no_session" or "expired_session")
        };
        var recovered = false;
        var service = CreateService(auth, (_, _, _) =>
        {
            if (recovered)
                return Task.FromResult<GogInstallerPackage?>(Remote);

            return failure switch
            {
                "timeout" => Task.FromException<GogInstallerPackage?>(new TaskCanceledException("HTTP timeout")),
                "no_installer" => Task.FromResult<GogInstallerPackage?>(null),
                "expired_session" => Task.FromException<GogInstallerPackage?>(new InvalidOperationException("401")),
                _ => Task.FromException<GogInstallerPackage?>(new IOException("Unavailable"))
            };
        }, clock: clock);
        var snapshot = Snapshot() with { LastCheckedUtc = clock.GetUtcNow().AddMinutes(-1) };
        var updates = new List<GogUpdateMetadata>();
        Task Record(GogUpdateMetadata metadata)
        {
            updates.Add(metadata);
            return Task.CompletedTask;
        }

        var attempt = await service.CheckAsync(snapshot, true, Record, IgnoreDlc, CancellationToken.None);

        Assert.Equal(expectedResult, attempt.ToString());
        var retry = Assert.Single(updates);
        Assert.Equal(new GogUpdateMetadata(expectedStatus, null, null, clock.GetUtcNow().AddMinutes(retryMinutes)), retry);
        snapshot = snapshot with { RetryAfterUtc = retry.RetryAfterUtc, LastStatus = retry.Status };
        clock.Advance(TimeSpan.FromMinutes(retryMinutes) - TimeSpan.FromSeconds(1));
        Assert.Equal(GogUpdateResult.NotDue, await CheckAsync(service, snapshot));

        recovered = true;
        auth.RefreshSucceeds = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GogUpdateResult.UpToDate,
            await service.CheckAsync(snapshot, false, Record, IgnoreDlc, CancellationToken.None));
        Assert.Equal(new GogUpdateMetadata("up_to_date", false, clock.GetUtcNow()), updates.Last());
        Assert.Equal(2, updates.Count);
    }

    [Theory]
    [InlineData("error", 15)]
    [InlineData("auth_missing", 30)]
    [InlineData("unknown", 60)]
    [InlineData("up_to_date", 24 * 60)]
    [InlineData("no_baseline", 24 * 60)]
    public void AutomaticChecks_InterpretLegacyStatusAndKeepCompletedChecksDaily(string status, int minutes)
    {
        var clock = new ManualClock();
        var service = CreateService(clock: clock);
        var snapshot = Snapshot() with { LastCheckedUtc = clock.GetUtcNow(), LastStatus = status };

        clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
        Assert.False(service.IsAutomaticCheckDue(snapshot));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(service.IsAutomaticCheckDue(snapshot));
    }

    [Fact]
    public async Task CheckAsync_RejectsConcurrentChecksUntilPublicationFinishes()
    {
        var service = CreateService();
        var snapshot = Snapshot();
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = service.CheckAsync(snapshot, true, _ =>
        {
            publishing.SetResult();
            return release.Task;
        }, IgnoreDlc, CancellationToken.None);

        try
        {
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GogUpdateResult.InFlight, await CheckAsync(service, snapshot, force: true));
        }
        finally
        {
            release.SetResult();
        }

        Assert.Equal(GogUpdateResult.UpToDate, await first);
        Assert.Equal(GogUpdateResult.UpToDate, await CheckAsync(service, snapshot, force: true));
    }

    [Fact]
    public async Task CheckAsync_LeavesDlcBadgeUntouchedWhenCatalogIsUnavailable()
    {
        var service = CreateService(getDlc: (_, _) =>
            Task.FromException<IReadOnlyList<GogDlcCatalogItem>>(new IOException("Catalog unavailable")));
        var snapshot = Snapshot() with { InstalledDlcs = [new GogDlcInstallationState { ProductId = "77" }] };
        var updates = new List<GogUpdateMetadata>();
        var dlcAvailability = new List<bool>();

        var result = await service.CheckAsync(snapshot, false, metadata =>
        {
            updates.Add(metadata);
            return Task.CompletedTask;
        }, available =>
        {
            dlcAvailability.Add(available);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(GogUpdateResult.UpToDate, result);
        Assert.Equal("up_to_date", Assert.Single(updates).Status);
        Assert.Empty(dlcAvailability);
    }

    [Fact]
    public async Task StopBackgroundChecks_CancelsTheActiveCandidate()
    {
        var service = CreateService();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new[] { new MediaItem("First"), new MediaItem("Second") };
        service.StartBackgroundChecks(() => Task.FromResult<IReadOnlyList<MediaItem>>(candidates), async (item, ct) =>
        {
            entered.SetResult(ct);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return GogUpdateResult.UpToDate;
            }
            finally
            {
                finished.SetResult();
            }
        });

        try
        {
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(token.IsCancellationRequested);
            var stopped = service.StopBackgroundChecksAsync();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopped.WaitAsync(TestTimeout);
            Assert.True(token.IsCancellationRequested);
        }
        finally
        {
            await service.StopBackgroundChecksAsync().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task BackgroundChecks_WakeForPersistedRetryBeforeNormalDailyWindow()
    {
        var clock = new ManualClock();
        var service = CreateService(clock: clock);
        var now = clock.GetUtcNow();
        var snapshot = Snapshot() with
        {
            LastCheckedUtc = now.AddHours(-1),
            RetryAfterUtc = now.AddMinutes(15),
            LastStatus = "error"
        };
        var published = new TaskCompletionSource<GogUpdateMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StartBackgroundChecks(() => Task.FromResult<IReadOnlyList<MediaItem>>([new MediaItem("Game")]),
            (_, ct) => service.CheckAsync(snapshot, false, metadata =>
            {
                published.SetResult(metadata);
                return Task.CompletedTask;
            }, IgnoreDlc, ct));

        try
        {
            for (var sweep = 0; sweep < 3; sweep++)
            {
                var timer = await clock.NextTimerAsync();
                Assert.Equal(TimeSpan.FromMinutes(5), timer.Delay);
                Assert.False(published.Task.IsCompleted);
                clock.Fire(timer);
            }

            var success = await published.Task.WaitAsync(TestTimeout);
            Assert.Equal(now.AddMinutes(15), success.CheckedUtc);
            Assert.Null(success.RetryAfterUtc);
        }
        finally
        {
            await service.StopBackgroundChecksAsync().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task RequestImmediateSweep_WakesAnExistingLoopWithoutWaitingForItsTimer()
    {
        var clock = new ManualClock();
        var service = CreateService(clock: clock);
        var sweepCount = 0;
        var secondSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<MediaItem>> Candidates()
        {
            if (Interlocked.Increment(ref sweepCount) == 2)
                secondSweep.TrySetResult();
            return Task.FromResult<IReadOnlyList<MediaItem>>([]);
        }
        Task<GogUpdateResult> Check(MediaItem item, CancellationToken ct) =>
            Task.FromResult(GogUpdateResult.NotApplicable);

        service.StartBackgroundChecks(Candidates, Check);
        try
        {
            var timer = await clock.NextTimerAsync();
            Assert.Equal(TimeSpan.FromMinutes(5), timer.Delay);

            service.StartBackgroundChecks(Candidates, Check, requestImmediateSweep: true);

            await secondSweep.Task.WaitAsync(TestTimeout);
            Assert.Equal(2, sweepCount);
        }
        finally
        {
            await service.StopBackgroundChecksAsync().WaitAsync(TestTimeout);
        }
    }

    [Fact]
    public async Task RestartBackgroundChecks_WaitsForPreviousSweepAndChecksImmediatelyAfterward()
    {
        var service = CreateService();
        var oldCheckStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newCheckStartedAfterStop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidates = new[] { new MediaItem("Game") };
        Task<IReadOnlyList<MediaItem>> GetCandidates() => Task.FromResult<IReadOnlyList<MediaItem>>(candidates);
        service.StartBackgroundChecks(GetCandidates, async (_, ct) =>
        {
            oldCheckStarted.SetResult(ct);
            await releaseOldCheck.Task;
            return GogUpdateResult.NotApplicable;
        });

        try
        {
            var token = await oldCheckStarted.Task.WaitAsync(TestTimeout);
            var stopped = service.StopBackgroundChecksAsync();
            service.StartBackgroundChecks(GetCandidates, (_, _) =>
            {
                newCheckStartedAfterStop.SetResult(stopped.IsCompletedSuccessfully);
                return Task.FromResult(GogUpdateResult.NotApplicable);
            });

            Assert.True(token.IsCancellationRequested);
            Assert.False(stopped.IsCompleted);
            Assert.False(newCheckStartedAfterStop.Task.IsCompleted);
            releaseOldCheck.SetResult();

            Assert.True(await newCheckStartedAfterStop.Task.WaitAsync(TestTimeout));
        }
        finally
        {
            releaseOldCheck.TrySetResult();
            await service.StopBackgroundChecksAsync().WaitAsync(TestTimeout);
        }
    }

    private static GogInstalledSnapshot Snapshot() =>
        new("item", "42", GogInstallPlatform.Linux, "1.0", null, [], null);

    private static Task<GogUpdateResult> CheckAsync(
        GogUpdateCheckService service, GogInstalledSnapshot snapshot, bool force = false) =>
        service.CheckAsync(snapshot, force, _ => Task.CompletedTask, IgnoreDlc, CancellationToken.None);

    private static Task IgnoreDlc(bool _) => Task.CompletedTask;

    private static GogUpdateCheckService CreateService(
        StubAuth? auth = null,
        Func<string, GogInstallPlatform, CancellationToken, Task<GogInstallerPackage?>>? getPackage = null,
        Func<string, CancellationToken, Task<IReadOnlyList<GogDlcCatalogItem>>>? getDlc = null,
        TimeProvider? clock = null) =>
        new(auth ?? new StubAuth(),
            getPackage ?? ((_, _, _) => Task.FromResult<GogInstallerPackage?>(Remote)),
            getDlc ?? ((_, _) => Task.FromResult<IReadOnlyList<GogDlcCatalogItem>>([])),
            clock);

    private sealed class ManualClock : TimeProvider
    {
        private long _utcTicks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;
        private readonly Channel<ManualTimer> _timers = Channel.CreateUnbounded<ManualTimer>();

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref _utcTicks, amount.Ticks);
        internal Task<ManualTimer> NextTimerAsync() => _timers.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        internal void Fire(ManualTimer timer)
        {
            Advance(timer.Delay);
            timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        private TimerCallback? _callback = callback;
        internal TimeSpan Delay { get; } = delay;

        internal void Fire() => Interlocked.Exchange(ref _callback, null)?.Invoke(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref _callback, null);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubAuth : IStoreAuthProvider
    {
        public string ProviderId => "gog";
        public string DisplayName => "GOG";
        public StoreProviderCapabilities Capabilities => StoreProviderCapabilities.Authentication;
        internal bool HasSession { get; init; } = true;
        internal bool AuthenticatedOnFirstReadOnly { get; init; }
        internal bool RefreshSucceeds { get; set; } = true;
        internal int StateReads { get; private set; }
        internal int Refreshes { get; private set; }

        public Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
        {
            StateReads++;
            return Task.FromResult(new StoreAuthState(HasSession && (!AuthenticatedOnFirstReadOnly || StateReads == 1), null));
        }

        public Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
        {
            Refreshes++;
            return Task.FromResult(RefreshSucceeds);
        }

        public Task<StoreAccountInfo> SignInInteractiveAsync(
            Func<StoreSignInChallenge, CancellationToken, Task<StoreSignInResponse?>>? challengeResolver = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
