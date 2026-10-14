using System;
using System.IO;
using System.Linq;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

internal enum GogInstallSpaceStatus
{
    Sufficient = 0,
    InsufficientForDownload = 1,
    EstimatedInstallMayNotFit = 2,
    PackageSizeUnknown = 3,
    FreeSpaceUnavailable = 4
}

internal sealed record GogInstallSpaceAssessment(
    GogInstallSpaceStatus Status,
    string CheckedPath,
    long? AvailableBytes,
    long? InstallerBytes,
    long? RemainingDownloadBytes,
    long? EstimatedPeakAdditionalBytes);

internal sealed class GogInsufficientDiskSpaceException : IOException
{
    internal GogInsufficientDiskSpaceException(string checkedPath, long requiredBytes, long availableBytes)
        : base(
            $"Not enough free space on '{checkedPath}'. " +
            $"The download requires {GogInstallSpaceService.FormatByteSize(requiredBytes)} including the safety reserve, " +
            $"but only {GogInstallSpaceService.FormatByteSize(availableBytes)} is available.")
    {
        CheckedPath = checkedPath;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    internal string CheckedPath { get; }
    internal long RequiredBytes { get; }
    internal long AvailableBytes { get; }
}

internal static class GogInstallSpaceService
{
    // Keep enough space for filesystem metadata, installer logs and small temporary files.
    internal const long FreeSpaceReserveBytes = 1024L * 1024L * 1024L;

    internal static GogInstallSpaceAssessment Assess(
        GogInstallerPackage package,
        string stagingDirectory,
        string checkedPath)
    {
        ArgumentNullException.ThrowIfNull(package);

        var normalizedPath = GetNearestExistingPath(checkedPath);
        if (!TryGetAvailableBytes(normalizedPath, out var availableBytes))
        {
            return new GogInstallSpaceAssessment(
                GogInstallSpaceStatus.FreeSpaceUnavailable,
                normalizedPath,
                null,
                null,
                null,
                null);
        }

        return Assess(package, stagingDirectory, normalizedPath, availableBytes);
    }

    internal static GogInstallSpaceAssessment Assess(
        GogInstallerPackage package,
        string stagingDirectory,
        string checkedPath,
        long availableBytes)
    {
        ArgumentNullException.ThrowIfNull(package);
        availableBytes = Math.Max(0, availableBytes);

        if (package.Files.Count == 0 || package.Files.Any(file => file.Size is null or <= 0))
        {
            return new GogInstallSpaceAssessment(
                GogInstallSpaceStatus.PackageSizeUnknown,
                checkedPath,
                availableBytes,
                null,
                null,
                null);
        }

        long installerBytes;
        try
        {
            installerBytes = checked(package.Files.Sum(file => file.Size!.Value));
        }
        catch (OverflowException)
        {
            return new GogInstallSpaceAssessment(
                GogInstallSpaceStatus.PackageSizeUnknown,
                checkedPath,
                availableBytes,
                null,
                null,
                null);
        }

        var reusableBytes = CalculateReusableStagingBytes(package, stagingDirectory);
        var remainingDownloadBytes = Math.Max(0, installerBytes - reusableBytes);
        var requiredForDownload = SaturatingAdd(remainingDownloadBytes, FreeSpaceReserveBytes);
        var estimatedPeakAdditionalBytes = SaturatingAdd(requiredForDownload, installerBytes);

        var status = remainingDownloadBytes > 0 && availableBytes < requiredForDownload
            ? GogInstallSpaceStatus.InsufficientForDownload
            : availableBytes < estimatedPeakAdditionalBytes
                ? GogInstallSpaceStatus.EstimatedInstallMayNotFit
                : GogInstallSpaceStatus.Sufficient;

        return new GogInstallSpaceAssessment(
            status,
            checkedPath,
            availableBytes,
            installerBytes,
            remainingDownloadBytes,
            estimatedPeakAdditionalBytes);
    }

    internal static long CalculateReusableStagingBytes(
        GogInstallerPackage package,
        string stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
            return 0;

        var reusableBytes = 0L;
        foreach (var file in package.Files)
        {
            if (file.Size is not > 0)
                continue;

            var targetPath = Path.Combine(
                stagingDirectory,
                GogInstallerStagingPathHelper.SanitizeFileName(file.FileName));
            var existingBytes = Math.Max(
                TryGetFileLength(targetPath),
                TryGetFileLength(targetPath + ".part"));
            reusableBytes = SaturatingAdd(reusableBytes, Math.Min(existingBytes, file.Size.Value));
        }

        return reusableBytes;
    }

    internal static void EnsureDownloadCanFit(string targetPath, long? remainingResponseBytes)
    {
        if (remainingResponseBytes is not > 0)
            return;

        var checkedPath = GetNearestExistingPath(Path.GetDirectoryName(targetPath) ?? targetPath);
        if (!TryGetAvailableBytes(checkedPath, out var availableBytes))
            return;

        var requiredBytes = SaturatingAdd(remainingResponseBytes.Value, FreeSpaceReserveBytes);
        if (availableBytes < requiredBytes)
            throw new GogInsufficientDiskSpaceException(checkedPath, requiredBytes, availableBytes);
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

            availableBytes = Math.Max(0, drive.AvailableFreeSpace);
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

    private static string GetNearestExistingPath(string path)
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

    private static long TryGetFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? Math.Max(0, new FileInfo(path).Length) : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal static class GogInstallerStagingPathHelper
{
    internal static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "installer.bin";

        var sanitized = fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(sanitized) ? "installer.bin" : sanitized;
    }
}
