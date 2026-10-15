using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.Services.Stores.Epic;

internal enum EpicInstallDirectoryStatus
{
    NewDirectory,
    EmptyDirectory,
    OwnedDirectory,
    InvalidPath,
    DangerousPath,
    SymbolicLink,
    UnreadableDirectory,
    UnownedDirectory
}

internal readonly record struct EpicInstallDirectoryAssessment(
    EpicInstallDirectoryStatus Status,
    string FullPath)
{
    internal bool IsAllowed => Status is
        EpicInstallDirectoryStatus.NewDirectory or
        EpicInstallDirectoryStatus.EmptyDirectory or
        EpicInstallDirectoryStatus.OwnedDirectory;
}

internal enum EpicInstallSpaceStatus
{
    Sufficient,
    EstimatedInstallMayNotFit,
    EstimateUnavailable,
    FreeSpaceUnavailable
}

internal sealed record EpicInstallSpaceAssessment(
    EpicInstallSpaceStatus Status,
    string CheckedPath,
    long? AvailableBytes,
    long? EstimatedInstallBytes,
    long? RequiredBytes);

public sealed class EpicInstallService
{
    private const string MarkerFileName = ".retromind-install.json";
    private readonly LegendaryCliClient _client;

    internal EpicInstallService(LegendaryCliClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    internal Task<LegendaryInstallEstimate> GetInstallEstimateAsync(
        string appName,
        CancellationToken ct = default) =>
        _client.GetInstallEstimateAsync(appName, ct: ct);

    internal EpicInstallSpaceAssessment AssessSpace(
        string installPath,
        LegendaryInstallEstimate? estimate)
    {
        var checkedPath = StoreDiskSpaceHelper.GetNearestExistingPath(installPath);
        if (!StoreDiskSpaceHelper.TryGetAvailableBytes(checkedPath, out var availableBytes))
        {
            return new EpicInstallSpaceAssessment(
                EpicInstallSpaceStatus.FreeSpaceUnavailable,
                checkedPath,
                null,
                estimate?.InstalledBytes,
                null);
        }

        if (estimate is not { InstalledBytes: > 0 })
        {
            return new EpicInstallSpaceAssessment(
                EpicInstallSpaceStatus.EstimateUnavailable,
                checkedPath,
                availableBytes,
                estimate?.InstalledBytes,
                null);
        }

        var requiredBytes = StoreDiskSpaceHelper.SaturatingAdd(
            estimate.InstalledBytes,
            StoreDiskSpaceHelper.FreeSpaceReserveBytes);
        return new EpicInstallSpaceAssessment(
            availableBytes < requiredBytes
                ? EpicInstallSpaceStatus.EstimatedInstallMayNotFit
                : EpicInstallSpaceStatus.Sufficient,
            checkedPath,
            availableBytes,
            estimate.InstalledBytes,
            requiredBytes);
    }

    internal EpicInstallDirectoryAssessment AssessDirectory(string? installPath, MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.InvalidPath, string.Empty);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(installPath);
        }
        catch
        {
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.InvalidPath, installPath);
        }

        if (IsDangerousPath(fullPath))
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.DangerousPath, fullPath);

        try
        {
            if (ContainsSymbolicLinkInPath(fullPath))
                return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.SymbolicLink, fullPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Could not inspect install path '{fullPath}': {ex.Message}");
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.UnreadableDirectory, fullPath);
        }

        if (File.Exists(fullPath))
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.InvalidPath, fullPath);
        if (!Directory.Exists(fullPath))
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.NewDirectory, fullPath);

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
            if (!entries.MoveNext())
                return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.EmptyDirectory, fullPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Could not inspect install directory '{fullPath}': {ex.Message}");
            return new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.UnreadableDirectory, fullPath);
        }

        return HasValidMarker(fullPath, item)
            ? new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.OwnedDirectory, fullPath)
            : new EpicInstallDirectoryAssessment(EpicInstallDirectoryStatus.UnownedDirectory, fullPath);
    }

    internal async Task<LegendaryInstalledGame> InstallAsync(
        string appName,
        string installPath,
        Action<string>? outputReceived,
        CancellationToken ct = default)
    {
        var installed = await _client
            .InstallAsync(appName, installPath, outputReceived, ct)
            .ConfigureAwait(false);
        return installed;
    }

    internal void WriteMarker(string installPath, MediaItem item)
    {
        if (!StoreGameIdentityHelper.TryGetIdentity(item, out var providerId, out var gameId) ||
            !string.Equals(providerId, StoreProviderIds.Epic, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The media item has no Epic Games identity.");
        }

        var fullPath = Path.GetFullPath(installPath);
        Directory.CreateDirectory(fullPath);
        var marker = new InstallMarker(StoreProviderIds.Epic, gameId, item.Id);
        File.WriteAllText(
            Path.Combine(fullPath, MarkerFileName),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
    }

    private static bool HasValidMarker(string installPath, MediaItem item)
    {
        if (!StoreGameIdentityHelper.TryGetIdentity(item, out var providerId, out var gameId) ||
            !string.Equals(providerId, StoreProviderIds.Epic, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var markerPath = Path.Combine(installPath, MarkerFileName);
            if (!File.Exists(markerPath))
                return false;

            var marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(markerPath));
            return marker != null &&
                   string.Equals(marker.ProviderId, StoreProviderIds.Epic, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(marker.StoreGameId, gameId, StringComparison.Ordinal) &&
                   string.Equals(marker.MediaItemId, item.Id, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDangerousPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || FileSystemPathIdentity.Equals(fullPath, root))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && FileSystemPathIdentity.Equals(fullPath, Path.GetFullPath(home)))
            return true;
        if (FileSystemPathIdentity.Equals(fullPath, AppPaths.DataRoot) ||
            FileSystemPathIdentity.Equals(fullPath, AppPaths.LibraryRoot))
        {
            return true;
        }

        string[] blockedPaths = ["/usr", "/bin", "/sbin", "/etc", "/var", "/boot", "/dev", "/proc", "/sys"];
        foreach (var blockedPath in blockedPaths)
        {
            if (IsEqualToOrBelow(fullPath, blockedPath))
                return true;
        }

        return false;
    }

    private static bool ContainsSymbolicLinkInPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var currentPath = root;
        foreach (var component in Path.GetRelativePath(root, fullPath).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, component);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
                return false;
            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                return true;
        }

        return false;
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

    private sealed record InstallMarker(string ProviderId, string StoreGameId, string MediaItemId);
}
