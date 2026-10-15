using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

internal sealed record EpicUninstallResult(
    bool PrefixPreserved,
    string? PreservedInstallDirectory);

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

    internal Task<LegendaryUpdateStatus> GetUpdateStatusAsync(
        string appName,
        CancellationToken ct = default) =>
        _client.GetUpdateStatusAsync(appName, ct);

    internal Task<IReadOnlyList<LegendaryDlc>> GetDlcCatalogAsync(
        MediaItem item,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException("The media item has no Epic Games identity.");

        return _client.GetDlcCatalogAsync(appName, ct);
    }

    internal async Task<LegendaryInstalledGame> InstallDlcAsync(
        MediaItem item,
        string dlcAppName,
        Action<string>? outputReceived = null,
        CancellationToken ct = default)
    {
        var (baseAppName, installPath) = ResolveOwnedInstallation(item);
        var validatedDlcAppName = await ValidateDlcAppNameAsync(baseAppName, dlcAppName, ct)
            .ConfigureAwait(false);

        return await _client
            .InstallAsync(validatedDlcAppName, installPath, outputReceived, ct)
            .ConfigureAwait(false);
    }

    internal async Task UninstallDlcAsync(
        MediaItem item,
        string dlcAppName,
        Action<string>? outputReceived = null,
        CancellationToken ct = default)
    {
        var (baseAppName, installPath) = ResolveOwnedInstallation(item);
        var validatedDlcAppName = await ValidateDlcAppNameAsync(baseAppName, dlcAppName, ct)
            .ConfigureAwait(false);

        await _client
            .UninstallAsync(validatedDlcAppName, keepFiles: false, outputReceived, ct)
            .ConfigureAwait(false);

        // A DLC shares the main game's directory. Legendary may remove only files
        // belonging to the DLC; Retromind must never apply its recursive main-game
        // cleanup here. Recheck the ownership marker instead.
        if (Directory.Exists(installPath) &&
            AssessDirectory(installPath, item).Status != EpicInstallDirectoryStatus.OwnedDirectory)
        {
            throw new InvalidOperationException(
                "The Epic Games install directory lost its Retromind ownership marker while removing the DLC.");
        }
    }

    internal async Task<EpicUninstallResult> UninstallAsync(
        MediaItem item,
        bool deletePrefix = true,
        Action<string>? outputReceived = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException("The media item has no Epic Games identity.");
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) ||
            !StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath))
        {
            throw new InvalidOperationException("The Epic Games install path is missing or invalid.");
        }

        if (Directory.Exists(installPath))
        {
            var assessment = AssessDirectory(installPath, item);
            if (assessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
            {
                throw new InvalidOperationException(
                    $"Retromind cannot safely remove the Epic Games install directory '{assessment.FullPath}'.");
            }
        }

        var hadPrefix = !string.IsNullOrWhiteSpace(item.PrefixPath);
        var prefixPath = ResolveSafePrefixForDeletion(item.PrefixPath, deletePrefix);
        var prefixPreserved = hadPrefix && string.IsNullOrWhiteSpace(prefixPath);

        var installedDlcs = (await _client.GetDlcCatalogAsync(appName, ct).ConfigureAwait(false))
            .Where(static dlc => dlc.IsInstalled)
            .ToArray();
        foreach (var dlc in installedDlcs)
        {
            outputReceived?.Invoke($"[Retromind] Removing installed DLC '{dlc.Title}'...");
            await _client
                .UninstallAsync(dlc.AppName, keepFiles: false, outputReceived, ct)
                .ConfigureAwait(false);
        }

        await _client
            .UninstallAsync(appName, keepFiles: false, outputReceived, ct)
            .ConfigureAwait(false);

        string? preservedInstallDirectory = null;
        if (Directory.Exists(installPath))
        {
            // Legendary deletes the files from its manifest but intentionally leaves
            // unknown files behind. Remove Retromind's marker, but preserve every
            // remaining file because its ownership cannot be proven safely.
            var remainingAssessment = AssessDirectory(installPath, item);
            if (remainingAssessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
            {
                throw new InvalidOperationException(
                    $"Legendary was unregistered, but Retromind could not safely remove the remaining install directory '{remainingAssessment.FullPath}'.");
            }

            File.Delete(Path.Combine(remainingAssessment.FullPath, MarkerFileName));
            try
            {
                Directory.Delete(remainingAssessment.FullPath, recursive: false);
            }
            catch (IOException) when (Directory.Exists(remainingAssessment.FullPath))
            {
                preservedInstallDirectory = remainingAssessment.FullPath;
            }
            catch (UnauthorizedAccessException) when (Directory.Exists(remainingAssessment.FullPath))
            {
                preservedInstallDirectory = remainingAssessment.FullPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(prefixPath) && Directory.Exists(prefixPath))
        {
            await Task.Run(
                    () => Directory.Delete(prefixPath, recursive: true),
                    ct)
                .ConfigureAwait(false);
        }

        ClearInstallState(item, prefixPreserved);
        return new EpicUninstallResult(prefixPreserved, preservedInstallDirectory);
    }

    private static void ClearInstallState(MediaItem item, bool prefixPreserved)
    {
        var installFields = new HashSet<string>(StringComparer.Ordinal)
        {
            CustomFieldKeyHelper.StoreInstallPath,
            CustomFieldKeyHelper.StoreInstallPlatform,
            CustomFieldKeyHelper.StoreInstallRunnerVersionId,
            CustomFieldKeyHelper.StoreInstallWindowsInstallerPreference,
            CustomFieldKeyHelper.StoreInstalledVersion,
            CustomFieldKeyHelper.StoreInstalledInstallerSignature,
            CustomFieldKeyHelper.StoreUpdateAvailable,
            CustomFieldKeyHelper.StoreDlcUpdateAvailable,
            CustomFieldKeyHelper.StoreUpdateLastCheckedUtc,
            CustomFieldKeyHelper.StoreUpdateLastStatus
        };

        item.CustomFields = item.CustomFields
            .Where(entry => !installFields.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        item.Files = [];
        item.MediaType = MediaType.Command;
        item.EmulatorId = null;
        item.LauncherPath = null;
        item.LauncherArgs = null;
        item.WorkingDirectory = null;
        item.RunnerVersionId = null;
        if (!prefixPreserved)
            item.PrefixPath = null;
    }

    private (string BaseAppName, string InstallPath) ResolveOwnedInstallation(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException("The media item has no Epic Games identity.");
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) ||
            !StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath))
        {
            throw new InvalidOperationException("The Epic Games install path is missing or invalid.");
        }

        var assessment = AssessDirectory(installPath, item);
        if (assessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
        {
            throw new InvalidOperationException(
                $"Retromind cannot safely modify DLCs in the Epic Games install directory '{assessment.FullPath}'.");
        }

        return (appName, assessment.FullPath);
    }

    private async Task<string> ValidateDlcAppNameAsync(
        string baseAppName,
        string dlcAppName,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dlcAppName);
        var normalizedDlcAppName = dlcAppName.Trim();
        if (string.Equals(baseAppName, normalizedDlcAppName, StringComparison.Ordinal))
            throw new InvalidOperationException("The main game cannot be changed through the DLC workflow.");

        var catalog = await _client.GetDlcCatalogAsync(baseAppName, ct).ConfigureAwait(false);
        var matchingDlc = catalog.SingleOrDefault(
            dlc => string.Equals(dlc.AppName, normalizedDlcAppName, StringComparison.Ordinal));
        if (matchingDlc == null)
        {
            throw new InvalidOperationException(
                $"The Epic Games title '{normalizedDlcAppName}' is not a DLC of '{baseAppName}'.");
        }

        return matchingDlc.AppName;
    }

    private static string? ResolveSafePrefixForDeletion(string? storedPrefixPath, bool deletePrefix)
    {
        if (!deletePrefix || string.IsNullOrWhiteSpace(storedPrefixPath))
            return null;

        string resolvedPrefix;
        try
        {
            resolvedPrefix = PrefixPathHelper.ResolveAbsolutePrefixPath(
                storedPrefixPath,
                AppPaths.LibraryRoot);
            var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
            var protectedPaths = new[]
            {
                libraryRoot,
                Path.Combine(libraryRoot, "Prefixes"),
                Path.Combine(libraryRoot, "Games")
            };
            if (protectedPaths.Any(path => FileSystemPathIdentity.Equals(path, resolvedPrefix)) ||
                !IsEqualToOrBelow(resolvedPrefix, libraryRoot) ||
                ContainsSymbolicLinkInPath(resolvedPrefix))
            {
                Debug.WriteLine(
                    $"[Epic] Prefix '{resolvedPrefix}' is outside the managed library, protected, or contains a symbolic link. It will be preserved.");
                return null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Prefix path could not be validated and will be preserved: {ex.Message}");
            return null;
        }

        return resolvedPrefix;
    }

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
        MediaItem item,
        string installPath,
        Action<string>? outputReceived,
        CancellationToken ct = default,
        bool repair = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException("The media item has no Epic Games identity.");

        var assessment = AssessDirectory(installPath, item);
        if (assessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
        {
            throw new InvalidOperationException(
                $"Retromind cannot safely install into the Epic Games directory '{assessment.FullPath}'.");
        }

        var installed = await _client
            .InstallAsync(appName, assessment.FullPath, outputReceived, ct, repair)
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

        var assessment = AssessDirectory(installPath, item);
        if (!assessment.IsAllowed)
        {
            throw new InvalidOperationException(
                $"Retromind cannot safely claim the Epic Games install directory '{assessment.FullPath}'.");
        }

        Directory.CreateDirectory(assessment.FullPath);
        assessment = AssessDirectory(assessment.FullPath, item);
        if (!assessment.IsAllowed)
        {
            throw new InvalidOperationException(
                $"The Epic Games install directory changed while Retromind was preparing it: '{assessment.FullPath}'.");
        }

        var marker = new InstallMarker(StoreProviderIds.Epic, gameId, item.Id);
        File.WriteAllText(
            Path.Combine(assessment.FullPath, MarkerFileName),
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
