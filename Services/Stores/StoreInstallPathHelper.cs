using System.IO;
using Retromind.Helpers;

namespace Retromind.Services.Stores;

/// <summary>
/// Stores install roots portably when possible and resolves them against DataRoot.
/// The contract is shared by store integrations even though their installers differ.
/// </summary>
internal static class StoreInstallPathHelper
{
    internal static string ToStoredPath(string installPath, bool preferPortablePath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return string.Empty;

        var trimmed = installPath.Trim();
        if (!preferPortablePath)
            return trimmed;

        return PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(trimmed)
               ?? trimmed;
    }

    internal static bool TryResolveStoredPath(string? storedPath, out string fullPath)
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
