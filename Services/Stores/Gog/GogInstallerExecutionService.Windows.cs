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
    bool CreateStartMenuShortcuts);

public sealed partial class GogInstallerExecutionService
{
    internal async Task<GogInstallerExecutionResult> RunWindowsAsync(
        GogWindowsInstallerExecutionRequest request,
        Action<string> appendLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appendLog);

        GogInstallerExecutionResult Fail(string message) => new(false, message);

        if (string.IsNullOrWhiteSpace(request.WinePath))
            return Fail("Windows installation requires a system wine version.");

        Directory.CreateDirectory(request.InstallPath);
        Directory.CreateDirectory(request.DownloadedPackage.StagingDirectory);

        var prefixRoot = ResolveOrCreatePrefixRoot(request);
        var prefixDrivePath = Path.Combine(prefixRoot, "drive_c");
        var windowsInstallDestinationPath = ToWineWindowsAbsolutePath(request.InstallPath);

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
        var payloadBaseline = GogInstallPayloadTracker.Capture(request.InstallPath);
        var prefixPayloadBaseline = GogInstallPayloadTracker.Capture(prefixDrivePath);
        var profiles = BuildWindowsInstallerArgumentProfiles(
            request.CreateDesktopShortcut,
            request.CreateStartMenuShortcuts);
        GogInstallerProcessResult? lastExecution = null;

        for (var attemptIndex = 0; attemptIndex < profiles.Count; attemptIndex++)
        {
            var profile = profiles[attemptIndex];
            var startInfo = EmulatorResolverHelper.BuildWineInstallStartInfo(request.WinePath, prefixRoot);
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

            var innoLogHostPath = attemptIndex == 0
                ? Path.Combine(request.DownloadedPackage.StagingDirectory, "inno-setup.log")
                : Path.Combine(
                    request.DownloadedPackage.StagingDirectory,
                    $"inno-setup-{profile.Name}.log");
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

            lastExecution = execution;
            appendLog(
                $"[Windows installer] Process outcome: exit={execution.ExitCode}, " +
                $"durationMs={execution.DurationMs}, runtimeCrash={execution.HasRuntimeCrashError}, " +
                $"unsupportedFlags={execution.HasUnsupportedFlagsError}, " +
                $"shellParse={execution.HasShellParsingError}, " +
                $"terminalSpawn={execution.HasTerminalSpawnError}");

            var payloadDetected = await GogInstallPayloadTracker.WaitForChangeAsync(
                request.InstallPath,
                payloadBaseline,
                TimeSpan.FromSeconds(25),
                cancellationToken).ConfigureAwait(false);
            if (payloadDetected)
            {
                if (attemptIndex > 0)
                    appendLog("[Windows installer] Interactive fallback succeeded; install payload detected.");

                return new GogInstallerExecutionResult(true);
            }

            if (attemptIndex < profiles.Count - 1)
            {
                appendLog(
                    "[Windows installer] Silent attempt failed or produced no payload. " +
                    "Starting interactive fallback with prefilled target directory.");
            }
        }

        if (lastExecution is { ExitCode: not 0 })
        {
            appendLog($"[Windows installer] Warning: Exit code {lastExecution.ExitCode}. Payload unchanged.");
            return Fail($"Windows installer exited with code {lastExecution.ExitCode} and did not place files.");
        }

        if (GogInstallPayloadTracker.HasChanged(prefixDrivePath, prefixPayloadBaseline))
        {
            appendLog(
                $"Installer changed files inside prefix drive_c ({prefixDrivePath}), " +
                "but target path stayed unchanged. /DIR may have been ignored.");
        }

        return Fail("Windows installer did not complete successfully.");
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

    private string ResolveOrCreatePrefixRoot(GogWindowsInstallerExecutionRequest request)
    {
        string absolutePrefixPath;
        if (!string.IsNullOrWhiteSpace(request.PrefixPath))
        {
            absolutePrefixPath = PrefixPathHelper.ResolveAbsolutePrefixPath(request.PrefixPath, _libraryRoot);
        }
        else
        {
            var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(request.ItemTitle);
            var folderName = string.IsNullOrWhiteSpace(safeTitle)
                ? $"gog_{request.StoreGameId}"
                : $"gog_{request.StoreGameId}_{safeTitle}";
            absolutePrefixPath = Path.Combine(_libraryRoot, "Prefixes", folderName);
        }

        Directory.CreateDirectory(absolutePrefixPath);
        return absolutePrefixPath;
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
