using System;
using System.IO;
using Retromind.Helpers;

namespace Retromind.Services.Stores;

internal static class StoreInstallPathSafety
{
    private static readonly string[] BlockedPaths =
        ["/usr", "/bin", "/sbin", "/etc", "/var", "/boot", "/dev", "/proc", "/sys"];

    internal static bool IsDangerousInstallPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return true;

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || PathsEqual(fullPath, root))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && PathsEqual(fullPath, Path.GetFullPath(home)))
            return true;

        if (PathsEqual(fullPath, AppPaths.DataRoot) || PathsEqual(fullPath, AppPaths.LibraryRoot))
            return true;

        foreach (var blockedPath in BlockedPaths)
        {
            if (IsPathEqualToOrBelow(fullPath, blockedPath))
                return true;
        }

        return false;
    }

    private static bool IsPathEqualToOrBelow(string path, string potentialParent)
    {
        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(potentialParent);
        if (FileSystemPathIdentity.Equals(fullPath, fullParent))
            return true;

        var parentWithSeparator = Path.EndsInDirectorySeparator(fullParent)
            ? fullParent
            : fullParent + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(parentWithSeparator, FileSystemPathIdentity.Comparison);
    }

    private static bool PathsEqual(string left, string right) =>
        FileSystemPathIdentity.Equals(Path.GetFullPath(left), Path.GetFullPath(right));
}
