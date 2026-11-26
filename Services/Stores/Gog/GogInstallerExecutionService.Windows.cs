using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

public enum GogWindowsInstallerPreference
{
    AutoPrefer64 = 0,
    Prefer64 = 1,
    Prefer32 = 2
}

internal sealed record GogWindowsInstallerExecutionRequest(
    string StoreGameId,
    string ItemTitle,
    string? PrefixPath,
    string InstallPath,
    string WinePath,
    GogDownloadedInstallerPackage DownloadedPackage,
    GogWindowsInstallerPreference InstallerPreference,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcuts,
    PrefixResolution? ResolvedPrefix = null,
    string? LogDirectory = null,
    bool ValidatePackageFiles = false);

public sealed partial class GogInstallerExecutionService
{
    internal async Task<GogInstallerExecutionResult> RunWindowsAsync(
        GogWindowsInstallerExecutionRequest request,
        Action<string> appendLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appendLog);
        cancellationToken.ThrowIfCancellationRequested();

        GogInstallerExecutionResult Fail(string message) => new(false, message);

        if (string.IsNullOrWhiteSpace(request.WinePath))
            return Fail("Windows installation requires a system wine version.");

        var prefix = request.ResolvedPrefix ?? ResolveWindowsPrefix(request.StoreGameId, request.ItemTitle, request.PrefixPath);
        if (!prefix.CanPrepare || prefix.FromOverride && !WinePrefixService.StillSelectsSameTree(prefix))
            return Fail("The Windows prefix could not be safely selected. Check the prefix layout and overrides.");
        WinePrefixService.PreparePortableProfile(prefix, _libraryRoot);
        var prefixRoot = prefix.ResolvedWindowsPrefix!;
        Directory.CreateDirectory(request.InstallPath);
        Directory.CreateDirectory(request.DownloadedPackage.StagingDirectory);
        var prefixDrivePath = Path.Combine(prefixRoot, "drive_c");
        var dosdevicesPath = Path.Combine(prefixRoot, "dosdevices");
        var windowsInstallDestinationPath = ToWineWindowsAbsolutePath(request.InstallPath);

        // Wine only creates C: and Z: when it creates dosdevices itself. Our games-drive mapping
        // scaffolds that directory first, so supply the defaults before starting Wine. Also fills
        // missing mappings in a prefix left by an earlier failed attempt; never overwrite user mappings.
        Directory.CreateDirectory(prefixDrivePath);
        Exception? mappingFailure = null;
        PrefixPathHelper.EnsureDosDeviceMapping(dosdevicesPath, "c:", "../drive_c", ex => mappingFailure ??= ex);
        PrefixPathHelper.EnsureDosDeviceMapping(dosdevicesPath, "z:", "/", ex => mappingFailure ??= ex);
        if (mappingFailure != null || !Directory.Exists(Path.Combine(dosdevicesPath, "c:")) ||
            !Directory.Exists(Path.Combine(dosdevicesPath, "z:")))
        {
            return Fail("Could not prepare required Wine drive mappings C: and Z:. " +
                        "Check prefix permissions and existing mappings. Existing files and mappings were preserved.");
        }

        PrefixPathHelper.EnsurePortableGamesDriveMapping(
            prefix.CompatRoot,
            dosdevicesPath,
            _libraryRoot);

        appendLog($"Windows prefix root: {prefixRoot}");
        appendLog($"Windows prefix dosdevices: {Path.Combine(prefixRoot, "dosdevices")}");
        GogInstallerProcessService.AppendWineDosDeviceMappings(appendLog, prefixRoot);
        appendLog($"Windows installer destination: {windowsInstallDestinationPath}");

        var installerCandidates = BuildWindowsInstallerEntryCandidates(
            request.DownloadedPackage,
            request.InstallerPreference);
        var installerPath = installerCandidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(installerPath))
            return Fail("Windows installer entry file was not found.");

        appendLog($"[Windows installer] Using: {installerPath}");
        var metadata = request.ValidatePackageFiles
            ? await GogPackageHashDbReader.TryReadAsync(request.DownloadedPackage, installerPath,
                request.StoreGameId, appendLog, cancellationToken).ConfigureAwait(false)
            : null;
        var manifest = metadata?.HashDb;
        var innoFiles = metadata?.InnoFiles;
        if (manifest != null)
            appendLog($"[Package files] GOG-HashDB: Loaded {manifest.Count} expected files from the selected package.");
        else if (innoFiles != null)
            appendLog($"[Package files] Inno-Dateiliste: Loaded {innoFiles.Count} candidate game files.");
        else if (request.ValidatePackageFiles)
            appendLog("[Package files] nicht verifiziert: No usable GOG-HashDB or Inno file list.");

        var verifyAllHashes = Environment.GetEnvironmentVariable("RETROMIND_GOG_VERIFY_MD5") == "1";
        var prefixPayloadBaseline = GogInstallPayloadTracker.Capture(prefixDrivePath);
        var logDirectory = request.LogDirectory ?? request.DownloadedPackage.StagingDirectory;
        var logId = Guid.NewGuid().ToString("N");
        var profiles = BuildWindowsInstallerArgumentProfiles(
            request.CreateDesktopShortcut,
            request.CreateStartMenuShortcuts);
        string? lastError = null;
        var targetChanged = false;

        for (var attemptIndex = 0; attemptIndex < profiles.Count; attemptIndex++)
        {
            var profile = profiles[attemptIndex];
            var payloadBaseline = GogInstallPayloadTracker.Capture(request.InstallPath);
            var existingFiles = manifest?.CaptureExistingFiles(request.InstallPath, cancellationToken);
            var startInfo = EmulatorResolverHelper.BuildWineInstallStartInfo(request.WinePath, prefixRoot);
            if (prefix.PortableProfile)
                startInfo.Environment["USER"] = WinePrefixService.PortableProfileUser;
            startInfo.WorkingDirectory = request.DownloadedPackage.StagingDirectory;
            GogWindowsShortcutPolicy.ApplyInstallerEnvironment(
                startInfo,
                request.CreateDesktopShortcut,
                request.CreateStartMenuShortcuts);
            startInfo.ArgumentList.Add(installerPath);

            if (profile.AdditionalArguments is { Count: > 0 })
            {
                foreach (var argument in profile.AdditionalArguments)
                {
                    if (!string.IsNullOrWhiteSpace(argument))
                        startInfo.ArgumentList.Add(argument);
                }
            }

            var directoryArgument = BuildWindowsInstallerDirectoryArgument(
                windowsInstallDestinationPath,
                profile.DirectoryArgumentPrefix);
            if (!string.IsNullOrEmpty(directoryArgument))
                startInfo.ArgumentList.Add(directoryArgument);

            var innoLogHostPath = Path.Combine(logDirectory, $"inno-setup-{logId}-{attemptIndex + 1}.log");
            startInfo.ArgumentList.Add(BuildInnoSetupLogArgument(innoLogHostPath));

            appendLog($"[Windows installer] Inno log path: {innoLogHostPath}");
            appendLog($"[Windows installer] Attempt {attemptIndex + 1}/{profiles.Count} ({profile.Name})");
            GogInstallerProcessService.AppendRunnerEnvironmentSnapshot(appendLog, startInfo);
            appendLog($"> {GogInstallerProcessService.FormatCommand(startInfo)}");

            GogInstallerProcessResult execution;
            try
            {
                execution = await _processService.ExecuteAsync(
                    startInfo,
                    appendLog,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Wine setup can outlive/reparent away from its original launcher. A tree kill alone
                // cannot stop that prefix's server once the launcher has exited.
                await StopInstallerWinePrefixAsync(request.WinePath, prefixRoot, appendLog).ConfigureAwait(false);
                throw;
            }
            finally
            {
                var removedShortcutExports = GogWindowsShortcutPolicy.RemoveUnwantedExports(
                    prefixRoot,
                    request.CreateDesktopShortcut,
                    request.CreateStartMenuShortcuts,
                    startInfo.Environment);
                if (removedShortcutExports > 0)
                {
                    appendLog(
                        $"[Windows installer] Removed {removedShortcutExports} unwanted Wine shortcut export(s).");
                }
            }

            if (!execution.Started)
                return Fail(execution.StartErrorMessage ?? "Installer process could not be started.");

            appendLog(
                $"[Windows installer] Process outcome: exit={execution.ExitCode}, " +
                $"durationMs={execution.DurationMs}, runtimeCrash={execution.HasRuntimeCrashError}, " +
                $"unsupportedFlags={execution.HasUnsupportedFlagsError}, " +
                $"shellParse={execution.HasShellParsingError}, " +
                $"terminalSpawn={execution.HasTerminalSpawnError}");

            lastError = GetWindowsInstallerFailure(execution, innoLogHostPath);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageVerified = false;
                if (lastError == null && manifest != null)
                {
                    lastError = await manifest.ValidateAsync(request.InstallPath, existingFiles!, verifyAllHashes,
                        cancellationToken).ConfigureAwait(false);
                    packageVerified = lastError == null;
                }
                else if (lastError == null && innoFiles != null)
                {
                    var verification = await innoFiles.ValidateAsync(request.InstallPath, windowsInstallDestinationPath,
                        innoLogHostPath, cancellationToken).ConfigureAwait(false);
                    lastError = verification.Failure;
                    packageVerified = verification.Complete;
                    appendLog($"[Package files] Inno-Dateiliste: {verification.Verified}/{verification.Total} " +
                              "candidate game files match their package size and checksum.");
                }

                if (lastError == null && !packageVerified && !await GogInstallPayloadTracker.WaitForChangeAsync(
                        request.InstallPath,
                        payloadBaseline,
                        TimeSpan.FromSeconds(25),
                        cancellationToken).ConfigureAwait(false))
                {
                    lastError = "Windows installer did not place or update files in the target directory.";
                }

                if (lastError == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (manifest != null)
                    {
                        appendLog(verifyAllHashes
                            ? "[Package files] Installation completed. All package file checksums match."
                            : "[Package files] Installation completed. Expected package files are present.");
                    }
                    else if (!packageVerified && request.ValidatePackageFiles)
                    {
                        appendLog("[Package files] Installation completed. File integrity was not verified.");
                        appendLog("[Package files] Result: nicht verifiziert.");
                    }
                    if (attemptIndex > 0)
                        appendLog("[Windows installer] Interactive fallback completed.");

                    return new GogInstallerExecutionResult(true,
                        PackageFilesVerified: request.ValidatePackageFiles ? packageVerified : null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Detached Wine setup may also remain after the launcher has closed its output pipes.
                await StopInstallerWinePrefixAsync(request.WinePath, prefixRoot, appendLog).ConfigureAwait(false);
                throw;
            }
            targetChanged |= GogInstallPayloadTracker.HasChanged(request.InstallPath, payloadBaseline);
            appendLog($"[Windows installer] {lastError}");

            if (attemptIndex < profiles.Count - 1)
            {
                appendLog(
                    "[Windows installer] Silent attempt failed or produced no payload. " +
                    "Starting interactive fallback with prefilled target directory.");
            }
        }

        if (!targetChanged && GogInstallPayloadTracker.HasChanged(prefixDrivePath, prefixPayloadBaseline))
        {
            appendLog(
                $"Installer changed files inside prefix drive_c ({prefixDrivePath}), " +
                "but target path stayed unchanged. /DIR may have been ignored.");
        }

        lastError ??= "Windows installer did not complete successfully.";
        if (targetChanged)
            lastError += " The target directory was modified and may contain an incomplete installation.";

        return Fail(lastError);
    }

    private static string? GetWindowsInstallerFailure(GogInstallerProcessResult execution, string innoLogPath)
    {
        if (execution.HasRuntimeCrashError)
            return $"Windows installer crashed (exit code {execution.ExitCode}).";
        if (execution.ExitCode != 0)
            return $"Windows installer exited with code {execution.ExitCode}.";
        if (execution.HasUnsupportedFlagsError || execution.HasShellParsingError || execution.HasTerminalSpawnError)
            return "Windows installer reported a startup error.";

        try
        {
            // Some Windows installers ignore /LOG. When Inno does write a log, check
            // both markers because a Wine loader can exit without forwarding a child crash.
            var installationSucceeded = false;
            string? lastLine = null;
            foreach (var line in File.ReadLines(innoLogPath))
            {
                if (GogInstallerProcessService.LooksLikeRuntimeCrashError(line))
                    return "Windows installer log reports a crash.";
                if (line.EndsWith("Installation process succeeded.", StringComparison.Ordinal))
                    installationSucceeded = true;
                if (!string.IsNullOrWhiteSpace(line))
                    lastLine = line;
            }

            return installationSucceeded && lastLine?.EndsWith("Log closed.", StringComparison.Ordinal) == true
                ? null
                : "Windows installer log does not confirm a completed installation.";
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not read Windows installer log: {ex.Message}";
        }
    }

    private async Task StopInstallerWinePrefixAsync(string winePath, string prefixRoot, Action<string> appendLog)
    {
        try
        {
            var wineDirectory = Path.IsPathRooted(winePath) ? Path.GetDirectoryName(winePath) : null;
            var wineServerPath = wineDirectory == null ? null : Path.Combine(wineDirectory, "wineserver");
            if (wineServerPath == null || !File.Exists(wineServerPath))
            {
                appendLog("[Windows prefix] Could not find wineserver beside the selected Wine executable. " +
                          "Detached Wine processes may need to be stopped manually.");
                return;
            }

            appendLog($"[Windows prefix] Stopping Wine processes for this prefix: {prefixRoot}");
            var startInfo = EmulatorResolverHelper.BuildWineInstallStartInfo(wineServerPath, prefixRoot);
            startInfo.WorkingDirectory = prefixRoot;
            startInfo.ArgumentList.Add("-k");
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _processService.ExecuteAsync(startInfo, appendLog, stopTimeout.Token).ConfigureAwait(false);
            if (!result.Started || result.ExitCode != 0)
                appendLog("[Windows prefix] Wine prefix shutdown failed. Detached Wine processes may still be running.");
        }
        catch (Exception ex)
        {
            try { appendLog($"[Windows prefix] Could not complete Wine prefix shutdown: {ex.Message}"); }
            catch { /* A failed diagnostic callback must not replace the user's cancellation. */ }
        }
    }

    internal static IReadOnlyList<string> BuildWindowsShortcutArguments(
        bool createDesktopShortcut,
        bool createStartMenuShortcuts)
    {
        var arguments = new List<string>(3);
        if (!createDesktopShortcut)
        {
            arguments.Add("/nodesktopshorctut");
            arguments.Add("/nodesktopshortcut");
        }

        if (!createStartMenuShortcuts)
            arguments.Add("/nostartmenushortcut");

        return arguments;
    }

    internal static IReadOnlyList<string> BuildWindowsInstallerEntryCandidates(
        GogDownloadedInstallerPackage downloadedPackage,
        GogWindowsInstallerPreference preference)
    {
        ArgumentNullException.ThrowIfNull(downloadedPackage);

        var candidates = downloadedPackage.DownloadedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (!string.IsNullOrWhiteSpace(downloadedPackage.EntryFilePath) &&
            downloadedPackage.EntryFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !candidates.Contains(downloadedPackage.EntryFilePath, StringComparer.Ordinal))
        {
            candidates.Add(downloadedPackage.EntryFilePath);
        }

        var preferredEntry = downloadedPackage.EntryFilePath ?? string.Empty;
        var orderedCandidates = candidates
            .OrderByDescending(path => ScoreWindowsInstallerCandidate(path, preferredEntry, preference))
            .ThenBy(path => path.Length)
            .ToList();
        var preferredArchitectureCandidates = orderedCandidates
            .Where(path => MatchesWindowsInstallerPreference(path, preference))
            .ToList();
        if (preferredArchitectureCandidates.Count == 0)
            return orderedCandidates;

        return preferredArchitectureCandidates
            .Concat(orderedCandidates.Where(path =>
                !preferredArchitectureCandidates.Contains(path, StringComparer.Ordinal)))
            .ToList();
    }

    internal static string ToWineWindowsAbsolutePath(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
            return @"Z:\";

        var windowsSlashes = Path.GetFullPath(hostPath)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '\\');
        if (windowsSlashes.Length >= 2 && windowsSlashes[1] == ':')
            return windowsSlashes;
        if (windowsSlashes.StartsWith('\\'))
            return $"Z:{windowsSlashes}";

        return $@"Z:\{windowsSlashes.TrimStart('\\')}";
    }

    internal PrefixResolution ResolveWindowsPrefix(string gameId, string itemTitle, string? prefixPath)
    {
        string absolutePrefixPath;
        if (!string.IsNullOrWhiteSpace(prefixPath))
        {
            absolutePrefixPath = PrefixPathHelper.ResolveAbsolutePrefixPath(prefixPath, _libraryRoot);
        }
        else
        {
            var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(itemTitle);
            var folderName = string.IsNullOrWhiteSpace(safeTitle)
                ? $"gog_{gameId}"
                : $"gog_{gameId}_{safeTitle}";
            absolutePrefixPath = Path.Combine(_libraryRoot, "Prefixes", folderName);
        }

        return WinePrefixService.ResolveManaged(absolutePrefixPath, WinePrefixRuntime.Wine, _libraryRoot);
    }

    private static IReadOnlyList<WindowsInstallerArgumentProfile> BuildWindowsInstallerArgumentProfiles(
        bool createDesktopShortcut,
        bool createStartMenuShortcuts)
    {
        var shortcutArguments = BuildWindowsShortcutArguments(createDesktopShortcut, createStartMenuShortcuts);
        return
        [
            new(
                "inno-silent-dir-argument",
                "/DIR=",
                ["/SP-", "/SILENT", "/NOGUI", "/SUPPRESSMSGBOXES", "/NORESTART", .. shortcutArguments]),
            new("inno-interactive-dir-argument", "/DIR=", ["/SP-", .. shortcutArguments])
        ];
    }

    private static int ScoreWindowsInstallerCandidate(
        string path,
        string preferredEntryPath,
        GogWindowsInstallerPreference preference)
    {
        if (string.IsNullOrWhiteSpace(path))
            return int.MinValue;

        var score = 0;
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        if (path.Equals(preferredEntryPath, StringComparison.Ordinal))
            score += 1;
        if (fileName.Contains("setup", StringComparison.Ordinal))
            score += 4;

        var has64Token = fileName.Contains("x64", StringComparison.Ordinal) ||
                         fileName.Contains("64", StringComparison.Ordinal);
        var has32Token = fileName.Contains("x86", StringComparison.Ordinal) ||
                         fileName.Contains("32", StringComparison.Ordinal);
        if (has64Token)
            score += preference == GogWindowsInstallerPreference.Prefer32 ? -3 : 3;
        if (has32Token)
            score += preference == GogWindowsInstallerPreference.Prefer32 ? 3 : -2;
        return score;
    }

    private static bool MatchesWindowsInstallerPreference(
        string path,
        GogWindowsInstallerPreference preference)
    {
        var architecture = DetectWindowsInstallerArchitecture(path);
        if (architecture == WindowsInstallerArchitecture.Unknown)
            return false;

        return preference == GogWindowsInstallerPreference.Prefer32
            ? architecture == WindowsInstallerArchitecture.X86
            : architecture == WindowsInstallerArchitecture.X64;
    }

    private static WindowsInstallerArchitecture DetectWindowsInstallerArchitecture(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return WindowsInstallerArchitecture.Unknown;

        var tokens = Path.GetFileNameWithoutExtension(fileName)
            .ToLowerInvariant()
            .Split(['_', '-', '.', ' ', '(', ')', '[', ']', '{', '}', '+'], StringSplitOptions.RemoveEmptyEntries);
        var has64 = tokens.Any(token =>
            token is "x64" or "64" or "64bit" or "win64" or "amd64" ||
            token.EndsWith("x64", StringComparison.Ordinal) ||
            token.EndsWith("64bit", StringComparison.Ordinal));
        var has32 = tokens.Any(token =>
            token is "x86" or "32" or "32bit" or "win32" or "i386" ||
            token.EndsWith("x86", StringComparison.Ordinal) ||
            token.EndsWith("32bit", StringComparison.Ordinal));
        if (has64 && !has32)
            return WindowsInstallerArchitecture.X64;
        if (has32 && !has64)
            return WindowsInstallerArchitecture.X86;
        return WindowsInstallerArchitecture.Unknown;
    }

    private static string? BuildWindowsInstallerDirectoryArgument(string targetInstallPath, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        var value = targetInstallPath?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(value) ? prefix : prefix + value;
    }

    private static string BuildInnoSetupLogArgument(string hostLogPath)
        => string.IsNullOrWhiteSpace(hostLogPath)
            ? "/LOG"
            : "/LOG=" + ToWineWindowsAbsolutePath(hostLogPath);

    private sealed record WindowsInstallerArgumentProfile(
        string Name,
        string? DirectoryArgumentPrefix,
        IReadOnlyList<string>? AdditionalArguments = null);

    private enum WindowsInstallerArchitecture
    {
        Unknown = 0,
        X64 = 1,
        X86 = 2
    }
}
