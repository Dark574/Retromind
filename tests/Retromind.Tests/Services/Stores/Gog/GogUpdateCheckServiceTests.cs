using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Abstractions;
using Retromind.Services.Stores.Gog;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogUpdateCheckServiceTests
{
    private const string LegacySignature = "1C3E813E38EA062A0015F054EACA9FFE2BAB9E1DC637B11AEF5E8390C1999D92";
    private static readonly GogInstallerPackage Remote = new(
        "42", GogInstallPlatform.Linux, "Game", "1.0", [new("", "z.bin", 20), new("", "A.sh", 10)]);

    [Theory]
    [InlineData(" 1.0 ", null, "UpToDate", "up_to_date", false)]
    [InlineData("0.9", null, "UpdateAvailable", "available", true)]
    [InlineData(null, LegacySignature, "UpToDate", "up_to_date", false)]
    [InlineData("1.0", "different-signature", "UpdateAvailable", "available", true)]
    [InlineData(null, null, "NoBaseline", "unknown", false)]
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
        Assert.Equal(new GogUpdateMetadata(expectedStatus, expectedAvailable, clock.GetUtcNow()), Assert.Single(updates));
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
            service.StopBackgroundChecks();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(token.IsCancellationRequested);
        }
        finally
        {
            service.StopBackgroundChecks();
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
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _utcNow;
        internal void Advance(TimeSpan amount) => _utcNow += amount;
    }

    private sealed class StubAuth : IStoreAuthProvider
    {
        public string ProviderId => "gog";
        public string DisplayName => "GOG";
        public StoreProviderCapabilities Capabilities => StoreProviderCapabilities.Authentication;
        internal bool AuthenticatedOnFirstReadOnly { get; init; }
        internal int StateReads { get; private set; }
        internal int Refreshes { get; private set; }

        public Task<StoreAuthState> GetAuthStateAsync(CancellationToken ct = default)
        {
            StateReads++;
            return Task.FromResult(new StoreAuthState(!AuthenticatedOnFirstReadOnly || StateReads == 1, null));
        }

        public Task<bool> TryRefreshSessionAsync(CancellationToken ct = default)
        {
            Refreshes++;
            return Task.FromResult(true);
        }

        public Task<StoreAccountInfo> SignInInteractiveAsync(
            Func<StoreSignInChallenge, CancellationToken, Task<StoreSignInResponse?>>? challengeResolver = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task SignOutAsync(bool forgetPersistentToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
