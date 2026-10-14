using System;
using System.Collections.Generic;
using System.Linq;
using Retromind.Services.Stores.Abstractions;

namespace Retromind.Services.Stores;

public sealed class StoreProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IStoreProvider> _providers;

    public StoreProviderRegistry(IEnumerable<IStoreProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var providerMap = new Dictionary<string, IStoreProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            var providerId = provider.ProviderId?.Trim();
            if (string.IsNullOrWhiteSpace(providerId))
                throw new InvalidOperationException("Store providers must define a non-empty provider ID.");

            if (!providerMap.TryAdd(providerId, provider))
                throw new InvalidOperationException($"Store provider ID '{providerId}' is registered more than once.");
        }

        _providers = providerMap;
    }

    public IReadOnlyCollection<IStoreProvider> Providers => _providers.Values.ToArray();

    public bool TryGetProvider(string? providerId, out IStoreProvider provider)
    {
        var normalizedId = providerId?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedId) &&
            _providers.TryGetValue(normalizedId, out var resolvedProvider))
        {
            provider = resolvedProvider;
            return true;
        }

        provider = null!;
        return false;
    }

    public IStoreProvider GetRequiredProvider(string providerId)
    {
        if (TryGetProvider(providerId, out var provider))
            return provider;

        throw new KeyNotFoundException($"Store provider '{providerId}' is not registered.");
    }

    public bool TryGetCapability<TCapability>(string? providerId, out TCapability capability)
        where TCapability : class, IStoreProvider
    {
        if (TryGetProvider(providerId, out var provider) && provider is TCapability resolvedCapability)
        {
            capability = resolvedCapability;
            return true;
        }

        capability = null!;
        return false;
    }

    public TCapability GetRequiredCapability<TCapability>(string providerId)
        where TCapability : class, IStoreProvider
    {
        if (TryGetCapability<TCapability>(providerId, out var capability))
            return capability;

        var provider = GetRequiredProvider(providerId);
        throw new InvalidOperationException(
            $"Store provider '{provider.ProviderId}' does not implement '{typeof(TCapability).Name}'.");
    }
}
