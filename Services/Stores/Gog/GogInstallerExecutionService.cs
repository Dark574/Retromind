using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

internal sealed record GogLinuxInstallerExecutionRequest(
    string StoreGameId,
    string InstallPath,
    GogDownloadedInstallerPackage DownloadedPackage,
    bool UseTemporaryDestination,
    bool RequirePayloadChange);

internal sealed record GogInstallerExecutionResult(bool Success, string? ErrorMessage = null);

/// <summary>
/// Owns platform-specific GOG installer workflows independently from dialogs
/// and media-item mutation.
/// </summary>
public sealed partial class GogInstallerExecutionService
{
    private readonly GogInstallerProcessService _processService;
    private readonly string _libraryRoot;

    public GogInstallerExecutionService(GogInstallerProcessService processService, string libraryRoot)
    {
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("A library root is required.", nameof(libraryRoot));

        _libraryRoot = Path.GetFullPath(libraryRoot);
    }

    internal async Task<GogInstallerExecutionResult> RunLinuxAsync(
        GogLinuxInstallerExecutionRequest request,
        Action<string> appendLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appendLog);

        GogInstallerExecutionResult Fail(string message) => new(false, message);

        Directory.CreateDirectory(request.InstallPath);
        Directory.CreateDirectory(request.DownloadedPackage.StagingDirectory);

        var baseline = request.RequirePayloadChange
            ? GogInstallPayloadTracker.Capture(request.InstallPath)
            : default;
        var installerPath = request.DownloadedPackage.EntryFilePath;
        if (!File.Exists(installerPath))
            return Fail("Linux installer entry file was not found.");

        EnsureExecutableBitBestEffort(installerPath);
        appendLog($"Install path: {request.InstallPath}");
        appendLog($"Staging path: {request.DownloadedPackage.StagingDirectory}");

        var effectiveInstallPath = request.UseTemporaryDestination
            ? ResolveSafeDestinationPath(request.InstallPath, request.StoreGameId)
            : request.InstallPath;
        string? existingInstallAlias = null;
        if (!request.UseTemporaryDestination &&
            OperatingSystem.IsLinux() &&
            HasShellSensitivePathCharacters(request.InstallPath))
        {
            try
            {
                existingInstallAlias = CreateSafeDestinationAlias(request.InstallPath, request.StoreGameId);
                effectiveInstallPath = existingInstallAlias;
                appendLog($"Installer destination (safe alias): {effectiveInstallPath}");
            }
            catch (Exception ex)
            {
                return Fail($"Could not create a safe Linux installer destination alias: {ex.Message}");
            }
        }

        var usesTemporaryInstallPath = request.UseTemporaryDestination &&
                                       !PathsEqual(effectiveInstallPath, request.InstallPath);
        if (usesTemporaryInstallPath)
        {
            Directory.CreateDirectory(effectiveInstallPath);
            appendLog($"Installer destination (temporary): {effectiveInstallPath}");
        }

        var compatibilityEnvironment = _processService.PrepareLinuxCompatibilityEnvironment(appendLog);
        try
        {
            var profiles = BuildArgumentProfiles(effectiveInstallPath);
            var profileSucceeded = false;
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                var startInfo = _processService.CreateStartInfo(request.DownloadedPackage.StagingDirectory);
                GogInstallerProcessService.ApplyLinuxCompatibilityEnvironment(startInfo, compatibilityEnvironment);
                startInfo.FileName = installerPath;
                foreach (var argument in profile.Arguments)
                    startInfo.ArgumentList.Add(argument);

                appendLog($"[Linux installer] Attempt {index + 1}/{profiles.Count} ({profile.Name})");
                appendLog($"> {GogInstallerProcessService.FormatCommand(startInfo)}");

                var execution = await _processService.ExecuteAsync(
                    startInfo,
                    appendLog,
                    cancellationToken).ConfigureAwait(false);
                if (!execution.Started)
                    return Fail(execution.StartErrorMessage ?? "Installer process could not be started.");

                if (execution.ExitCode == 0)
                {
                    profileSucceeded = true;
                    break;
                }

                if (index < profiles.Count - 1 &&
                    (execution.HasUnsupportedFlagsError ||
                     execution.HasShellParsingError ||
                     execution.HasTerminalSpawnError))
                {
                    if (execution.HasUnsupportedFlagsError)
                        appendLog("Installer rejected flags, retrying with compatibility profile.");
                    else if (execution.HasShellParsingError)
                        appendLog("Installer hit shell argument parsing issue, retrying with compatibility profile.");
                    else
                        appendLog("Installer hit terminal launch issue, retrying with compatibility profile.");
                    continue;
                }

                break;
            }

            if (!profileSucceeded)
            {
                appendLog("[Linux installer] Fallback: extract mode (noexec + startmojo direct)");
                var fallbackResult = await RunViaExtractedStartMojoAsync(
                    installerPath,
                    request.DownloadedPackage.StagingDirectory,
                    effectiveInstallPath,
                    compatibilityEnvironment,
                    appendLog,
                    cancellationToken).ConfigureAwait(false);
                if (!fallbackResult.Success)
                    return fallbackResult;
            }
        }
        finally
        {
            GogInstallerProcessService.CleanupLinuxCompatibilityEnvironment(compatibilityEnvironment);
            TryDeleteDestinationAlias(existingInstallAlias);
        }

        if (usesTemporaryInstallPath)
        {
            appendLog($"Promoting install from temporary path to requested path: {request.InstallPath}");
            MoveDirectoryContentsOverwrite(effectiveInstallPath, request.InstallPath);
            TryDeleteDirectoryIfEmpty(effectiveInstallPath);

            try
            {
                var repairedFiles = GogLinuxInstallRelocationRepair.RepairMovedInstallation(
                    request.InstallPath,
                    effectiveInstallPath);
                if (repairedFiles > 0)
                    appendLog($"Repaired {repairedFiles} relocated MojoSetup metadata file(s).");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Failed to repair relocated Linux installer metadata: {ex.Message}");
                appendLog($"Warning: relocated MojoSetup metadata could not be repaired: {ex.Message}");
            }
        }

        EnsureInstalledExecutablePermissionsBestEffort(request.InstallPath);
        if (request.RequirePayloadChange && !GogInstallPayloadTracker.HasChanged(request.InstallPath, baseline))
        {
            return Fail(
                "Linux installer exited without changing game files. " +
                "The installation may have been declined or cancelled.");
        }

        return new GogInstallerExecutionResult(true);
    }

    internal static bool HasShellSensitivePathCharacters(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var character in path)
        {
            if (character is ' ' or '\t' or '\r' or '\n' or '(' or ')' or '\'' or '"' or '`' or '$' or
                '&' or ';' or '|' or '<' or '>' or '*' or '?' or '[' or ']' or '{' or '}' or '!')
            {
                return true;
            }
        }

        return false;
    }

    private async Task<GogInstallerExecutionResult> RunViaExtractedStartMojoAsync(
        string installerPath,
        string installerWorkingDirectory,
        string installPath,
        GogLinuxInstallerCompatibilityEnvironment compatibilityEnvironment,
        Action<string> appendLog,
        CancellationToken cancellationToken)
    {
        var extractionRoot = Path.Combine(
            Path.GetTempPath(),
            "retromind-gog-extract",
            $"extract-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(extractionRoot);
        try
        {
            var extractStartInfo = _processService.CreateStartInfo(installerWorkingDirectory);
            GogInstallerProcessService.ApplyLinuxCompatibilityEnvironment(extractStartInfo, compatibilityEnvironment);
            extractStartInfo.FileName = installerPath;
            extractStartInfo.ArgumentList.Add("--noexec");
            extractStartInfo.ArgumentList.Add("--target");
            extractStartInfo.ArgumentList.Add(extractionRoot);

            appendLog($"> {GogInstallerProcessService.FormatCommand(extractStartInfo)}");
            var extractExecution = await _processService.ExecuteAsync(
                extractStartInfo,
                appendLog,
                cancellationToken).ConfigureAwait(false);
            if (!extractExecution.Started)
            {
                return new GogInstallerExecutionResult(
                    false,
                    extractExecution.StartErrorMessage ?? "Installer extraction process could not be started.");
            }

            if (extractExecution.ExitCode != 0)
                return new GogInstallerExecutionResult(false, $"Exit code {extractExecution.ExitCode}");

            var startMojoPath = Path.Combine(extractionRoot, "startmojo.sh");
            if (!File.Exists(startMojoPath))
                return new GogInstallerExecutionResult(false, "Extracted Linux installer is missing startmojo.sh.");

            EnsureExecutableBitBestEffort(startMojoPath);
            var runStartMojoInfo = _processService.CreateStartInfo(extractionRoot);
            GogInstallerProcessService.ApplyLinuxCompatibilityEnvironment(runStartMojoInfo, compatibilityEnvironment);
            runStartMojoInfo.FileName = startMojoPath;
            runStartMojoInfo.ArgumentList.Add("--i-agree-to-all-licenses");
            runStartMojoInfo.ArgumentList.Add("--noreadme");
            runStartMojoInfo.ArgumentList.Add("--destination");
            runStartMojoInfo.ArgumentList.Add(installPath);

            appendLog($"> {GogInstallerProcessService.FormatCommand(runStartMojoInfo)}");
            var runExecution = await _processService.ExecuteAsync(
                runStartMojoInfo,
                appendLog,
                cancellationToken).ConfigureAwait(false);
            if (!runExecution.Started)
            {
                return new GogInstallerExecutionResult(
                    false,
                    runExecution.StartErrorMessage ?? "startmojo process could not be started.");
            }

            return runExecution.ExitCode == 0
                ? new GogInstallerExecutionResult(true)
                : new GogInstallerExecutionResult(false, $"Exit code {runExecution.ExitCode}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractionRoot))
                    Directory.Delete(extractionRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of extracted installer files.
            }
        }
    }

    private static IReadOnlyList<LinuxInstallerArgumentProfile> BuildArgumentProfiles(string installPath) =>
    [
        new(
            "makeself-pass-through",
            ["--nox11", "--", "--i-agree-to-all-licenses", "--noreadme", "--destination", installPath]),
        new(
            "legacy-direct",
            ["--nox11", "--i-agree-to-all-licenses", "--noreadme", "--destination", installPath]),
        new(
            "pass-through-destination-only",
            ["--nox11", "--", "--destination", installPath]),
        new(
            "legacy-destination-only",
            ["--nox11", "--destination", installPath])
    ];

    private static string ResolveSafeDestinationPath(string requestedInstallPath, string storeGameId)
    {
        if (!HasShellSensitivePathCharacters(requestedInstallPath))
            return requestedInstallPath;

        var safeId = SanitizeStoreGameId(storeGameId);
        var safeRoot = Path.Combine(Path.GetTempPath(), "retromind-gog-install", safeId);
        Directory.CreateDirectory(safeRoot);
        return Path.Combine(
            safeRoot,
            $"install-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
    }

    private static string CreateSafeDestinationAlias(string requestedInstallPath, string storeGameId)
    {
        var aliasRoot = Path.Combine(Path.GetTempPath(), "retromind-gog-install-targets");
        Directory.CreateDirectory(aliasRoot);
        var aliasPath = Path.Combine(aliasRoot, $"{SanitizeStoreGameId(storeGameId)}-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(aliasPath, Path.GetFullPath(requestedInstallPath));
        return aliasPath;
    }

    private static string SanitizeStoreGameId(string storeGameId)
    {
        var safeId = string.IsNullOrWhiteSpace(storeGameId)
            ? "gog"
            : new string(storeGameId.Where(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(safeId) ? "gog" : safeId;
    }

    private static void TryDeleteDestinationAlias(string? aliasPath)
    {
        if (string.IsNullOrWhiteSpace(aliasPath))
            return;

        try
        {
            File.Delete(aliasPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to delete Linux installer destination alias '{aliasPath}': {ex.Message}");
        }
    }

    private static void MoveDirectoryContentsOverwrite(string sourceDirectory, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return;

        Directory.CreateDirectory(targetDirectory);
        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (string.IsNullOrWhiteSpace(directoryName))
                continue;

            var targetSubDirectory = Path.Combine(targetDirectory, directoryName);
            Directory.CreateDirectory(targetSubDirectory);
            MoveDirectoryContentsOverwrite(directoryPath, targetSubDirectory);
            TryDeleteDirectoryIfEmpty(directoryPath);
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var targetFile = Path.Combine(targetDirectory, fileName);
            var sourceMode = TryGetUnixFileModeBestEffort(filePath);
            try
            {
                File.Move(filePath, targetFile, overwrite: true);
            }
            catch (IOException)
            {
                File.Copy(filePath, targetFile, overwrite: true);
                TrySetUnixFileModeBestEffort(targetFile, sourceMode);
                File.Delete(filePath);
            }
        }
    }

    private static UnixFileMode? TryGetUnixFileModeBestEffort(string path)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return File.GetUnixFileMode(path);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetUnixFileModeBestEffort(string path, UnixFileMode? mode)
    {
        if (!OperatingSystem.IsLinux() || mode == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            File.SetUnixFileMode(path, mode.Value);
        }
        catch
        {
            // Best-effort mode preservation when a move crosses filesystems.
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string directoryPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath) &&
            Directory.Exists(directoryPath) &&
            !Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            Directory.Delete(directoryPath, recursive: false);
        }
    }

    private static bool PathsEqual(string pathA, string pathB)
        => FileSystemPathIdentity.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB));

    private static void EnsureInstalledExecutablePermissionsBestEffort(string installRoot)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return;

        string[] allFiles;
        try
        {
            allFiles = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return;
        }

        foreach (var filePath in allFiles)
        {
            if (!File.Exists(filePath) || IsInsideInstallerStaging(filePath) || !ShouldEnsureExecutableBit(filePath))
                continue;

            LinuxFileSystemHelper.EnsureExecutableBitBestEffort(filePath);
        }
    }

    private static bool ShouldEnsureExecutableBit(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        if (fileName.Equals("start.sh", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".run", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".x86", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".x86_64", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".AppImage", StringComparison.OrdinalIgnoreCase);
        }

        return LooksLikeElfBinary(filePath);
    }

    private static bool LooksLikeElfBinary(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 4)
                return false;

            Span<byte> magic = stackalloc byte[4];
            return stream.Read(magic) == 4 &&
                   magic[0] == 0x7F &&
                   magic[1] == (byte)'E' &&
                   magic[2] == (byte)'L' &&
                   magic[3] == (byte)'F';
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureExecutableBitBestEffort(string filePath)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // The installer may already be executable or live on a filesystem without Unix modes.
        }
    }

    private static bool IsInsideInstallerStaging(string path)
        => path.IndexOf(".retromind-gog-installers", FileSystemPathIdentity.Comparison) >= 0;

    private sealed record LinuxInstallerArgumentProfile(string Name, IReadOnlyList<string> Arguments);
}
