using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Epic.Legendary;

internal sealed record LegendaryCliCommand(
    string Operation,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    Action<string>? OutputReceived = null,
    string? StandardInput = null,
    bool SuppressBrowserLaunch = false,
    bool IncludeErrorDetails = true)
{
    public override string ToString()
    {
        return $"LegendaryCliCommand {{ Operation = {Operation}, ArgumentCount = {Arguments.Count}, " +
               $"StandardInput = {(StandardInput == null ? "<none>" : "<redacted>")} }}";
    }
}

internal sealed record LegendaryCliResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal interface ILegendaryCliProcessRunner
{
    Task<LegendaryCliResult> RunAsync(LegendaryCliCommand command, CancellationToken ct = default);
}

internal sealed class LegendaryCliProcessRunner : ILegendaryCliProcessRunner
{
    internal const string ConfigPathEnvironmentVariable = "LEGENDARY_CONFIG_PATH";

    private readonly LegendaryExecutableResolver _executableResolver;
    private readonly string _configDirectory;

    internal LegendaryCliProcessRunner(
        LegendaryExecutableResolver executableResolver,
        string configDirectory)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _configDirectory = Path.GetFullPath(configDirectory);
    }

    internal static string GetDefaultConfigDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(Path.GetFullPath(dataRoot), "Tools", "Legendary", "Data");
    }

    public async Task<LegendaryCliResult> RunAsync(
        LegendaryCliCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Timeout != Timeout.InfiniteTimeSpan && command.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(command), "The Legendary command timeout must be positive.");

        Directory.CreateDirectory(_configDirectory);
        var executablePath = _executableResolver.GetRequiredExecutable();
        var startInfo = CreateStartInfo(executablePath, command);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new LegendaryCliException($"Legendary could not start the '{command.Operation}' operation.");
        }
        catch (Win32Exception ex)
        {
            throw new LegendaryCliException(
                $"Legendary could not start the '{command.Operation}' operation.",
                ex);
        }

        if (command.StandardInput != null)
        {
            try
            {
                await process.StandardInput
                    .WriteLineAsync(command.StandardInput.AsMemory(), ct)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                TryKill(process);
                throw new LegendaryCliException(
                    $"Legendary could not receive input for the '{command.Operation}' operation.",
                    ex);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (command.Timeout != Timeout.InfiniteTimeSpan)
            timeoutCts.CancelAfter(command.Timeout);

        var outputGate = new object();
        void ReportOutput(string line)
        {
            if (command.OutputReceived == null)
                return;

            lock (outputGate)
            {
                try
                {
                    command.OutputReceived(line);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Legendary] Output callback failed: {ex.Message}");
                }
            }
        }

        var stdoutTask = ReadOutputAsync(
            process.StandardOutput,
            ReportOutput,
            timeoutCts.Token);
        var stderrTask = ReadOutputAsync(
            process.StandardError,
            ReportOutput,
            timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await ObserveOutputTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new LegendaryCliException(
                $"Legendary's '{command.Operation}' operation timed out after {command.Timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveOutputTasksAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        return new LegendaryCliResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private static async Task<string> ReadOutputAsync(
        StreamReader reader,
        Action<string> outputReceived,
        CancellationToken ct)
    {
        var completeOutput = new StringBuilder();
        var pendingLine = new StringBuilder();
        var buffer = new char[2048];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            completeOutput.Append(buffer, 0, read);
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    ReportPendingLine(pendingLine, outputReceived);
                    continue;
                }

                pendingLine.Append(character);
            }
        }

        ReportPendingLine(pendingLine, outputReceived);
        return completeOutput.ToString();
    }

    private static void ReportPendingLine(StringBuilder pendingLine, Action<string> outputReceived)
    {
        if (pendingLine.Length == 0)
            return;

        var line = pendingLine.ToString().Trim();
        pendingLine.Clear();
        if (line.Length > 0)
            outputReceived(line);
    }

    private ProcessStartInfo CreateStartInfo(
        string executablePath,
        LegendaryCliCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.StandardInput != null,
            CreateNoWindow = true
        };

        // Legendary supports this override natively and will keep config, credentials,
        // manifests and temporary metadata inside this Retromind-owned directory.
        startInfo.Environment[ConfigPathEnvironmentVariable] = _configDirectory;
        startInfo.Environment.Remove("VLC_PLUGIN_PATH");
        if (command.SuppressBrowserLaunch)
        {
            // Retromind already opened the Epic authorization page. Legendary's
            // interactive stdin flow would otherwise open it a second time.
            startInfo.Environment["BROWSER"] = "true";
        }
        AppImageToolResolver.ConfigureBundledToolEnvironment(startInfo, executablePath);

        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private static async Task ObserveOutputTasksAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller's cancellation is already propagated by RunAsync.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during cancellation/timeout.
        }
    }
}
