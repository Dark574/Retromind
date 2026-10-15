namespace Retromind.Services.Stores.Gog;

/// <summary>
/// Defines how GOG installation roots are stored and resolved. Relative values
/// are always relative to DataRoot; absolute values remain supported for legacy
/// entries and installations intentionally located outside Retromind.
/// </summary>
internal static class GogInstallPathHelper
{
    public static string ToStoredPath(string installPath, bool preferPortablePath)
        => StoreInstallPathHelper.ToStoredPath(installPath, preferPortablePath);

    public static bool TryResolveStoredPath(string? storedPath, out string fullPath)
        => StoreInstallPathHelper.TryResolveStoredPath(storedPath, out fullPath);
}
