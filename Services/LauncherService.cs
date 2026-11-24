using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Gog.Comet;
using Retromind.Services.Stores.Gog.Playtime;

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
    private readonly SteamProcessTrackingService _steamProcessTrackingService;
    private readonly IGogCometSessionService? _gogCometSessionService;
    private readonly IGogPlaytimeSyncService? _gogPlaytimeSyncService;
    private readonly object _gogCometCompletionLock = new();
    private readonly object _gogLaunchCompletionLock = new();
    private readonly HashSet<TaskCompletionSource> _activeGogLaunchCompletions = [];
    private readonly IErrorService? _errorService;
    private PendingGogCometCompletion? _pendingGogCometCompletion;
    private int _shutdownRequested;

    public LauncherService(
        string libraryRootPath,
        AppSettings settings,
        LaunchLogService launchLogService,
        LaunchPlaylistService? launchPlaylistService = null,
        LaunchEnvironmentService? launchEnvironmentService = null,
        WinePrefixService? winePrefixService = null,
        LaunchProcessService? launchProcessService = null,
        ProtonPrefixRelocationService? protonPrefixRelocationService = null,
        SteamProcessTrackingService? steamProcessTrackingService = null,
        IErrorService? errorService = null)
        : this(
            libraryRootPath,
            settings,
            launchLogService,
            launchPlaylistService ?? new LaunchPlaylistService(libraryRootPath,
                new ErrorService(Path.Combine(AppPaths.DataRoot, "Logs", "Exceptions"))),
            launchEnvironmentService ?? new LaunchEnvironmentService(),
            winePrefixService ?? new WinePrefixService(libraryRootPath, settings),
            launchProcessService ?? new LaunchProcessService(),
            protonPrefixRelocationService ?? new ProtonPrefixRelocationService(libraryRootPath, settings),
            steamProcessTrackingService ?? new SteamProcessTrackingService(),
            gogCometSessionService: null,
            gogPlaytimeSyncService: null,
            errorService: errorService)
    {
    }

    internal LauncherService(
        string libraryRootPath,
        AppSettings settings,
        LaunchLogService launchLogService,
        LaunchPlaylistService launchPlaylistService,
        LaunchEnvironmentService launchEnvironmentService,
        WinePrefixService winePrefixService,
        LaunchProcessService launchProcessService,
        ProtonPrefixRelocationService protonPrefixRelocationService,
        SteamProcessTrackingService steamProcessTrackingService,
        IGogCometSessionService? gogCometSessionService,
        IGogPlaytimeSyncService? gogPlaytimeSyncService,
        IErrorService? errorService = null)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launchLogService = launchLogService ?? throw new ArgumentNullException(nameof(launchLogService));
        _launchPlaylistService = launchPlaylistService ?? throw new ArgumentNullException(nameof(launchPlaylistService));
        _launchEnvironmentService = launchEnvironmentService ?? throw new ArgumentNullException(nameof(launchEnvironmentService));
        _winePrefixService = winePrefixService ?? throw new ArgumentNullException(nameof(winePrefixService));
        _launchProcessService = launchProcessService ?? throw new ArgumentNullException(nameof(launchProcessService));
        _protonPrefixRelocationService = protonPrefixRelocationService ??
                                         throw new ArgumentNullException(nameof(protonPrefixRelocationService));
        _steamProcessTrackingService = steamProcessTrackingService ??
                                       throw new ArgumentNullException(nameof(steamProcessTrackingService));
        _gogCometSessionService = gogCometSessionService;
        _gogPlaytimeSyncService = gogPlaytimeSyncService;
        _errorService = errorService;
    }

    internal Task<LaunchSessionStopRequestResult> RequestActiveSessionStopAsync(
        CancellationToken cancellationToken = default) =>
        _launchProcessService.RequestActiveSessionStopAsync(cancellationToken);

    public async Task<LaunchResult> LaunchAsync(
        MediaItem item,
        EmulatorConfig? inheritedConfig = null,
        List<string>? nodePath = null,
        IReadOnlyList<LaunchWrapper>? nativeWrappers = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        IReadOnlyList<string>? additionalLaunchArguments = null,
        bool usePlaylistForMultiDisc = false,
        bool recordStatistics = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ParentalControlHelper.IsPlaybackBlocked(_settings, item))
            return ParentalBlockResult();

        TaskCompletionSource? launchCompletion = null;
        if (_settings.EnableGogGalaxyIntegration && GogCometSessionService.IsEligible(item))
        {
            launchCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gogLaunchCompletionLock)
                _activeGogLaunchCompletions.Add(launchCompletion);
        }

        try
        {
            return await LaunchCoreAsync(
                    item,
                    inheritedConfig,
                    nodePath,
                    nativeWrappers,
                    environmentOverrides,
                    additionalLaunchArguments,
                    usePlaylistForMultiDisc,
                    recordStatistics,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (launchCompletion != null)
            {
                launchCompletion.TrySetResult();
                lock (_gogLaunchCompletionLock)
                    _activeGogLaunchCompletions.Remove(launchCompletion);
            }
        }
    }

    private async Task<LaunchResult> LaunchCoreAsync(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        List<string>? nodePath,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        IReadOnlyList<string>? additionalLaunchArguments,
        bool usePlaylistForMultiDisc,
        bool recordStatistics,
        CancellationToken cancellationToken)
    {

        var launchLog = new LaunchLogBuilder(
            item,
            inheritedConfig,
            _settings,
            nativeWrappers,
            recordStatistics);
        Process? process = null;
        IGogCometGameSession? gogCometSession = null;
        ActiveLaunchSessionRegistration? activeSession = null;
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
        var steamProcessTrackingSession = watchedProcessName == null &&
                                          isDelegatedCommand &&
                                          SteamAppIdResolver.TryGetAppId(item, out var steamAppId)
            ? _steamProcessTrackingService.Prepare(steamAppId, item.Title)
            : null;
        int? exitCode = null;
        string? consoleOutput = null;
        TimeSpan? processStartedAt = null;
        DateTimeOffset? sessionStartedAtUtc = null;
        TimeSpan? trackedSessionDuration = null;
        var elapsed = TimeSpan.Zero;
        var wasStoppedByUser = false;
        var trackingWasCancelled = false;

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

        var shouldStartGogComet = _settings.EnableGogGalaxyIntegration &&
                                  !isDelegatedCommand &&
                                  !watchedProcessWasAlreadyRunning &&
                                  _gogCometSessionService != null &&
                                  GogCometSessionService.IsEligible(item);

        try
        {
            if (shouldStartGogComet)
            {
                // A previous sidecar may still be using Comet's fixed port while
                // completing post-game synchronization in the background.
                await StopPendingGogCometCompletionAsync().ConfigureAwait(false);
                try
                {
                    gogCometSession = await _gogCometSessionService!
                        .TryStartAsync(item, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Comet is an optional sidecar. A missing SDK service or a
                    // transient authentication failure must not block the game.
                    Debug.WriteLine($"[Comet] Optional startup failed: {ex.Message}");
                }
            }

            if (ParentalControlHelper.IsPlaybackBlocked(_settings, item))
            {
                await FinishGogCometSessionAsync(gogCometSession, allowGracefulCompletion: false).ConfigureAwait(false);
                return ParentalBlockResult();
            }

            process = item.MediaType == MediaType.Command
                ? LaunchCommand(item, environmentOverrides, launchLog)
                : LaunchNativeOrEmulator(
                    item,
                    inheritedConfig,
                    nodePath,
                    nativeWrappers,
                    usePlaylistForMultiDisc,
                    environmentOverrides,
                    additionalLaunchArguments,
                    launchLog);
            processStartedAt = stopwatch.Elapsed;
            sessionStartedAtUtc = DateTimeOffset.UtcNow;

            if (watchedProcessName == null &&
                !isDelegatedCommand &&
                process is { HasExited: false })
            {
                activeSession = _launchProcessService.BeginDirectSession(process);
            }

            outputCapture = _launchProcessService.StartOutputCapture(process);
            initialLogWriteTask = _launchLogService.TryWriteAsync(
                item.Id,
                launchLog.Build("Process started; session still running", stopwatch.Elapsed));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (activeSession is { } registration)
                _launchProcessService.EndActiveSession(registration);
            await FinishGogCometSessionAsync(gogCometSession, allowGracefulCompletion: false)
                .ConfigureAwait(false);
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

        var trackingMode = "Unknown";
        var trackingPhase = "Unknown";

        try
        {
            // Tracking strategy:
            // A) If OverrideWatchProcess is set, we track by process name (for launchers like Steam).
            // B) Imported Steam commands are tracked by their exact AppID on Linux.
            // C) Other store/URI commands without a watched process are only handoffs.
            // D) Otherwise, if we have a process handle, wait for it.
            if (watchedProcessName != null)
            {
                trackingMode = "ProcessName";
                trackingPhase = "WatchProcessByName";

                var watchOutcome = await _launchProcessService.WatchProcessByNameAsync(
                        watchedProcessName,
                        watchedProcessWasAlreadyRunning,
                        cancellationToken,
                        onProcessesDetected: processIds =>
                        {
                            if (activeSession is { } registration)
                            {
                                _launchProcessService.UpdateWatchedSession(registration, processIds);
                            }
                            else
                            {
                                activeSession = _launchProcessService.BeginWatchedSession(
                                    watchedProcessName,
                                    processIds);
                            }
                        })
                    .ConfigureAwait(false);
                shouldRecordSession = watchOutcome == ProcessWatchOutcome.Tracked;
                if (watchOutcome == ProcessWatchOutcome.NotFound)
                    missingWatchedProcessName = watchedProcessName;
            }
            else if (steamProcessTrackingSession != null && process != null)
            {
                trackingMode = "SteamAppId";
                trackingPhase = "ObserveHandoff";

                // First inspect the Steam handoff itself for an immediate failure.
                // A successful handoff is then tracked by its exact Steam AppID.
                if (await _launchProcessService.WaitForDelegatedCommandAsync(process, cancellationToken)
                        .ConfigureAwait(false))
                {
                    exitCode = process.ExitCode;
                }

                if (exitCode is null or 0)
                {
                    trackingPhase = "TrackSteamAppId";

                    var trackingResult = await _steamProcessTrackingService.TrackAsync(
                            steamProcessTrackingSession,
                            onGameProcessesDetected: processIds =>
                            {
                                if (activeSession is { } registration)
                                {
                                    _launchProcessService.UpdateWatchedSession(registration, processIds);
                                }
                                else
                                {
                                    activeSession = _launchProcessService.BeginObservedSession(processIds);
                                }
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    shouldRecordSession = trackingResult.WasTracked;
                    if (trackingResult.WasTracked)
                        trackedSessionDuration = trackingResult.Duration;
                }
            }
            else if (isDelegatedCommand && process != null)
            {
                trackingMode = "DelegatedCommand";
                trackingPhase = "ObserveHandoff";

                // The Steam/Heroic/xdg-open process is not the game. Observe it
                // briefly for an immediate error, but never use its lifetime as
                // game playtime or as a game-exit boundary.
                if (await _launchProcessService.WaitForDelegatedCommandAsync(process, cancellationToken)
                        .ConfigureAwait(false))
                    exitCode = process.ExitCode;
            }
            else if (process is { HasExited: false })
            {
                trackingMode = "DirectProcess";
                trackingPhase = "WaitForExit";

                shouldRecordSession = true;
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            else if (process != null)
            {
                trackingMode = "DirectProcess";
                trackingPhase = "WaitForExit";

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
            shouldRecordSession = false;
            trackingWasCancelled = true;
        }
        catch (Exception ex)
        {
            // The process was already started. A tracking failure must not be reported as a launch failure.
            ReportSessionWarning(
                "Launcher.SessionTrackingFailed",
                "The game started, but its session could not be tracked reliably.",
                item.Id,
                trackingPhase,
                ex,
                trackingMode);
        }
        finally
        {
            stopwatch.Stop();
            elapsed = stopwatch.Elapsed;

            if (gogCometSession != null)
            {
                if (trackingWasCancelled)
                {
                    await FinishGogCometSessionAsync(
                            gogCometSession,
                            allowGracefulCompletion: false)
                        .ConfigureAwait(false);
                }
                else if (Volatile.Read(ref _shutdownRequested) == 0)
                {
                    ScheduleGogCometCompletion(gogCometSession);
                }
                else
                {
                    await FinishGogCometSessionAsync(
                            gogCometSession,
                            allowGracefulCompletion: false)
                        .ConfigureAwait(false);
                }
            }
            gogCometSession = null;

            if (activeSession is { } registration)
            {
                wasStoppedByUser = _launchProcessService.WasStopRequested(registration);
                _launchProcessService.EndActiveSession(registration);
            }

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

        var delegatedCommandSucceeded = !trackingWasCancelled &&
                                        isDelegatedCommand &&
                                        exitCode is null or 0;
        if (recordStatistics && (shouldRecordSession || delegatedCommandSucceeded))
        {
            try
            {
                if (shouldRecordSession)
                    await EvaluateSessionAsync(item, trackedSessionDuration ?? elapsed).ConfigureAwait(false);
                else
                    await RecordLaunchAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportSessionWarning(
                    "Launcher.StatisticsUpdateFailed",
                    "The game started, but its launch statistics could not be updated.",
                    item.Id,
                    shouldRecordSession ? "UpdateTrackedSession" : "RecordLaunch",
                    ex);
            }
        }

        if (sessionStartedAtUtc is { } startedAtUtc)
        {
            var measuredDuration = trackedSessionDuration ??
                                   (processStartedAt is { } startedAt
                                       ? elapsed - startedAt
                                       : elapsed);
            await TryReportGogPlaytimeSessionAsync(
                    item,
                    recordStatistics,
                    shouldRecordSession,
                    startedAtUtc,
                    measuredDuration)
                .ConfigureAwait(false);
        }

        LaunchResult result;
        if (!string.IsNullOrWhiteSpace(missingWatchedProcessName))
        {
            result = LaunchResult.WatchedProcessNotFound(missingWatchedProcessName, consoleOutput);
        }
        else if (shouldRecordSession && wasStoppedByUser)
        {
            result = LaunchResult.TrackedSessionStoppedByUser;
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

    private static LaunchResult ParentalBlockResult() => LaunchResult.Failed(
        Strings.ResourceManager.GetString("Launch.ParentalBlocked", Strings.Culture) ??
        "Parental control blocks this game from starting.");

    private void ReportSessionWarning(
        string code, string message, string itemId, string phase,
        Exception exception, string? trackingMode = null)
    {
        var context = new Dictionary<string, string>
        {
            ["ItemId"] = itemId,
            ["Phase"] = phase,
            ["ExceptionType"] = exception.GetType().Name
        };

        if (trackingMode is not null)
            context["TrackingMode"] = trackingMode;

        try
        {
            _errorService?.Report(new ErrorReport(
                code, ErrorSeverity.Warning, nameof(LauncherService), message,
                Context: context));
        }
        catch
        {
            // dont change the result when trying to log
        }
    }

    internal async Task TryReportGogPlaytimeSessionAsync(
        MediaItem item,
        bool recordStatistics,
        bool wasSessionTracked,
        DateTimeOffset startedAtUtc,
        TimeSpan duration)
    {
        if (!recordStatistics ||
            !wasSessionTracked ||
            !_settings.EnableGogGalaxyIntegration ||
            _gogPlaytimeSyncService == null)
        {
            return;
        }

        try
        {
            await _gogPlaytimeSyncService
                .EnqueueSessionAsync(item, startedAtUtc, duration)
                .ConfigureAwait(false);
            _gogPlaytimeSyncService.RequestPendingSync();
        }
        catch (Exception ex)
        {
            // Queueing remote playtime is best effort and must never change the launch outcome.
            Debug.WriteLine($"[GOG] Playtime session could not be queued: {ex.Message}");
        }
    }

    internal async Task StopPendingGogCometCompletionAsync()
    {
        await StopPendingGogCometCompletionAsync(TimeSpan.Zero).ConfigureAwait(false);
    }

    internal async Task PrepareForShutdownAsync(TimeSpan gracePeriod)
    {
        if (gracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(gracePeriod));

        Interlocked.Exchange(ref _shutdownRequested, 1);
        var stopwatch = Stopwatch.StartNew();
        Task[] activeLaunchCompletions;
        lock (_gogLaunchCompletionLock)
        {
            activeLaunchCompletions = _activeGogLaunchCompletions
                .Select(completion => completion.Task)
                .ToArray();
        }

        if (activeLaunchCompletions.Length > 0)
        {
            try
            {
                await Task.WhenAll(activeLaunchCompletions)
                    .WaitAsync(gracePeriod)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The game may still be running, or local post-launch work may
                // be unusually slow. Keep application shutdown bounded.
            }
        }

        var remainingGracePeriod = gracePeriod - stopwatch.Elapsed;
        if (remainingGracePeriod < TimeSpan.Zero)
            remainingGracePeriod = TimeSpan.Zero;

        await StopPendingGogCometCompletionAsync(remainingGracePeriod).ConfigureAwait(false);
    }

    private async Task StopPendingGogCometCompletionAsync(TimeSpan gracefulWait)
    {
        PendingGogCometCompletion? pendingCompletion;
        lock (_gogCometCompletionLock)
            pendingCompletion = _pendingGogCometCompletion;

        if (pendingCompletion == null)
            return;

        if (gracefulWait > TimeSpan.Zero && !pendingCompletion.Completion.IsCompleted)
        {
            try
            {
                await pendingCompletion.Completion
                    .WaitAsync(gracefulWait)
                    .ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                // The bounded graceful window expired. Cancel below so the
                // sidecar cannot outlive Retromind or retain its fixed port.
            }
        }

        lock (_gogCometCompletionLock)
        {
            if (ReferenceEquals(_pendingGogCometCompletion, pendingCompletion))
                pendingCompletion.Cancellation.Cancel();
        }

        await pendingCompletion.Completion.ConfigureAwait(false);
    }

    internal void CancelPendingGogCometCompletion()
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        lock (_gogCometCompletionLock)
            _pendingGogCometCompletion?.Cancellation.Cancel();
    }

    internal void ResumeAfterCancelledShutdown() =>
        Interlocked.Exchange(ref _shutdownRequested, 0);

    private void ScheduleGogCometCompletion(IGogCometGameSession session)
    {
        var cancellation = new CancellationTokenSource();
        var completion = FinishGogCometSessionAsync(
            session,
            allowGracefulCompletion: true,
            cancellationToken: cancellation.Token);
        var pendingCompletion = new PendingGogCometCompletion(cancellation, completion);

        lock (_gogCometCompletionLock)
            _pendingGogCometCompletion = pendingCompletion;

        _ = ClearPendingGogCometCompletionAsync(pendingCompletion);
    }

    private async Task ClearPendingGogCometCompletionAsync(
        PendingGogCometCompletion pendingCompletion)
    {
        await pendingCompletion.Completion.ConfigureAwait(false);
        lock (_gogCometCompletionLock)
        {
            if (ReferenceEquals(_pendingGogCometCompletion, pendingCompletion))
                _pendingGogCometCompletion = null;
        }

        pendingCompletion.Cancellation.Dispose();
    }

    private static async Task FinishGogCometSessionAsync(
        IGogCometGameSession? session,
        bool allowGracefulCompletion,
        CancellationToken cancellationToken = default)
    {
        if (session == null)
            return;

        try
        {
            if (allowGracefulCompletion)
                await session.CompleteAsync(cancellationToken).ConfigureAwait(false);
            else
                await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Comet] Session cleanup failed: {ex.Message}");
        }
    }

    private sealed record PendingGogCometCompletion(
        CancellationTokenSource Cancellation,
        Task Completion);

    private Process? LaunchCommand(
        MediaItem item,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        LaunchLogBuilder launchLog)
    {
        var (startInfo, target) = PrepareCommandStartInfo(item, environmentOverrides, _launchEnvironmentService);
        launchLog.CaptureProcessStart(startInfo, target, environmentOverrides);
        return _launchProcessService.Start(startInfo);
    }

    internal static (ProcessStartInfo StartInfo, string Target) PrepareCommandStartInfo(MediaItem item,
        IReadOnlyDictionary<string, string>? environmentOverrides, LaunchEnvironmentService environmentService)
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
            environmentService.PrepareHostCommand(psi, environmentOverrides);
            return (psi, target);
        }

        // Otherwise treat as executable command
        var hasEnvOverrides = environmentOverrides is { Count: > 0 };
        var forceDirectExec = hasEnvOverrides || environmentService.IsRunningInsideAppImageRuntime;
        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? BuildLinuxCommandArguments(item, target)
                : item.LauncherArgs ?? string.Empty,
            // In AppImage mode we force direct exec so runtime env sanitization can always apply.
            UseShellExecute = !forceDirectExec
        };
        startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, target, launchFilePath: null);
        environmentService.PrepareCommand(startInfo, environmentOverrides);
        return (startInfo, target);
    }

    internal static string BuildLinuxCommandArguments(MediaItem item, string target)
    {
        ArgumentNullException.ThrowIfNull(item);

        var arguments = item.LauncherArgs ?? string.Empty;
        if (!IsSteamCommandToken(target) ||
            !SteamAppIdResolver.TryGetAppId(item, out _) ||
            HasCommandLineArgument(arguments, "-silent"))
        {
            return arguments;
        }

        return string.IsNullOrWhiteSpace(arguments)
            ? "-silent"
            : $"-silent {arguments}";
    }

    internal static bool IsDelegatedCommand(MediaItem item)
    {
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
        IReadOnlyList<string>? additionalLaunchArguments,
        LaunchLogBuilder launchLog)
    {
        var launchFilePath = _launchPlaylistService.ResolveLaunchFilePath(
            item,
            nodePath,
            usePlaylistForMultiDisc);
        var (startInfo, prefix) = PrepareNativeOrEmulatorStartInfo(item, inheritedConfig,
            nativeWrappers, launchFilePath, environmentOverrides, _launchEnvironmentService, _winePrefixService,
            additionalLaunchArguments);

        launchLog.CaptureProcessStart(startInfo, launchFilePath, environmentOverrides, prefix);
        if (prefix is { RuntimeUncertain: false })
        {
            if (prefix.WouldCreateSecondTree)
                throw new InvalidOperationException(Strings.ResourceManager.GetString("Launch.Prefix.SecondTreeBlocked", Strings.Culture) ??
                    "The selected Proton launch would create a second Windows prefix. Select a compatible prefix or keep its current runner.");
            if (prefix.Runtime == WinePrefixRuntime.Umu && prefix.Layout is PrefixLayout.CustomAlias or PrefixLayout.BrokenAlias)
                throw new InvalidOperationException(Strings.ResourceManager.GetString("Launch.Prefix.UmuAliasBlocked", Strings.Culture) ??
                    "UMU would replace the existing pfx link. Select a compatible prefix before starting the game.");
        }

        if (ShouldManagePrefix(item, inheritedConfig) &&
            prefix is { CanRepair: true, Runtime: WinePrefixRuntime.Proton or WinePrefixRuntime.Umu })
        {
            startInfo.Environment.TryGetValue("PROTONPATH", out var protonPath);
            startInfo.Environment.TryGetValue("PATH", out var searchPath);
            var effectiveProtonPath = prefix.Runtime == WinePrefixRuntime.Proton
                ? LaunchExecutablePathHelper.ResolveForDisplay(startInfo.FileName, searchPath).ResolvedPath
                : protonPath;
            var repair = _protonPrefixRelocationService.Repair(item, prefix, effectiveProtonPath, allowRunnerFallback: false);
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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            item.MediaType == MediaType.Native &&
            !string.IsNullOrWhiteSpace(launchFilePath))
        {
            LinuxFileSystemHelper.EnsureExecutableBitBestEffort(launchFilePath);
        }

        var diagnosticCommand = LaunchSecretRedactor.RedactArguments(
            LaunchProcessService.BuildCommandDisplay(startInfo));
        Debug.WriteLine($"[Launcher] START: {diagnosticCommand}");
        return _launchProcessService.Start(startInfo);
    }

    internal static (ProcessStartInfo StartInfo, PrefixResolution? Prefix) PrepareNativeOrEmulatorStartInfo(
        MediaItem item, EmulatorConfig? inheritedConfig, IReadOnlyList<LaunchWrapper>? nativeWrappers,
        string? launchFilePath, IReadOnlyDictionary<string, string>? environmentOverrides,
        LaunchEnvironmentService environmentService, WinePrefixService winePrefixService,
        IReadOnlyList<string>? additionalLaunchArguments = null, bool preview = false, bool plannedPlaylist = false)
    {
        var (fileName, args, useShellExecute) =
            LaunchPlanBuilder.Build(item, inheritedConfig, nativeWrappers, launchFilePath);
        args = AppendLaunchArguments(args, additionalLaunchArguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName
        };

        startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, fileName, launchFilePath, plannedPlaylist);

        var hasEnvOverrides =
            (environmentOverrides?.Count ?? 0) > 0 ||
            ((environmentOverrides == null) &&
             ((inheritedConfig?.EnvironmentOverrides?.Count ?? 0) > 0 ||
              (item.EnvironmentOverrides?.Count ?? 0) > 0));
        var knownRuntime = WinePrefixService.RuntimeForExecutable(item.LauncherPath is { Length: > 0 } itemLauncher
            ? itemLauncher : inheritedConfig?.Path ?? launchFilePath);
        var prefixRuntime = knownRuntime ??
            (IsUmuBased(item, inheritedConfig, nativeWrappers, environmentOverrides) ? WinePrefixRuntime.Umu :
                IsProtonBased(item, inheritedConfig, nativeWrappers, environmentOverrides)
                    ? WinePrefixRuntime.Proton : WinePrefixRuntime.Wine);

        // Prefix management rules:
        // - explicit item PrefixPath -> always apply
        // - emulator profile with UsesWinePrefix=true -> apply
        var shouldApplyPrefix = ShouldManagePrefix(item, inheritedConfig);
        var isAppImageRuntime = environmentService.IsRunningInsideAppImageRuntime;

        // Ensure env vars + wrapper arguments are honored (shell exec can drop env vars).
        var requiresDirectExec = isAppImageRuntime ||
                                 shouldApplyPrefix ||
                                 hasEnvOverrides ||
                                 (nativeWrappers is { Count: > 0 }) ||
                                 !string.IsNullOrWhiteSpace(args);

        startInfo.UseShellExecute = requiresDirectExec ? false : useShellExecute;

        environmentService.PrepareNativeOrEmulator(
            startInfo,
            item,
            inheritedConfig,
            environmentOverrides);

        startInfo.Arguments = args ?? string.Empty;
        var wineOverride = HasExplicitOverride("WINEPREFIX");
        var compatOverride = HasExplicitOverride("STEAM_COMPAT_DATA_PATH");
        var prefix = shouldApplyPrefix || knownRuntime.HasValue || wineOverride || compatOverride
            ? winePrefixService.PrepareLaunch(item, startInfo, prefixRuntime, shouldApplyPrefix, scaffold: !preview,
                wineOverride: wineOverride, compatOverride: compatOverride,
                runtimeUncertain: !knownRuntime.HasValue || nativeWrappers is { Count: > 0 })
            : null;
        return (startInfo, prefix);

        bool HasExplicitOverride(string key) => environmentOverrides is { Count: > 0 }
            ? ContainsOverride(environmentOverrides, key)
            : ContainsOverride(inheritedConfig?.EnvironmentOverrides, key) ||
              ContainsOverride(item.EnvironmentOverrides, key);

        static bool ContainsOverride(IReadOnlyDictionary<string, string>? variables, string key) =>
            variables?.Keys.Any(candidate => string.Equals(candidate?.Trim(), key, StringComparison.Ordinal)) == true;
    }

    private static bool ShouldManagePrefix(MediaItem item, EmulatorConfig? emulator) =>
        !string.IsNullOrWhiteSpace(item.PrefixPath) ||
        item.MediaType == MediaType.Emulator && emulator?.UsesWinePrefix == true;

    internal static string AppendLaunchArguments(
        string? existingArguments,
        IReadOnlyList<string>? additionalArguments)
    {
        if (additionalArguments is not { Count: > 0 })
            return existingArguments?.Trim() ?? string.Empty;

        var formatted = string.Join(' ', additionalArguments.Select(QuoteProcessArgument));
        return string.IsNullOrWhiteSpace(existingArguments)
            ? formatted
            : $"{existingArguments.Trim()} {formatted}";
    }

    private static string QuoteProcessArgument(string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder(argument.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
                builder.Append('\\', backslashes * 2 + 1);
            else
                builder.Append('\\', backslashes);

            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static string DescribeLaunchOutcome(LaunchResult result) => result.Outcome switch
    {
        _ when result.WasStoppedByUser => "Tracked session stopped by user",
        LaunchOutcome.ExitedEarly => "Process started and exited early with an error",
        LaunchOutcome.WatchedProcessNotFound => "Launcher started, but the configured game process was not found",
        LaunchOutcome.StartFailed => "Process could not be started",
        _ when result.WasSessionTracked => "Tracked session completed",
        _ => "Launch handed off successfully"
    };

    private static string ResolveWorkingDirectory(string? overrideDirectory, string fileName, string? launchFilePath,
        bool plannedPlaylist = false)
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
            if (!plannedPlaylist && Directory.Exists(launchFilePath))
                return launchFilePath;

            if (plannedPlaylist || File.Exists(launchFilePath))
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
        return IsSteamCommandToken(executable) ||
               string.Equals(executable, "heroic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSteamCommandToken(string token)
    {
        var executable = Path.GetFileName(token.Trim('"', '\''));
        return string.Equals(executable, "steam", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasCommandLineArgument(string arguments, string expectedArgument) =>
        arguments
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(argument => string.Equals(
                argument.Trim('"', '\''),
                expectedArgument,
                StringComparison.OrdinalIgnoreCase));

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
