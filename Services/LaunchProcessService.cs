using System;
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

/// <summary>
/// Owns the operating-system process lifecycle used by media launches.
/// </summary>
public sealed class LaunchProcessService
{
    private static readonly TimeSpan WatchProcessStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WatchProcessStartupPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DelegatedCommandObservationTimeout = TimeSpan.FromSeconds(1);

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
    {
        var cleanName = GetWatchProcessName(processName);
        var processes = Process.GetProcessesByName(cleanName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
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
        CancellationToken cancellationToken) =>
        WatchProcessByNameAsync(
            processName,
            wasRunningBeforeLaunch,
            WatchProcessStartupTimeout,
            WatchProcessStartupPollInterval,
            cancellationToken);

    internal async Task<ProcessWatchOutcome> WatchProcessByNameAsync(
        string processName,
        bool wasRunningBeforeLaunch,
        TimeSpan startupTimeout,
        TimeSpan startupPollInterval,
        CancellationToken cancellationToken)
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

            if (IsProcessRunning(cleanName))
                break;

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

            if (!IsProcessRunning(cleanName))
                break;
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
