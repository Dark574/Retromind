namespace Retromind.Services.Stores.Abstractions;

public interface IStoreProvider
{
    string ProviderId { get; }

    string DisplayName { get; }

    StoreProviderCapabilities Capabilities { get; }
}
