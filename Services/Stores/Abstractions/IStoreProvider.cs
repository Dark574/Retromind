namespace Retromind.Services.Stores.Abstractions;

[System.Flags]
public enum StoreProviderCapabilities
{
    None = 0,
    Authentication = 1 << 0,
    OwnedLibrary = 1 << 1,
    Install = 1 << 2,
    Update = 1 << 3,
    Repair = 1 << 4,
    Reinstall = 1 << 5,
    Uninstall = 1 << 6,
    Dlc = 1 << 7,
    Launch = 1 << 8
}

public interface IStoreProvider
{
    string ProviderId { get; }

    string DisplayName { get; }

    StoreProviderCapabilities Capabilities { get; }
}
