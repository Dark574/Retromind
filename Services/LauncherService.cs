using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Executes media items (native, emulator-based, or command/url).
/// Also tracks playtime and can configure Wine prefixes for non-native launches on Linux.
/// </summary>
public sealed class LauncherService
{
    private const int MinPlayTimeSeconds = 5;
    private const int EarlyFailureThresholdSeconds = 10;
    private readonly string _libraryRootPath;
    private readonly AppSettings _settings;
    private readonly LaunchLogService _launchLogService;
    private readonly LaunchPlaylistService _launchPlaylistService;
    private readonly LaunchEnvironmentService _launchEnvironmentService;
    private readonly WinePrefixService _winePrefixService;
    private readonly LaunchProcessService _launchProcessService;
    private readonly ProtonPrefixRelocationService _protonPrefixRelocationService;

    public LauncherService(
        string libraryRootPath,
        AppSettings settings,
        LaunchLogService launchLogService,
        LaunchPlaylistService? launchPlaylistService = null,
        LaunchEnvironmentService? launchEnvironmentService = null,
        WinePrefixService? winePrefixService = null,
        LaunchProcessService? launchProcessService = null,
        ProtonPrefixRelocationService? protonPrefixRelocationService = null)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launchLogService = launchLogService ?? throw new ArgumentNullException(nameof(launchLogService));
        _launchPlaylistService = launchPlaylistService ?? new LaunchPlaylistService(libraryRootPath);
        _launchEnvironmentService = launchEnvironmentService ?? new LaunchEnvironmentService();
        _winePrefixService = winePrefixService ?? new WinePrefixService(libraryRootPath, settings);
        _launchProcessService = launchProcessService ?? new LaunchProcessService();
        _protonPrefixRelocationService = protonPrefixRelocationService ??
                                         new ProtonPrefixRelocationService(libraryRootPath, settings);
    }

    public async Task<LaunchResult> LaunchAsync(
        MediaItem item,
        EmulatorConfig? inheritedConfig = null,
        List<string>? nodePath = null,
        IReadOnlyList<LaunchWrapper>? nativeWrappers = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        bool usePlaylistForMultiDisc = false,
        bool recordStatistics = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var launchLog = new LaunchLogBuilder(
            item,
            inheritedConfig,
            _settings,
            nativeWrappers,
            recordStatistics);
        Process? process = null;
        LaunchProcessOutputCapture? outputCapture = null;
        Task initialLogWriteTask = Task.CompletedTask;
        var stopwatch = Stopwatch.StartNew();
        var shouldRecordSession = false;
        var watchedProcessName = string.IsNullOrWhiteSpace(item.OverrideWatchProcess)
            ? null
            : item.OverrideWatchProcess;
        var isDelegatedCommand = watchedProcessName == null && IsDelegatedCommand(item);
        string? missingWatchedProcessName = null;
        var watchedProcessWasAlreadyRunning = false;
        int? exitCode = null;
        string? consoleOutput = null;
        TimeSpan? processStartedAt = null;
        var elapsed = TimeSpan.Zero;

        if (watchedProcessName != null)
        {
            try
            {
                watchedProcessWasAlreadyRunning = _launchProcessService.IsProcessRunning(watchedProcessName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Launcher] Could not inspect watched process before launch: {ex.Message}");
            }
        }

        try
        {
            process = item.MediaType == MediaType.Command
                ? LaunchCommand(item, environmentOverrides, launchLog)
                : LaunchNativeOrEmulator(
                    item,
                    inheritedConfig,
                    nodePath,
                    nativeWrappers,
                    usePlaylistForMultiDisc,
                    environmentOverrides,
                    launchLog);
            processStartedAt = stopwatch.Elapsed;
            outputCapture = _launchProcessService.StartOutputCapture(process);
            initialLogWriteTask = _launchLogService.TryWriteAsync(
                item.Id,
                launchLog.Build("Process started; session still running", stopwatch.Elapsed));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            process?.Dispose();
            Debug.WriteLine($"[Launcher] Failed to launch: {ex.Message}");
            var failedResult = LaunchResult.Failed(ex.Message);
            await _launchLogService.TryWriteAsync(
                    item.Id,
                    launchLog.Build(
                        failedResult.Outcome.ToString(),
                        stopwatch.Elapsed,
                        errorMessage: ex.Message))
                .ConfigureAwait(false);
            return failedResult;
        }

        try
        {
            // Tracking strategy:
            // A) If OverrideWatchProcess is set, we track by process name (for launchers like Steam).
            // B) Store/URI commands without a watched process are only handoffs.
            // C) Otherwise, if we have a process handle, wait for it.
            if (watchedProcessName != null)
            {
                var watchOutcome = await _launchProcessService.WatchProcessByNameAsync(
                        watchedProcessName,
                        watchedProcessWasAlreadyRunning,
                        cancellationToken)
                    .ConfigureAwait(false);
                shouldRecordSession = watchOutcome == ProcessWatchOutcome.Tracked;
                if (watchOutcome == ProcessWatchOutcome.NotFound)
                    missingWatchedProcessName = watchedProcessName;
            }
            else if (isDelegatedCommand && process != null)
            {
                // The Steam/Heroic/xdg-open process is not the game. Observe it
                // briefly for an immediate error, but never use its lifetime as
                // game playtime or as a game-exit boundary.
                if (await _launchProcessService.WaitForDelegatedCommandAsync(process, cancellationToken)
                        .ConfigureAwait(false))
                    exitCode = process.ExitCode;
            }
            else if (process is { HasExited: false })
            {
                shouldRecordSession = true;
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            else if (process != null)
            {
                // Process started but already exited (very fast failure or immediate exit).
                // Still count as a launch attempt.
                shouldRecordSession = true;
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
        }
        catch (OperationCanceledException)
        {
            // App shutdown or caller cancellation: treat as "no/partial session".
        }
        catch (Exception ex)
        {
            // The process was already started. A tracking failure must not be reported as a launch failure.
            Debug.WriteLine($"[Launcher] Error during session tracking: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            elapsed = stopwatch.Elapsed;

            if (outputCapture != null)
            {
                try
                {
                    if (process is { HasExited: true })
                    {
                        await outputCapture.WaitForCompletionAsync(TimeSpan.FromMilliseconds(250))
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Launcher] Could not finish reading process output: {ex.Message}");
                }
            }

            consoleOutput = outputCapture?.GetOutput();
            outputCapture?.Dispose();
            process?.Dispose();
        }

        var delegatedCommandSucceeded = isDelegatedCommand && exitCode is null or 0;
        if (recordStatistics && (shouldRecordSession || delegatedCommandSucceeded))
        {
            try
            {
                if (shouldRecordSession)
                    await EvaluateSessionAsync(item, elapsed).ConfigureAwait(false);
                else
                    await RecordLaunchAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Launcher] Failed to record session statistics: {ex.Message}");
            }
        }

        LaunchResult result;
        if (!string.IsNullOrWhiteSpace(missingWatchedProcessName))
        {
            result = LaunchResult.WatchedProcessNotFound(missingWatchedProcessName, consoleOutput);
        }
        else if (exitCode is not null and not 0 &&
                 processStartedAt is { } startedAt &&
                 elapsed - startedAt <= TimeSpan.FromSeconds(EarlyFailureThresholdSeconds))
        {
            result = LaunchResult.ExitedEarly(
                exitCode.Value,
                consoleOutput,
                wasSessionTracked: shouldRecordSession);
        }
        else
        {
            result = shouldRecordSession
                ? LaunchResult.TrackedSessionCompleted
                : LaunchResult.Started;
        }

        await initialLogWriteTask.ConfigureAwait(false);
        await _launchLogService.TryWriteAsync(
                item.Id,
                launchLog.Build(
                    DescribeLaunchOutcome(result),
                    elapsed,
                    exitCode,
                    consoleOutput: consoleOutput))
            .ConfigureAwait(false);
        return result;
    }

    private Process? LaunchCommand(
        MediaItem item,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        LaunchLogBuilder launchLog)
    {
        // Command media: can be either
        // A) a URL/protocol (steam://, heroic://, https://, …) -> open via xdg-open on Linux
        // B) an executable command with arguments
        var target = item.GetPrimaryLaunchPath();

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("The command has no launch target.");

        // Linux-first: prefer xdg-open for URI/protocol
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && LooksLikeUriOrProtocol(target))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false
            };

            // xdg-open expects the URI as a single argument
            psi.ArgumentList.Add(target);
            _launchEnvironmentService.PrepareHostCommand(psi, environmentOverrides);
            launchLog.CaptureProcessStart(psi, target, environmentOverrides);

            return _launchProcessService.Start(psi);
        }

        // Otherwise treat as executable command
        var hasEnvOverrides = environmentOverrides is { Count: > 0 };
        var forceDirectExec = hasEnvOverrides || _launchEnvironmentService.IsRunningInsideAppImageRuntime;
        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = item.LauncherArgs ?? string.Empty,
            // In AppImage mode we force direct exec so runtime env sanitization can always apply.
            UseShellExecute = !forceDirectExec
        };
        startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, target, launchFilePath: null);
        _launchEnvironmentService.PrepareCommand(startInfo, environmentOverrides);
        launchLog.CaptureProcessStart(startInfo, target, environmentOverrides);
        return _launchProcessService.Start(startInfo);
    }

    internal static bool IsDelegatedCommand(MediaItem item)
    {
        if (item.MediaType != MediaType.Command)
            return false;

        var target = item.GetPrimaryLaunchPath();
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (LooksLikeUriOrProtocol(target) || IsStoreCommandToken(target))
            return true;

        var executable = Path.GetFileName(target.Trim('"', '\''));
        if (string.Equals(executable, "xdg-open", StringComparison.OrdinalIgnoreCase))
            return LooksLikeUriOrProtocol(item.LauncherArgs ?? string.Empty);

        if (!string.Equals(executable, "env", StringComparison.OrdinalIgnoreCase))
            return false;

        var commandToken = TryGetFirstExecutableTokenFromEnvArgs(item.LauncherArgs);
        return !string.IsNullOrWhiteSpace(commandToken) && IsStoreCommandToken(commandToken);
    }

    private Process? LaunchNativeOrEmulator(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        List<string>? nodePath,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        bool usePlaylistForMultiDisc,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        LaunchLogBuilder launchLog)
    {
        var launchFilePath = _launchPlaylistService.ResolveLaunchFilePath(
            item,
            nodePath,
            usePlaylistForMultiDisc);
        var (fileName, args, useShellExecute) =
            LaunchPlanBuilder.Build(item, inheritedConfig, nativeWrappers, launchFilePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName
        };

        startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, fileName, launchFilePath);

        var hasEnvOverrides =
            (environmentOverrides?.Count ?? 0) > 0 ||
            ((environmentOverrides == null) &&
             ((inheritedConfig?.EnvironmentOverrides?.Count ?? 0) > 0 ||
              (item.EnvironmentOverrides?.Count ?? 0) > 0));
        var isUmuLaunch = IsUmuBased(item, inheritedConfig, nativeWrappers, environmentOverrides);
        var isProtonLaunch = isUmuLaunch || IsProtonBased(item, inheritedConfig, nativeWrappers, environmentOverrides);

        // Prefix management rules:
        // - explicit item PrefixPath -> always apply
        // - emulator profile with UsesWinePrefix=true -> apply
        var shouldApplyPrefix =
            !string.IsNullOrWhiteSpace(item.PrefixPath) ||
            (item.MediaType == MediaType.Emulator && inheritedConfig?.UsesWinePrefix == true);
        var isAppImageRuntime = _launchEnvironmentService.IsRunningInsideAppImageRuntime;

        // Ensure env vars + wrapper arguments are honored (shell exec can drop env vars).
        var requiresDirectExec = isAppImageRuntime ||
                                 shouldApplyPrefix ||
                                 hasEnvOverrides ||
                                 (nativeWrappers is { Count: > 0 }) ||
                                 !string.IsNullOrWhiteSpace(args);

        startInfo.UseShellExecute = requiresDirectExec ? false : useShellExecute;

        if (shouldApplyPrefix)
        {
            var prefixRuntime = isUmuLaunch
                ? WinePrefixRuntime.Umu
                : isProtonLaunch
                    ? WinePrefixRuntime.Proton
                    : WinePrefixRuntime.Wine;
            _winePrefixService.Prepare(item, startInfo, prefixRuntime);
        }
            
        _launchEnvironmentService.PrepareNativeOrEmulator(
            startInfo,
            item,
            inheritedConfig,
            environmentOverrides);

        if (shouldApplyPrefix && isProtonLaunch)
        {
            var effectiveProtonPath = startInfo.EnvironmentVariables.ContainsKey("PROTONPATH")
                ? startInfo.EnvironmentVariables["PROTONPATH"]
                : null;
            var repair = _protonPrefixRelocationService.Repair(item, effectiveProtonPath);
            if (repair.RepairedLinks > 0)
            {
                Debug.WriteLine(
                    $"[Launcher] Repaired {repair.RepairedLinks} relocated Proton prefix link(s) for '{item.Title}'.");
            }

            if (repair.UnresolvedLinks > 0 || repair.FailedLinks > 0)
            {
                Debug.WriteLine(
                    $"[Launcher] Proton prefix repair incomplete for '{item.Title}': " +
                    $"unresolved={repair.UnresolvedLinks}, failed={repair.FailedLinks}.");
            }
        }

        startInfo.Arguments = args ?? string.Empty;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            item.MediaType == MediaType.Native &&
            !string.IsNullOrWhiteSpace(launchFilePath))
        {
            LinuxFileSystemHelper.EnsureExecutableBitBestEffort(launchFilePath);
        }

        // DEBUG: log the exact command-line we are about to run
        Debug.WriteLine($"[Launcher] START: {startInfo.FileName} {startInfo.Arguments}");
        launchLog.CaptureProcessStart(startInfo, launchFilePath, environmentOverrides);

        return _launchProcessService.Start(startInfo);
    }

    private static string DescribeLaunchOutcome(LaunchResult result) => result.Outcome switch
    {
        LaunchOutcome.ExitedEarly => "Process started and exited early with an error",
        LaunchOutcome.WatchedProcessNotFound => "Launcher started, but the configured game process was not found",
        LaunchOutcome.StartFailed => "Process could not be started",
        _ when result.WasSessionTracked => "Tracked session completed",
        _ => "Launch handed off successfully"
    };

    private static string ResolveWorkingDirectory(string? overrideDirectory, string fileName, string? launchFilePath)
    {
        var overridePath = ResolveWorkingDirectoryOverride(overrideDirectory);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!Directory.Exists(overridePath))
                Debug.WriteLine($"[Launcher] Working directory not found: {overridePath}");

            return overridePath;
        }

        // Prefer the media file directory if we have one.
        if (!string.IsNullOrWhiteSpace(launchFilePath))
        {
            if (Directory.Exists(launchFilePath))
                return launchFilePath;

            if (File.Exists(launchFilePath))
                return Path.GetDirectoryName(launchFilePath) ?? string.Empty;
        }

        // Fall back to the launcher/executable directory if it's a real path.
        if (Path.IsPathRooted(fileName))
        {
            if (Directory.Exists(fileName))
                return fileName;

            if (File.Exists(fileName))
                return Path.GetDirectoryName(fileName) ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? ResolveWorkingDirectoryOverride(string? overrideDirectory)
    {
        if (string.IsNullOrWhiteSpace(overrideDirectory))
            return null;

        var trimmed = overrideDirectory.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        return AppPaths.ResolveDataPath(trimmed);
    }

    private static bool LooksLikeUriOrProtocol(string value)
    {
        // Cheap heuristics (no heavy Uri parsing needed):
        // - contains "://": http://, https://, steam://, heroic://, …
        // - or "scheme:" (steam:, magnet:, etc.)
        if (value.Contains("://", StringComparison.Ordinal))
            return true;

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return false;

        // Avoid treating "C:\..." (Windows paths) as protocol.
        // (Windows is not the focus, but this keeps behavior sane.)
        if (colon == 1 && char.IsLetter(value[0]))
            return false;

        return true;
    }

    private static bool IsStoreCommandToken(string token)
    {
        var executable = Path.GetFileName(token.Trim('"', '\''));
        return string.Equals(executable, "steam", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(executable, "heroic", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetFirstExecutableTokenFromEnvArgs(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        var args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var skipNext = false;
        foreach (var arg in args)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (string.Equals(arg, "--", StringComparison.Ordinal))
                continue;

            // Common env options that consume the next token.
            if (arg is "-u" or "--unset" or "-C" or "--chdir" or "-S" or "--split-string")
            {
                skipNext = true;
                continue;
            }

            // Long options with inline value.
            if (arg.StartsWith("--unset=", StringComparison.Ordinal) ||
                arg.StartsWith("--chdir=", StringComparison.Ordinal) ||
                arg.StartsWith("--split-string=", StringComparison.Ordinal))
            {
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                continue;

            return arg;
        }

        return null;
    }

    private static bool IsProtonBased(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (environmentOverrides is { Count: > 0 } && LaunchRuntimeHelper.ContainsProtonHints(environmentOverrides))
            return true;

        if (environmentOverrides == null &&
            (LaunchRuntimeHelper.ContainsProtonHints(inheritedConfig?.EnvironmentOverrides) ||
             LaunchRuntimeHelper.ContainsProtonHints(item.EnvironmentOverrides)))
        {
            return true;
        }

        if (LaunchRuntimeHelper.ContainsProtonToken(item.LauncherPath) ||
            LaunchRuntimeHelper.ContainsProtonToken(inheritedConfig?.Path))
        {
            return true;
        }

        return nativeWrappers != null &&
               nativeWrappers.Any(w => LaunchRuntimeHelper.ContainsProtonToken(w.Path));
    }

    private static bool IsUmuBased(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (environmentOverrides is { Count: > 0 } && LaunchRuntimeHelper.ContainsUmuHints(environmentOverrides))
            return true;

        if (environmentOverrides == null &&
            (LaunchRuntimeHelper.ContainsUmuHints(inheritedConfig?.EnvironmentOverrides) ||
             LaunchRuntimeHelper.ContainsUmuHints(item.EnvironmentOverrides)))
        {
            return true;
        }

        if (LaunchRuntimeHelper.ContainsUmuToken(item.LauncherPath) ||
            LaunchRuntimeHelper.ContainsUmuToken(inheritedConfig?.Path))
        {
            return true;
        }

        return nativeWrappers != null &&
               nativeWrappers.Any(w => LaunchRuntimeHelper.ContainsUmuToken(w.Path));
    }

    private static async Task EvaluateSessionAsync(MediaItem item, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;

        // if there is no time recorded, no need to record something
        if (seconds <= 0)
            return;

        // Above the minimum threshold, we add the measured time.
        // Below it, we only record that the item was started (PlayCount/LastPlayed),
        // but we do not add to TotalPlayTime.
        var effectiveSessionTime = seconds > MinPlayTimeSeconds
            ? elapsed
            : TimeSpan.Zero;

        await UiThreadHelper.InvokeAsync(() => UpdateStats(item, effectiveSessionTime))
            .ConfigureAwait(false);
    }

    private static Task RecordLaunchAsync(MediaItem item) =>
        UiThreadHelper.InvokeAsync(() => UpdateStats(item, TimeSpan.Zero));

    private static void UpdateStats(MediaItem item, TimeSpan sessionTime)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
        item.TotalPlayTime += sessionTime;
    }

}
