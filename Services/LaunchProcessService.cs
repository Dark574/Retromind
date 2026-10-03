using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services;

internal enum ProcessWatchOutcome
{
    Tracked,
    AlreadyRunning,
    NotFound
}

internal enum LaunchSessionStopRequestResult
{
    NotAvailable,
    GracefulCloseRequested,
    ForceStopArmed,
    ForceStopRequested,
    AlreadyExited
}

internal readonly record struct ActiveLaunchSessionRegistration(long Id);

/// <summary>
/// Owns the operating-system process lifecycle used by media launches.
/// </summary>
public sealed class LaunchProcessService
{
    private static readonly TimeSpan WatchProcessStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WatchProcessStartupPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DelegatedCommandObservationTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ForceStopGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ForceStopConfirmationWindow = TimeSpan.FromSeconds(20);

    private sealed class ActiveLaunchSession
    {
        public required long Id { get; init; }
        public int? RootProcessId { get; init; }
        public string? WatchedProcessName { get; init; }
        public HashSet<int>? WatchedProcessIds { get; init; }
        public DateTime? FirstStopRequestUtc { get; set; }
        public bool WasStopRequested { get; set; }
    }

    private readonly object _activeSessionGate = new();
    private readonly SemaphoreSlim _activeSessionStopGate = new(1, 1);
    private ActiveLaunchSession? _activeSession;
    private long _nextActiveSessionId;

    public Process? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (!startInfo.UseShellExecute)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            var command = BuildCommandDisplay(startInfo);
            var details = new StringBuilder(ex.Message)
                .AppendLine()
                .Append("Command: ")
                .Append(command);

            if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
                details.AppendLine()
                    .Append("Working directory: ")
                    .Append(startInfo.WorkingDirectory);
            }

            throw new InvalidOperationException(details.ToString(), ex);
        }
    }

    internal LaunchProcessOutputCapture? StartOutputCapture(Process? process) =>
        LaunchProcessOutputCapture.TryStart(process);

    internal bool IsProcessRunning(string processName)
        => GetProcessIds(processName).Count > 0;

    internal IReadOnlyCollection<int> GetProcessIds(string processName)
    {
        var cleanName = GetWatchProcessName(processName);
        var processes = Process.GetProcessesByName(cleanName);
        try
        {
            return processes.Select(process => process.Id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    internal ActiveLaunchSessionRegistration BeginDirectSession(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return SetActiveSession(
            rootProcessId: process.Id,
            watchedProcessName: null,
            watchedProcessIds: null);
    }

    internal ActiveLaunchSessionRegistration BeginWatchedSession(
        string processName,
        IEnumerable<int> processIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentNullException.ThrowIfNull(processIds);
        return SetActiveSession(
            rootProcessId: null,
            watchedProcessName: GetWatchProcessName(processName),
            watchedProcessIds: processIds);
    }

    internal void UpdateWatchedSession(
        ActiveLaunchSessionRegistration registration,
        IEnumerable<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        lock (_activeSessionGate)
        {
            if (_activeSession is not { } session || session.Id != registration.Id)
                return;

            session.WatchedProcessIds?.UnionWith(processIds);
        }
    }

    internal void EndActiveSession(ActiveLaunchSessionRegistration registration)
    {
        lock (_activeSessionGate)
        {
            if (_activeSession?.Id == registration.Id)
                _activeSession = null;
        }
    }

    internal bool WasStopRequested(ActiveLaunchSessionRegistration registration)
    {
        lock (_activeSessionGate)
            return _activeSession is { } session &&
                   session.Id == registration.Id &&
                   session.WasStopRequested;
    }

    internal Task<LaunchSessionStopRequestResult> RequestActiveSessionStopAsync(
        CancellationToken cancellationToken = default) =>
        RequestActiveSessionStopAsync(
            ForceStopGracePeriod,
            ForceStopConfirmationWindow,
            cancellationToken);

    internal async Task<LaunchSessionStopRequestResult> RequestActiveSessionStopAsync(
        TimeSpan gracePeriod,
        TimeSpan confirmationWindow,
        CancellationToken cancellationToken = default)
    {
        await _activeSessionStopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveLaunchSession? session;
            var now = DateTime.UtcNow;
            var isConfirmation = false;
            lock (_activeSessionGate)
            {
                session = _activeSession;
                if (session == null)
                    return LaunchSessionStopRequestResult.NotAvailable;

                if (session.FirstStopRequestUtc is not { } firstRequest ||
                    now - firstRequest > confirmationWindow)
                {
                    session.FirstStopRequestUtc = now;
                }
                else
                {
                    session.WasStopRequested = true;
                    isConfirmation = true;
                }
            }

            if (!isConfirmation)
            {
                using var targets = ResolveActiveSessionProcesses(session);
                if (targets.Count == 0)
                    return LaunchSessionStopRequestResult.AlreadyExited;

                var closeRequested = false;
                foreach (var process in targets.Processes)
                {
                    try
                    {
                        closeRequested |= process.CloseMainWindow();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Launcher] Graceful session close failed for PID {process.Id}: {ex.Message}");
                    }
                }

                if (closeRequested)
                {
                    lock (_activeSessionGate)
                    {
                        if (_activeSession?.Id == session.Id)
                            session.WasStopRequested = true;
                    }

                    return LaunchSessionStopRequestResult.GracefulCloseRequested;
                }

                return LaunchSessionStopRequestResult.ForceStopArmed;
            }

            var firstStopRequestUtc = session.FirstStopRequestUtc ?? now;
            var remainingGracePeriod = gracePeriod - (now - firstStopRequestUtc);
            if (remainingGracePeriod > TimeSpan.Zero)
                await Task.Delay(remainingGracePeriod, cancellationToken).ConfigureAwait(false);

            lock (_activeSessionGate)
            {
                if (_activeSession?.Id != session.Id)
                    return LaunchSessionStopRequestResult.AlreadyExited;
            }

            using var forceTargets = ResolveActiveSessionProcesses(session);
            if (forceTargets.Count == 0)
                return LaunchSessionStopRequestResult.AlreadyExited;

            foreach (var process in forceTargets.Processes)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Launcher] Forced session stop failed for PID {process.Id}: {ex.Message}");
                }
            }

            return LaunchSessionStopRequestResult.ForceStopRequested;
        }
        finally
        {
            _activeSessionStopGate.Release();
        }
    }

    private ActiveLaunchSessionRegistration SetActiveSession(
        int? rootProcessId,
        string? watchedProcessName,
        IEnumerable<int>? watchedProcessIds)
    {
        var id = Interlocked.Increment(ref _nextActiveSessionId);
        lock (_activeSessionGate)
        {
            _activeSession = new ActiveLaunchSession
            {
                Id = id,
                RootProcessId = rootProcessId,
                WatchedProcessName = watchedProcessName,
                WatchedProcessIds = watchedProcessIds?.ToHashSet()
            };
        }

        return new ActiveLaunchSessionRegistration(id);
    }

    private ProcessCollection ResolveActiveSessionProcesses(ActiveLaunchSession session)
    {
        if (session.RootProcessId is { } rootProcessId)
        {
            try
            {
                var process = Process.GetProcessById(rootProcessId);
                if (process.HasExited)
                {
                    process.Dispose();
                    return new ProcessCollection([]);
                }

                return new ProcessCollection([process]);
            }
            catch (ArgumentException)
            {
                return new ProcessCollection([]);
            }
        }

        if (!string.IsNullOrWhiteSpace(session.WatchedProcessName))
        {
            int[] processIds;
            lock (_activeSessionGate)
                processIds = session.WatchedProcessIds?.ToArray() ?? [];

            var processes = new List<Process>();
            foreach (var processId in processIds)
            {
                try
                {
                    var process = Process.GetProcessById(processId);
                    if (process.HasExited ||
                        !string.Equals(
                            process.ProcessName,
                            session.WatchedProcessName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        process.Dispose();
                        continue;
                    }

                    processes.Add(process);
                }
                catch (ArgumentException)
                {
                    // The observed process has already exited.
                }
            }

            return new ProcessCollection(processes.ToArray());
        }

        return new ProcessCollection([]);
    }

    private sealed class ProcessCollection(Process[] processes) : IDisposable
    {
        public Process[] Processes { get; } = processes;
        public int Count => Processes.Length;

        public void Dispose()
        {
            foreach (var process in Processes)
                process.Dispose();
        }
    }

    internal async Task<bool> WaitForDelegatedCommandAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (process.HasExited)
            return true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DelegatedCommandObservationTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return process.HasExited;
        }
    }

    internal Task<ProcessWatchOutcome> WatchProcessByNameAsync(
        string processName,
        bool wasRunningBeforeLaunch,
        CancellationToken cancellationToken,
        Action<IReadOnlyCollection<int>>? onProcessesDetected = null) =>
        WatchProcessByNameAsync(
            processName,
            wasRunningBeforeLaunch,
            WatchProcessStartupTimeout,
            WatchProcessStartupPollInterval,
            cancellationToken,
            onProcessesDetected);

    internal async Task<ProcessWatchOutcome> WatchProcessByNameAsync(
        string processName,
        bool wasRunningBeforeLaunch,
        TimeSpan startupTimeout,
        TimeSpan startupPollInterval,
        CancellationToken cancellationToken,
        Action<IReadOnlyCollection<int>>? onProcessesDetected = null)
    {
        var cleanName = GetWatchProcessName(processName);
        var startWatch = Stopwatch.StartNew();

        // If the process is already running, do not block waiting for it to exit.
        // This avoids hanging the launch flow when watching long-running launchers (e.g. Steam).
        if (wasRunningBeforeLaunch)
            return ProcessWatchOutcome.AlreadyRunning;

        // Phase 1: wait for process to appear.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var detectedProcessIds = GetProcessIds(cleanName);
            if (detectedProcessIds.Count > 0)
            {
                onProcessesDetected?.Invoke(detectedProcessIds);
                break;
            }

            var remaining = startupTimeout - startWatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return ProcessWatchOutcome.NotFound;

            var delay = remaining < startupPollInterval
                ? remaining
                : startupPollInterval;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        // Phase 2: wait for process to disappear.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            var observedProcessIds = GetProcessIds(cleanName);
            if (observedProcessIds.Count == 0)
                break;

            onProcessesDetected?.Invoke(observedProcessIds);
        }

        return ProcessWatchOutcome.Tracked;
    }

    internal static string BuildCommandDisplay(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList.Select(LaunchCommandLineHelper.QuoteIfNeeded))
            : startInfo.Arguments;

        return string.IsNullOrWhiteSpace(arguments)
            ? LaunchCommandLineHelper.QuoteIfNeeded(startInfo.FileName)
            : $"{LaunchCommandLineHelper.QuoteIfNeeded(startInfo.FileName)} {arguments}";
    }

    private static string GetWatchProcessName(string processName) =>
        Path.GetFileNameWithoutExtension(processName);
}

internal sealed class LaunchProcessOutputCapture : IDisposable
{
    private const int MaxCharactersPerStream = 2000;

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();
    private readonly TaskCompletionSource _outputCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _outputReadStarted;
    private bool _errorReadStarted;

    private LaunchProcessOutputCapture(Process process)
    {
        _process = process;
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
    }

    public static LaunchProcessOutputCapture? TryStart(Process? process)
    {
        if (process == null ||
            !process.StartInfo.RedirectStandardOutput ||
            !process.StartInfo.RedirectStandardError)
        {
            return null;
        }

        var capture = new LaunchProcessOutputCapture(process);
        try
        {
            process.BeginOutputReadLine();
            capture._outputReadStarted = true;
            process.BeginErrorReadLine();
            capture._errorReadStarted = true;
            return capture;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Could not capture process output: {ex.Message}");
            capture.Dispose();
            return null;
        }
    }

    public string? GetOutput()
    {
        lock (_gate)
        {
            var output = _standardOutput.ToString().Trim();
            var error = _standardError.ToString().Trim();
            if (output.Length == 0 && error.Length == 0)
                return null;

            var result = new StringBuilder();
            if (error.Length > 0)
                result.AppendLine("stderr:").AppendLine(error);

            if (output.Length > 0)
            {
                if (result.Length > 0)
                    result.AppendLine();

                result.AppendLine("stdout:").Append(output);
            }

            return result.ToString();
        }
    }

    public async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(_outputCompleted.Task, _errorCompleted.Task)
                .WaitAsync(timeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A child process may have inherited the output pipes. Keep the launch flow bounded.
        }
    }

    public void Dispose()
    {
        if (_outputReadStarted)
        {
            try
            {
                _process.CancelOutputRead();
            }
            catch (InvalidOperationException)
            {
                // The asynchronous reader already completed.
            }
        }

        if (_errorReadStarted)
        {
            try
            {
                _process.CancelErrorRead();
            }
            catch (InvalidOperationException)
            {
                // The asynchronous reader already completed.
            }
        }

        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.ErrorDataReceived -= OnErrorDataReceived;
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null)
        {
            _outputCompleted.TrySetResult();
            return;
        }

        // Output is redirected so early launch failures can include useful diagnostics.
        // Mirror it to the parent process as well, preserving live console output.
        Console.Out.WriteLine(args.Data);
        AppendTail(_standardOutput, args.Data);
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null)
        {
            _errorCompleted.TrySetResult();
            return;
        }

        Console.Error.WriteLine(args.Data);
        AppendTail(_standardError, args.Data);
    }

    private void AppendTail(StringBuilder target, string? line)
    {
        if (line == null)
            return;

        lock (_gate)
        {
            target.AppendLine(line);
            if (target.Length > MaxCharactersPerStream)
                target.Remove(0, target.Length - MaxCharactersPerStream);
        }
    }
}
