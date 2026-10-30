using System.Text.Json;
using Retromind.Models;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Security;

public sealed class SecretStoreSafetyTests
{
    private static readonly SecretKey Key = new("private-service", "private-account");
    private const string Secret = "unlabeled-private-credential";

    [Fact]
    public async Task PersistentWrite_ReportsPersistentAndRemovesStaleFallback()
    {
        var primary = new ControllableSecretStore();
        var fallback = new InMemorySecretStore();
        await fallback.SetAsync(Key, "stale");
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, fallback, errors);

        Assert.Equal(SecretStorageStatus.Persistent, await store.SetWithStatusAsync(Key, Secret));
        Assert.Equal(SecretStorageStatus.Persistent, store.GetStorageStatus(Key));
        Assert.Equal(Secret, await store.GetAsync(Key));
        Assert.Null(await fallback.GetAsync(Key));
        Assert.Empty(errors.Reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryUnavailableOrWriteFails_StoresAuthoritativeSessionValue(bool failWrite)
    {
        var primary = new ControllableSecretStore { Available = failWrite, FailWrite = failWrite };
        await primary.Values.SetAsync(Key, "old-persistent");
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, new InMemorySecretStore(), errors);

        Assert.Equal(SecretStorageStatus.SessionOnly, await store.SetWithStatusAsync(Key, Secret));
        Assert.Equal(SecretStorageStatus.SessionOnly, store.GetStorageStatus(Key));
        primary.Available = true;
        primary.FailWrite = false;

        Assert.Equal(Secret, await store.GetAsync(Key));
        Assert.Equal("old-persistent", await primary.Values.GetAsync(Key));
        Assert.Equal(0, primary.ReadCalls);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("SecretStore.PrimaryWriteFailed", report.Code);
        Assert.Equal(failWrite ? "OperationFailed" : "Unavailable", report.Context!["Reason"]);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);

        // Only an explicit later write publishes the updated value persistently.
        Assert.Equal(SecretStorageStatus.Persistent, await store.SetWithStatusAsync(Key, "explicit-new-value"));
        Assert.Equal("explicit-new-value", await store.GetAsync(Key));
    }

    [Fact]
    public async Task ExplicitSessionOnlyMode_DoesNotProbeOrMutatePrimary()
    {
        var primary = new ControllableSecretStore();
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, new InMemorySecretStore(), errors, sessionOnly: true);

        Assert.True(await store.IsAvailableAsync());
        Assert.Equal(SecretStorageStatus.SessionOnly, await store.SetWithStatusAsync(Key, Secret));
        Assert.Equal(Secret, await store.GetAsync(Key));
        await store.DeleteAsync(Key);
        Assert.Null(await store.GetAsync(Key));
        Assert.Equal(0, primary.AvailabilityCalls + primary.ReadCalls + primary.WriteCalls + primary.DeleteCalls);
        var report = Assert.Single(errors.Reports);
        Assert.Equal("SecretStore.SessionOnly", report.Code);
        Assert.Equal(ErrorSeverity.Diagnostic, report.Severity);
    }

    [Fact]
    public async Task BothWritesFail_ReturnsFailedAndLegacyMethodThrowsSanitizedException()
    {
        var primary = new ControllableSecretStore { FailWrite = true };
        var fallback = new ControllableSecretStore { FailWrite = true };
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var store = new CompositeSecretStore(primary, fallback, errors);

        Assert.Equal(SecretStorageStatus.Failed, await store.SetWithStatusAsync(Key, Secret));
        Assert.Equal(SecretStorageStatus.Failed, store.GetStorageStatus(Key));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAsync(Key, Secret));
        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task SuccessfulPersistentWriteWithFailedFallbackCleanup_DoesNotResurrectStaleValue()
    {
        var primary = new ControllableSecretStore { Available = false };
        var fallback = new ControllableSecretStore();
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, fallback, errors);
        await store.SetAsync(Key, "session-old");
        primary.Available = true;
        fallback.FailDelete = true;

        Assert.Equal(SecretStorageStatus.Persistent, await store.SetWithStatusAsync(Key, "persistent-new"));
        primary.FailRead = true;
        Assert.Null(await store.GetAsync(Key));
        Assert.Contains(errors.Reports, report => report.Code == "SecretStore.FallbackCleanupFailed");
        Assert.Equal("session-old", await fallback.Values.GetAsync(Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPersistentDelete_ClearsSessionAndMasksOldCredentialUntilExplicitWrite(bool failDelete)
    {
        var primary = new ControllableSecretStore { FailWrite = true };
        await primary.Values.SetAsync(Key, "old-persistent");
        var fallback = new InMemorySecretStore();
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, fallback, errors);
        await store.SetAsync(Key, Secret);
        primary.Available = failDelete;
        primary.FailDelete = failDelete;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(Key));

        Assert.Equal(SecretStorageStatus.Failed, store.GetStorageStatus(Key));
        Assert.Null(await fallback.GetAsync(Key));
        primary.Available = true;
        primary.FailDelete = false;
        Assert.Null(await store.GetAsync(Key));
        Assert.Equal("old-persistent", await primary.Values.GetAsync(Key));
        Assert.Single(errors.Reports, report => report.Code == "SecretStore.PrimaryDeleteFailed");
        await store.DeleteAsync(Key);
        Assert.Null(await primary.Values.GetAsync(Key));
        primary.FailWrite = false;
        await store.SetAsync(Key, "new-login");
        Assert.Equal("new-login", await store.GetAsync(Key));
    }

    [Fact]
    public async Task FailedFallbackDelete_DoesNotExposeRemainingFallbackSecret()
    {
        var primary = new ControllableSecretStore { FailWrite = true };
        var fallback = new ControllableSecretStore();
        var store = new CompositeSecretStore(primary, fallback, new RecordingErrorService());
        await store.SetAsync(Key, Secret);
        fallback.FailDelete = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(Key));

        Assert.Equal(Secret, await fallback.Values.GetAsync(Key));
        Assert.Null(await store.GetAsync(Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryReadFailure_IsDiagnosedWithoutClaimingDurability(bool unavailable)
    {
        var primary = new ControllableSecretStore { Available = !unavailable, FailRead = !unavailable };
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, new InMemorySecretStore(), errors);

        Assert.Null(await store.GetAsync(Key));

        Assert.Equal(SecretStorageStatus.Failed, store.GetStorageStatus(Key));
        Assert.Equal("SecretStore.PrimaryReadFailed", Assert.Single(errors.Reports).Code);
    }

    [Fact]
    public async Task Diagnostics_ContainNoSensitiveKeysValuesOrExceptionDetails()
    {
        var primary = new ControllableSecretStore { FailWrite = true, FailRead = true, FailDelete = true };
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var store = new CompositeSecretStore(primary, new InMemorySecretStore(), errors);
        await store.GetAsync(Key);
        await store.SetAsync(Key, Secret);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(Key));

        Assert.Equal(3, errors.Reports.Count);
        foreach (var report in errors.Reports)
        {
            var serialized = JsonSerializer.Serialize(report);
            Assert.DoesNotContain(Secret, serialized);
            Assert.DoesNotContain(Key.Service, serialized);
            Assert.DoesNotContain(Key.Account, serialized);
            Assert.DoesNotContain("secret-tool output", serialized);
            Assert.Null(report.Exception);
            Assert.Equal(nameof(CompositeSecretStore), report.Component);
        }
        Assert.All(errors.Notifications, notification => Assert.Null(notification));
    }

    [Fact]
    public async Task PreCanceledOperations_DoNotMutateExistingSecretOrReportErrors()
    {
        var primary = new ControllableSecretStore();
        var errors = new RecordingErrorService();
        var store = new CompositeSecretStore(primary, new InMemorySecretStore(), errors);
        await store.SetAsync(Key, Secret);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetWithStatusAsync(Key, "new", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync(Key, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(Key, cts.Token));
        Assert.Equal(Secret, await store.GetAsync(Key));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task WaitingOperationCancellation_DoesNotInterruptAnActiveWrite()
    {
        var primary = new ControllableSecretStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.BeforeWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var store = new CompositeSecretStore(primary, new InMemorySecretStore(), new RecordingErrorService());
        var write = store.SetAsync(Key, Secret);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        try
        {
            var delete = store.DeleteAsync(Key, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete);
        }
        finally { release.TrySetResult(); }
        await write;
        Assert.Equal(Secret, await store.GetAsync(Key));
    }

    [Fact]
    public async Task UnknownLegacyStore_DoesNotImplicitlyPromisePersistentStorage()
    {
        ISecretStore memory = new InMemorySecretStore();
        Assert.Equal(SecretStorageStatus.SessionOnly, await memory.SetWithStatusAsync(Key, Secret));
        ISecretStore legacy = new LegacyStore();
        Assert.Equal(SecretStorageStatus.Unknown, await legacy.SetWithStatusAsync(Key, Secret));
    }

    private sealed class LegacyStore : ISecretStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task SetAsync(SecretKey key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.CompletedTask;
    }

    internal sealed class ControllableSecretStore : IPersistentSecretStore
    {
        internal InMemorySecretStore Values { get; } = new();
        internal bool Available { get; set; } = true;
        internal bool FailWrite { get; set; }
        internal bool FailRead { get; set; }
        internal bool FailDelete { get; set; }
        internal int AvailabilityCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        internal int DeleteCalls { get; private set; }
        internal Func<CancellationToken, Task>? BeforeWrite { get; set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AvailabilityCalls++;
            return Task.FromResult(Available);
        }
        public async Task SetAsync(SecretKey key, string value, CancellationToken ct = default)
        {
            WriteCalls++;
            if (BeforeWrite != null) await BeforeWrite(ct);
            if (FailWrite) throw SensitiveFailure();
            await Values.SetAsync(key, value, ct);
        }
        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
        {
            ReadCalls++;
            if (FailRead) throw SensitiveFailure();
            return Values.GetAsync(key, ct);
        }
        public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
        {
            DeleteCalls++;
            if (FailDelete) throw SensitiveFailure();
            return Values.DeleteAsync(key, ct);
        }
        private static Exception SensitiveFailure() => new InvalidOperationException(
            $"secret-tool output {Secret} {Key.Service} {Key.Account}", new IOException(Secret));
    }
}
