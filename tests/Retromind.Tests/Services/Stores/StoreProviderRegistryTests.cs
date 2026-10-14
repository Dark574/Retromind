using Retromind.Models.Stores;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Abstractions;

namespace Retromind.Tests.Services.Stores;

public sealed class StoreProviderRegistryTests
{
    [Fact]
    public void TryGetProvider_NormalizesWhitespaceAndCase()
    {
        var provider = new FakeLibraryProvider(StoreProviderIds.Gog);
        var registry = new StoreProviderRegistry([provider]);

        var found = registry.TryGetProvider(" GOG ", out var resolved);

        Assert.True(found);
        Assert.Same(provider, resolved);
    }

    [Fact]
    public void Constructor_RejectsDuplicateProviderIdsIgnoringCase()
    {
        var providers = new IStoreProvider[]
        {
            new FakeLibraryProvider("gog"),
            new FakeLibraryProvider("GOG")
        };

        var exception = Assert.Throws<InvalidOperationException>(() => new StoreProviderRegistry(providers));

        Assert.Contains("registered more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequiredCapability_ReturnsProviderCapability()
    {
        var provider = new FakeLibraryProvider(StoreProviderIds.Epic);
        var registry = new StoreProviderRegistry([provider]);

        var resolved = registry.GetRequiredCapability<IStoreLibraryProvider>(StoreProviderIds.Epic);

        Assert.Same(provider, resolved);
    }

    [Fact]
    public void GetRequiredCapability_RejectsUnsupportedCapability()
    {
        var registry = new StoreProviderRegistry([new IdentityOnlyProvider(StoreProviderIds.Steam)]);

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.GetRequiredCapability<IStoreLibraryProvider>(StoreProviderIds.Steam));

        Assert.Contains(nameof(IStoreLibraryProvider), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetCapability_ReturnsFalseForUnsupportedCapability()
    {
        var registry = new StoreProviderRegistry([new IdentityOnlyProvider(StoreProviderIds.Steam)]);

        var found = registry.TryGetCapability<IStoreLibraryProvider>(StoreProviderIds.Steam, out var capability);

        Assert.False(found);
        Assert.Null(capability);
    }

    private class IdentityOnlyProvider : IStoreProvider
    {
        public IdentityOnlyProvider(string providerId)
        {
            ProviderId = providerId;
        }

        public string ProviderId { get; }
        public string DisplayName => ProviderId;
        public StoreProviderCapabilities Capabilities => StoreProviderCapabilities.None;
    }

    private sealed class FakeLibraryProvider(string providerId) : IdentityOnlyProvider(providerId), IStoreLibraryProvider
    {
        public Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StoreGameRecord>>([]);
    }
}
