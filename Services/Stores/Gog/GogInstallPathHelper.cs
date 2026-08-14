using System;
using System.IO;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

/// <summary>
/// Defines how GOG installation roots are stored and resolved. Relative values
/// are always relative to DataRoot; absolute values remain supported for legacy
/// entries and installations intentionally located outside Retromind.
/// </summary>
internal static class GogInstallPathHelper
{
    public const string CustomFieldName = "Store.InstallPath";

    public static string ToStoredPath(string installPath, bool preferPortablePath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return string.Empty;

        var trimmed = installPath.Trim();
        if (!preferPortablePath)
            return trimmed;

        return PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(trimmed)
               ?? trimmed;
    }

    public static bool TryResolveStoredPath(string? storedPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(storedPath))
            return false;

        var trimmed = storedPath.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                fullPath = Path.GetFullPath(trimmed);
                return true;
            }

            return AppPaths.TryResolveDataPathInsideRoot(trimmed, out fullPath);
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
    }
}
