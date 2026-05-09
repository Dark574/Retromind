namespace Retromind.Models.Stores;

public sealed record StoreInstallRecord(
    string ProviderId,
    string StoreGameId,
    string InstallPath,
    string? LaunchExecutable,
    string? LaunchArguments,
    bool IsInstalled);
