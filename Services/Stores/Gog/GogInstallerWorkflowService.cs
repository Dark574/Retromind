using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Stores.Gog;

internal enum GogInstallerWorkflowFailureStage
{
    None = 0,
    Download = 1,
    Execution = 2,
    Cancelled = 3
}

internal sealed record GogInstallerWorkflowRequest(
    MediaItem Item,
    string StoreGameId,
    string InstallPath,
    GogInstallPlatform Platform,
    GogInstallerPackage InstallerPackage,
    string StagingDirectory,
    GogWindowsInstallerPreference WindowsInstallerPreference,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcuts,
    bool CleanInstall,
    bool UseTemporaryLinuxDestination,
    bool RequireLinuxPayloadChange);

internal sealed record GogInstallerWorkflowResult(
    bool Success,
    GogInstallerWorkflowFailureStage FailureStage,
    string? ErrorMessage = null,
    GogDownloadedInstallerPackage? DownloadedPackage = null);

/// <summary>
/// Coordinates the shared download and execution workflow for GOG base-game and DLC installers.
/// UI confirmation, dialogs, and media-item persistence remain with the application layer.
/// </summary>
public sealed class GogInstallerWorkflowService
{
    private static readonly object InstallerLogFileWriteLock = new();

    private readonly GogInstallService _installService;
    private readonly GogInstallerExecutionService _executionService;
    private readonly ProtonPrefixRelocationService _protonPrefixRelocationService;

    public GogInstallerWorkflowService(
        GogInstallService installService,
        GogInstallerExecutionService executionService,
        ProtonPrefixRelocationService protonPrefixRelocationService)
    {
        _installService = installService ?? throw new ArgumentNullException(nameof(installService));
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _protonPrefixRelocationService = protonPrefixRelocationService ??
                                         throw new ArgumentNullException(nameof(protonPrefixRelocationService));
    }

    internal async Task<GogInstallerWorkflowResult> RunAsync(
        GogInstallerWorkflowRequest request,
        Action<string> appendLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appendLog);

        await Task.Yield();
        appendLog("[Download] Starting installer download...");
        var reusableStagingFiles = CountExistingStagedInstallerFiles(
            request.InstallerPackage,
            request.StagingDirectory);
        if (reusableStagingFiles > 0)
        {
            appendLog(
                $"[Download] Validating {reusableStagingFiles}/{request.InstallerPackage.Files.Count} staged installer file(s) for reuse.");
        }

        GogDownloadedInstallerPackage downloadedPackage;
        try
        {
            downloadedPackage = await _installService.DownloadInstallerPackageAsync(
                    request.InstallerPackage,
                    request.StagingDirectory,
                    CreateDownloadProgress(appendLog),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[GOG] Installer download cancelled by user.");
            return new GogInstallerWorkflowResult(
                false,
                GogInstallerWorkflowFailureStage.Cancelled,
                "Installation cancelled by user.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Installer download failed: {ex.Message}");
            return new GogInstallerWorkflowResult(
                false,
                GogInstallerWorkflowFailureStage.Download,
                ex.Message);
        }

        appendLog("[Install] Starting installer execution...");
        var executionResult = await RunDownloadedInstallerAsync(
                request,
                downloadedPackage,
                appendLog,
                cancellationToken)
            .ConfigureAwait(false);
        return executionResult.Success
            ? new GogInstallerWorkflowResult(
                true,
                GogInstallerWorkflowFailureStage.None,
                DownloadedPackage: downloadedPackage)
            : executionResult with { DownloadedPackage = downloadedPackage };
    }

    internal static void DeleteStagingDirectoryBestEffort(string? stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory))
            return;

        string fullStagingPath;
        try
        {
            fullStagingPath = Path.GetFullPath(stagingDirectory);
        }
        catch
        {
            return;
        }

        var marker = $"{Path.DirectorySeparatorChar}.retromind-gog-installers{Path.DirectorySeparatorChar}";
        if (fullStagingPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        try
        {
            if (Directory.Exists(fullStagingPath))
                Directory.Delete(fullStagingPath, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to delete staging directory '{fullStagingPath}': {ex.Message}");
        }
    }

    internal static int CountExistingStagedInstallerFiles(
        GogInstallerPackage package,
        string stagingDirectory)
    {
        if (package.Files.Count == 0 ||
            string.IsNullOrWhiteSpace(stagingDirectory) ||
            !Directory.Exists(stagingDirectory))
        {
            return 0;
        }

        var existing = 0;
        foreach (var file in package.Files)
        {
            var fileName = SanitizeStagedInstallerFileName(file.FileName);
            if (File.Exists(Path.Combine(stagingDirectory, fileName)))
                existing++;
        }

        return existing;
    }

    internal static string BuildDownloadProgressLine(GogInstallerDownloadProgress progress)
    {
        var filePercent = CalculateProgressPercent(
            progress.BytesDownloadedCurrentFile,
            progress.BytesTotalCurrentFile);
        var overallPercent = CalculateProgressPercent(
            progress.BytesDownloadedOverall,
            progress.BytesTotalOverall);

        var filePart = $"{progress.FileIndex}/{progress.FileCount} {progress.FileName}";
        var fileBytes = progress.BytesTotalCurrentFile.HasValue && progress.BytesTotalCurrentFile.Value > 0
            ? $"{FormatByteSize(progress.BytesDownloadedCurrentFile)} / {FormatByteSize(progress.BytesTotalCurrentFile.Value)}"
            : FormatByteSize(progress.BytesDownloadedCurrentFile);
        var fileProgress = filePercent >= 0 ? $"{filePercent}% ({fileBytes})" : fileBytes;

        var overallProgress = progress.BytesTotalOverall.HasValue && progress.BytesTotalOverall.Value > 0
            ? overallPercent >= 0
                ? $"{overallPercent}% ({FormatByteSize(progress.BytesDownloadedOverall)} / {FormatByteSize(progress.BytesTotalOverall.Value)})"
                : $"{FormatByteSize(progress.BytesDownloadedOverall)} / {FormatByteSize(progress.BytesTotalOverall.Value)}"
            : FormatByteSize(progress.BytesDownloadedOverall);

        return $"Download {filePart}: {fileProgress} | Overall: {overallProgress}";
    }

    private async Task<GogInstallerWorkflowResult> RunDownloadedInstallerAsync(
        GogInstallerWorkflowRequest request,
        GogDownloadedInstallerPackage downloadedPackage,
        Action<string> appendLog,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(request.InstallPath);
            Directory.CreateDirectory(downloadedPackage.StagingDirectory);

            var installerLogPath = Path.Combine(downloadedPackage.StagingDirectory, "retromind-install.log");
            InitializeInstallerLogFile(
                installerLogPath,
                request.InstallPath,
                downloadedPackage.StagingDirectory);
            void AppendInstallerLog(string line)
            {
                TryAppendLineToInstallerLogFile(installerLogPath, line);
                appendLog(line);
            }

            AppendInstallerLog($"Detailed log file: {installerLogPath}");

            if (request.CleanInstall)
            {
                AppendInstallerLog("Clean install requested: removing existing target files.");
                if (!TryPrepareCleanInstallDirectory(
                        request.Item,
                        request.InstallPath,
                        downloadedPackage.StagingDirectory,
                        AppendInstallerLog,
                        out var cleanError))
                {
                    return ExecutionFailure(cleanError ?? "Clean install preparation failed.");
                }
            }

            GogInstallerExecutionResult result;
            if (request.Platform == GogInstallPlatform.Linux)
            {
                result = await _executionService.RunLinuxAsync(
                        new GogLinuxInstallerExecutionRequest(
                            request.StoreGameId,
                            request.InstallPath,
                            downloadedPackage,
                            request.UseTemporaryLinuxDestination,
                            request.RequireLinuxPayloadChange),
                        AppendInstallerLog,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var winePath = EmulatorResolverHelper.ResolveSystemWine();
                if (string.IsNullOrWhiteSpace(winePath))
                    return ExecutionFailure("Windows installation requires a system wine version.");

                var repairedPrefixPath = request.Item.PrefixPath;
                var prefixRepair = _protonPrefixRelocationService.Repair(request.Item);
                if (prefixRepair.IsProtonManagedPrefix)
                {
                    repairedPrefixPath = prefixRepair.PrefixPath ?? repairedPrefixPath;
                    if (prefixRepair.RepairedLinks > 0)
                    {
                        AppendInstallerLog(
                            $"[Windows prefix] Repaired {prefixRepair.RepairedLinks} relocated Proton runtime link(s).");
                    }

                    if (prefixRepair.FailedLinks > 0)
                    {
                        return ExecutionFailure(
                            $"Could not repair {prefixRepair.FailedLinks} relocated Proton prefix link(s).");
                    }

                    if (prefixRepair.ProtonRootPath == null && prefixRepair.UnresolvedLinks > 0)
                    {
                        return ExecutionFailure(
                            "The existing prefix contains relocated Proton links, but its configured Proton runner is unavailable.");
                    }

                    if (prefixRepair.UnresolvedLinks > 0)
                    {
                        AppendInstallerLog(
                            $"[Windows prefix] Warning: {prefixRepair.UnresolvedLinks} obsolete Proton link(s) " +
                            "have no equivalent in the selected runner.");
                    }
                }

                result = await _executionService.RunWindowsAsync(
                        new GogWindowsInstallerExecutionRequest(
                            request.StoreGameId,
                            request.Item.Title,
                            repairedPrefixPath,
                            request.InstallPath,
                            winePath,
                            downloadedPackage,
                            request.WindowsInstallerPreference,
                            request.CreateDesktopShortcut,
                            request.CreateStartMenuShortcuts),
                        AppendInstallerLog,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return result.Success
                ? new GogInstallerWorkflowResult(true, GogInstallerWorkflowFailureStage.None)
                : ExecutionFailure(result.ErrorMessage);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            appendLog("Error: executable not found.");
            Debug.WriteLine($"[GOG] Installer execution failed: {ex.Message}");
            return ExecutionFailure(ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 13)
        {
            appendLog("Error: permission denied while starting installer.");
            Debug.WriteLine($"[GOG] Installer execution failed: {ex.Message}");
            return ExecutionFailure(ex.Message);
        }
        catch (OperationCanceledException)
        {
            appendLog("Installer execution cancelled by user.");
            Debug.WriteLine("[GOG] Installer execution cancelled by user.");
            return new GogInstallerWorkflowResult(
                false,
                GogInstallerWorkflowFailureStage.Cancelled,
                "Installation cancelled by user.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Installer execution failed: {ex.Message}");
            appendLog($"Error: {ex.Message}");
            return ExecutionFailure(ex.Message);
        }
    }

    private static GogInstallerWorkflowResult ExecutionFailure(string? message) =>
        new(false, GogInstallerWorkflowFailureStage.Execution, message);

    private static IProgress<GogInstallerDownloadProgress> CreateDownloadProgress(Action<string> appendLog)
    {
        var lastLoggedFileIndex = -1;
        var lastLoggedFilePercent = -1;
        var lastLoggedOverallPercent = -1;
        var lastLoggedAtUtc = DateTimeOffset.MinValue;

        return new Progress<GogInstallerDownloadProgress>(progress =>
        {
            var now = DateTimeOffset.UtcNow;
            var currentFilePercent = CalculateProgressPercent(
                progress.BytesDownloadedCurrentFile,
                progress.BytesTotalCurrentFile);
            var overallPercent = CalculateProgressPercent(
                progress.BytesDownloadedOverall,
                progress.BytesTotalOverall);
            var fileChanged = progress.FileIndex != lastLoggedFileIndex;
            var fileAdvanced = currentFilePercent >= 0 && currentFilePercent >= lastLoggedFilePercent + 2;
            var overallAdvanced = overallPercent >= 0 && overallPercent >= lastLoggedOverallPercent + 1;
            var timedPulse = now - lastLoggedAtUtc >= TimeSpan.FromMilliseconds(900);

            if (!fileChanged && !fileAdvanced && !overallAdvanced && !timedPulse)
                return;

            lastLoggedFileIndex = progress.FileIndex;
            if (currentFilePercent >= 0)
                lastLoggedFilePercent = currentFilePercent;
            if (overallPercent >= 0)
                lastLoggedOverallPercent = overallPercent;
            lastLoggedAtUtc = now;
            appendLog(BuildDownloadProgressLine(progress));
        });
    }

    private static int CalculateProgressPercent(long downloadedBytes, long? totalBytes)
    {
        if (!totalBytes.HasValue || totalBytes.Value <= 0)
            return -1;

        return (int)Math.Clamp(
            Math.Round(downloadedBytes * 100.0 / totalBytes.Value, MidpointRounding.AwayFromZero),
            0,
            100);
    }

    private static string FormatByteSize(long bytes)
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

    private static string SanitizeStagedInstallerFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "installer.bin";

        var sanitized = fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(sanitized) ? "installer.bin" : sanitized;
    }

    private static bool TryPrepareCleanInstallDirectory(
        MediaItem item,
        string installPath,
        string? preservePath,
        Action<string> appendLog,
        out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(installPath))
            return true;

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            item,
            rejectSymbolicLinks: true);
        var fullInstallPath = assessment.FullPath;
        if (!assessment.IsAllowed)
        {
            errorMessage = "Clean install was blocked because the selected folder is unsafe or is no longer owned by this Retromind item.";
            appendLog(errorMessage);
            return false;
        }

        Directory.CreateDirectory(fullInstallPath);
        var preservedPaths = new List<string>
        {
            Path.Combine(fullInstallPath, GogInstallDirectorySafety.MarkerFileName)
        };
        if (!string.IsNullOrWhiteSpace(preservePath))
        {
            var fullPreservePath = Path.GetFullPath(preservePath);
            if (IsSubPathOfOrEqual(fullPreservePath, fullInstallPath))
                preservedPaths.Add(fullPreservePath);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(fullInstallPath))
        {
            if (ShouldPreserveDuringCleanInstall(entry, preservedPaths))
            {
                appendLog($"Preserving: {entry}");
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else if (File.Exists(entry))
                    File.Delete(entry);
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to remove '{entry}': {ex.Message}";
                appendLog(errorMessage);
                return false;
            }
        }

        try
        {
            GogInstallDirectorySafety.WriteMarker(fullInstallPath, item);
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to restore the Retromind install marker: {ex.Message}";
            appendLog(errorMessage);
            return false;
        }

        return true;
    }

    private static bool ShouldPreserveDuringCleanInstall(
        string candidatePath,
        IReadOnlyList<string> preservedPaths)
    {
        if (preservedPaths.Count == 0 || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        var fullCandidatePath = Path.GetFullPath(candidatePath);
        foreach (var preservedPath in preservedPaths)
        {
            if (PathsEqual(fullCandidatePath, preservedPath) ||
                IsSubPathOfOrEqual(preservedPath, fullCandidatePath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathsEqual(string pathA, string pathB)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), comparison);
    }

    private static bool IsSubPathOfOrEqual(string path, string potentialParentPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(potentialParentPath))
            return false;

        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(potentialParentPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullPath, fullParent, comparison))
            return true;

        var parentWithSeparator = fullParent.EndsWith(Path.DirectorySeparatorChar)
            ? fullParent
            : fullParent + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(parentWithSeparator, comparison);
    }

    private static void InitializeInstallerLogFile(
        string logFilePath,
        string installPath,
        string stagingPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var header = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] Retromind GOG installer log{Environment.NewLine}" +
                         $"Install path: {installPath}{Environment.NewLine}" +
                         $"Staging path: {stagingPath}{Environment.NewLine}" +
                         new string('-', 70) + Environment.NewLine;
            File.WriteAllText(logFilePath, header, Encoding.UTF8);
        }
        catch
        {
            // Detailed file logging is best-effort; the visible process log remains available.
        }
    }

    private static void TryAppendLineToInstallerLogFile(string logFilePath, string line)
    {
        try
        {
            lock (InstallerLogFileWriteLock)
            {
                File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Detailed file logging is best-effort; the visible process log remains available.
        }
    }
}
