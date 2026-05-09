using System;

namespace Retromind.Services.Stores.Abstractions;

[Flags]
public enum StoreProviderCapabilities
{
    None = 0,
    Auth = 1,
    Library = 2,
    InstallDiscovery = 4
}
