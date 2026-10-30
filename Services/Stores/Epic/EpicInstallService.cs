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
using Retromind.Services.Stores;
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
    string? PreservedInstallDirectory,
    bool CleanupFailed = false,
    bool CleanupCancelled = false);

public sealed class EpicInstallService
{
    private const string MarkerFileName = ".retromind-install.json";
    private readonly LegendaryCliClient _client;
    private readonly IErrorService _errorService;
    private readonly Action<string, CancellationToken> _deletePrefix;
    private readonly Action<string> _deleteInstallMarker;
    private readonly Action<string> _deleteEmptyInstallDirectory;

    internal EpicInstallService(LegendaryCliClient client, IErrorService errorService,
        Action<string, CancellationToken>? deletePrefix = null,
        Action<string>? deleteInstallMarker = null,
        Action<string>? deleteEmptyInstallDirectory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
        _deletePrefix = deletePrefix ?? StoreDirectoryDeletionHelper.DeleteDirectoryTreeWithoutFollowingLinks;
        _deleteInstallMarker = deleteInstallMarker ?? File.Delete;
        _deleteEmptyInstallDirectory = deleteEmptyInstallDirectory ?? (path => Directory.Delete(path, recursive: false));
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
        ct.ThrowIfCancellationRequested();
        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            throw new InvalidOperationException("The media item has no Epic Games identity.");
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) ||
            !StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath))
        {
            throw new InvalidOperationException("The Epic Games install path is missing or invalid.");
        }

        try
        {
            if (EntryExists(installPath))
            {
                var assessment = AssessDirectory(installPath, item);
                if (assessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
                {
                    throw new InvalidOperationException(
                        $"Retromind cannot safely remove the Epic Games install directory '{assessment.FullPath}'.");
                }
            }
        }
        catch (Exception ex)
        {
            ReportUninstallFailure("Epic.UninstallFailed", ErrorSeverity.Error, "ValidateInstallDirectory",
                appName, installPath, item.PrefixPath, 0, mainGameRemoved: false, ex);
            throw;
        }

        var hadPrefix = !string.IsNullOrWhiteSpace(item.PrefixPath);
        var prefixPath = ResolveSafePrefixForDeletion(item.PrefixPath, deletePrefix);
        // A prefix must not recursively consume an install root whose unknown files must be preserved.
        if (prefixPath != null && IsEqualToOrBelow(installPath, prefixPath))
            prefixPath = null;
        var prefixPreserved = hadPrefix;
        var removedDlcCount = 0;
        var operation = "LoadDlcCatalog";
        try
        {
            var installedDlcs = (await _client.GetDlcCatalogAsync(appName, ct).ConfigureAwait(false))
                .Where(static dlc => dlc.IsInstalled)
                .ToArray();
            foreach (var dlc in installedDlcs)
            {
                operation = "UninstallDlc";
                ct.ThrowIfCancellationRequested();
                outputReceived?.Invoke($"[Retromind] Removing installed DLC '{dlc.Title}'...");
                await _client.UninstallAsync(dlc.AppName, keepFiles: false, outputReceived, ct).ConfigureAwait(false);
                removedDlcCount++;
            }

            operation = "UninstallMainGame";
            ct.ThrowIfCancellationRequested();
            await _client.UninstallAsync(appName, keepFiles: false, outputReceived, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (removedDlcCount > 0)
                ReportUninstallFailure("Epic.UninstallInterrupted", ErrorSeverity.Diagnostic, operation,
                    appName, installPath, item.PrefixPath, removedDlcCount, mainGameRemoved: false);
            throw;
        }
        catch (Exception ex)
        {
            ReportUninstallFailure("Epic.UninstallFailed", ErrorSeverity.Error, operation,
                appName, installPath, item.PrefixPath, removedDlcCount, mainGameRemoved: false, ex);
            throw;
        }

        // Legendary verified that the main registration is gone. Commit this fact BEFORE any cancellation
        // check or fallible filesystem cleanup. Keep recovery paths until their cleanup is confirmed.
        ClearInstallState(item, prefixPreserved: hadPrefix, storedInstallPath);
        string? preservedInstallDirectory = installPath;
        var cleanupFailed = false;
        var cleanupCancelled = false;
        operation = "InspectRemainingInstallDirectory";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (EntryExists(installPath))
            {
                var remainingAssessment = AssessDirectory(installPath, item);
                if (remainingAssessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
                    throw new IOException("The remaining Epic install directory no longer passes ownership/path checks.");

                operation = "RemoveInstallMarker";
                _deleteInstallMarker(Path.Combine(remainingAssessment.FullPath, MarkerFileName));
                operation = "RemoveEmptyInstallDirectory";
                // Nonempty remnants are expected, not an invitation to recursively delete unknown files.
                if (!Directory.EnumerateFileSystemEntries(remainingAssessment.FullPath).Any())
                    _deleteEmptyInstallDirectory(remainingAssessment.FullPath);
            }
            if (!EntryExists(installPath)) preservedInstallDirectory = null;

            operation = "DeletePrefix";
            ct.ThrowIfCancellationRequested();
            if (prefixPath != null)
            {
                if (EntryExists(prefixPath))
                {
                    await Task.Run(() => _deletePrefix(prefixPath, ct), ct).ConfigureAwait(false);
                    if (EntryExists(prefixPath))
                        throw new IOException("The Epic prefix remains after the requested deletion.");
                }
                prefixPreserved = false;
                item.PrefixPath = null;
            }
        }
        catch (OperationCanceledException)
        {
            cleanupCancelled = true;
            ReportUninstallFailure("Epic.UninstallCleanupCancelled", ErrorSeverity.Diagnostic, operation,
                appName, installPath, item.PrefixPath, removedDlcCount, mainGameRemoved: true);
        }
        catch (Exception ex)
        {
            cleanupFailed = true;
            ReportUninstallFailure("Epic.UninstallCleanupFailed", ErrorSeverity.Warning, operation,
                appName, installPath, item.PrefixPath, removedDlcCount, mainGameRemoved: true, ex);
        }

        if (preservedInstallDirectory == null)
        {
            var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal);
            fields.Remove(CustomFieldKeyHelper.StoreUninstallRemainingInstallPath);
            item.CustomFields = fields;
        }
        return new EpicUninstallResult(prefixPreserved, preservedInstallDirectory, cleanupFailed, cleanupCancelled);
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void ReportUninstallFailure(string code, ErrorSeverity severity, string operation, string appName,
        string installPath, string? prefixPath, int removedDlcCount, bool mainGameRemoved, Exception? exception = null)
    {
        try
        {
            var context = new Dictionary<string, string>
            {
                ["Operation"] = operation,
                ["AppName"] = appName,
                ["InstallPath"] = installPath,
                ["RemovedDlcCount"] = removedDlcCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MainGameUninstalled"] = mainGameRemoved.ToString()
            };
            if (prefixPath != null) context["PrefixPath"] = prefixPath;
            if (exception != null) context["ExceptionType"] = exception.GetType().Name;
            // Legendary exceptions may embed CLI output/credentials. Never forward their payloads.
            _errorService.Report(new ErrorReport(code, severity, nameof(EpicInstallService), mainGameRemoved
                ? "Legendary confirmed game removal, but follow-up cleanup did not complete. Recovery paths were preserved."
                : "Epic game uninstallation was not confirmed. The local main-game install state was preserved.", Context: context));
        }
        catch { /* Diagnostic output must not replace the uninstall/cleanup outcome. */ }
    }

    private static void ClearInstallState(MediaItem item, bool prefixPreserved, string storedInstallPath)
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

        var fields = item.CustomFields
            .Where(entry => !installFields.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        fields[CustomFieldKeyHelper.StoreUninstallRemainingInstallPath] = storedInstallPath;
        item.CustomFields = fields;
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

        if (StoreInstallPathSafety.IsDangerousInstallPath(fullPath))
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

    private static bool ContainsSymbolicLinkInPath(string path)
        => StoreDirectoryDeletionHelper.ContainsSymbolicLinkInPath(path);

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
