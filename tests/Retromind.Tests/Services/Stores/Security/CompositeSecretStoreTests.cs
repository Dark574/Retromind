using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.Stores.Security;

public sealed class CompositeSecretStoreTests
{
    private static readonly SecretKey TestKey = new("retromind:test", "account");

    [Fact]
    public async Task IsAvailableAsync_PrimaryCancellationIsPropagated()
    {
        var primary = new TestSecretStore { CancelAvailability = true };
        var fallback = new TestSecretStore();
        var store = new CompositeSecretStore(primary, fallback);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.IsAvailableAsync());

        Assert.Equal(0, fallback.AvailabilityCalls);
    }

    [Fact]
    public async Task IsAvailableAsync_PrimaryFailureStillUsesFallback()
    {
        var primary = new TestSecretStore { FailAvailability = true };
        var fallback = new TestSecretStore();
        var store = new CompositeSecretStore(primary, fallback);

        var isAvailable = await store.IsAvailableAsync();

        Assert.True(isAvailable);
        Assert.Equal(1, fallback.AvailabilityCalls);
    }

    [Fact]
    public async Task SecretOperations_PrimaryCancellationIsPropagatedWithoutFallback()
    {
        var primary = new TestSecretStore { CancelOperations = true };
        var fallback = new TestSecretStore();
        var store = new CompositeSecretStore(primary, fallback);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SetAsync(TestKey, "secret"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetAsync(TestKey));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.DeleteAsync(TestKey));

        Assert.Equal(0, fallback.OperationCalls);
    }

    [Fact]
    public async Task SecretOperations_PrimaryFailureStillUsesFallback()
    {
        var primary = new TestSecretStore { FailOperations = true };
        var fallback = new TestSecretStore();
        var store = new CompositeSecretStore(primary, fallback);

        await store.SetAsync(TestKey, "secret");
        var value = await store.GetAsync(TestKey);
        await store.DeleteAsync(TestKey);

        Assert.Equal("fallback-secret", value);
        Assert.Equal(3, fallback.OperationCalls);
    }

    [Fact]
    public async Task InMemoryStore_PreCanceledOperationsDoNotMutateState()
    {
        var store = new InMemorySecretStore();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.IsAvailableAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SetAsync(TestKey, "secret", cancellation.Token));
        Assert.Null(await store.GetAsync(TestKey));

        await store.SetAsync(TestKey, "secret");
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.GetAsync(TestKey, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.DeleteAsync(TestKey, cancellation.Token));
        Assert.Equal("secret", await store.GetAsync(TestKey));
    }

    private sealed class TestSecretStore : ISecretStore
    {
        public bool CancelAvailability { get; init; }
        public bool FailAvailability { get; init; }
        public bool CancelOperations { get; init; }
        public bool FailOperations { get; init; }
        public int AvailabilityCalls { get; private set; }
        public int OperationCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            AvailabilityCalls++;
            if (CancelAvailability)
                throw new OperationCanceledException(ct);

            if (FailAvailability)
                throw new InvalidOperationException("Primary store availability check failed.");

            return Task.FromResult(true);
        }

        public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
        {
            OperationCalls++;
            ThrowIfConfigured(ct);
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
        {
            OperationCalls++;
            ThrowIfConfigured(ct);
            return Task.FromResult<string?>("fallback-secret");
        }

        public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
        {
            OperationCalls++;
            ThrowIfConfigured(ct);
            return Task.CompletedTask;
        }

        private void ThrowIfConfigured(CancellationToken ct)
        {
            if (CancelOperations)
                throw new OperationCanceledException(ct);

            if (FailOperations)
                throw new InvalidOperationException("Primary store failed.");
        }
    }
}
