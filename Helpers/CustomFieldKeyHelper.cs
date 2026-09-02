using System;

namespace Retromind.Helpers;

public static class CustomFieldKeyHelper
{
    private const string InternalStorePrefix = "Store.";

    public const string StoreProviderId = InternalStorePrefix + "ProviderId";
    public const string StoreGameId = InternalStorePrefix + "GameId";
    public const string StoreInstallPath = InternalStorePrefix + "InstallPath";
    public const string StoreInstallPlatform = InternalStorePrefix + "InstallPlatform";
    public const string StoreInstallRunnerVersionId = InternalStorePrefix + "InstallRunnerVersionId";
    public const string StoreInstallWindowsInstallerPreference = InternalStorePrefix + "InstallWindowsInstallerPreference";
    public const string StoreInstalledVersion = InternalStorePrefix + "InstalledVersion";
    public const string StoreInstalledInstallerSignature = InternalStorePrefix + "InstalledInstallerSignature";
    public const string StoreUpdateAvailable = InternalStorePrefix + "UpdateAvailable";
    public const string StoreUpdateLastCheckedUtc = InternalStorePrefix + "LastUpdateCheckUtc";
    public const string StoreUpdateLastStatus = InternalStorePrefix + "LastUpdateCheckStatus";

    public static bool IsInternal(string? key)
    {
        return !string.IsNullOrWhiteSpace(key) &&
               key.Trim().StartsWith(InternalStorePrefix, StringComparison.OrdinalIgnoreCase);
    }
}
