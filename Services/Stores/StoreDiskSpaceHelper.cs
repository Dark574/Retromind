using System.IO;
using System.Linq;
using Retromind.Helpers;

namespace Retromind.Services.Stores;

internal static class StoreDiskSpaceHelper
{
    internal const long FreeSpaceReserveBytes = 1024L * 1024L * 1024L;

    internal static string GetNearestExistingPath(string path)
    {
        try
        {
            var candidate = Path.GetFullPath(path);
            while (!Directory.Exists(candidate))
            {
                var parent = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(parent) || FileSystemPathIdentity.Equals(parent, candidate))
                    break;

                candidate = parent;
            }

            return candidate;
        }
        catch
        {
            return path;
        }
    }

    internal static bool TryGetAvailableBytes(string path, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(candidate => candidate.IsReady && IsEqualToOrBelow(fullPath, candidate.RootDirectory.FullName))
                .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
                .FirstOrDefault();
            if (drive == null)
                return false;

            availableBytes = System.Math.Max(0, drive.AvailableFreeSpace);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string FormatByteSize(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
    }

    internal static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsEqualToOrBelow(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        if (FileSystemPathIdentity.Equals(path, fullRoot))
            return true;

        var rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, FileSystemPathIdentity.Comparison);
    }
}
