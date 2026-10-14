using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Epic.Legendary;

internal sealed record LegendaryCliCommand(
    string Operation,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

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
        if (command.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(command), "The Legendary command timeout must be positive.");

        Directory.CreateDirectory(_configDirectory);
        var executablePath = _executableResolver.GetRequiredExecutable();
        var startInfo = CreateStartInfo(executablePath, command.Arguments);

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(command.Timeout);

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

    private ProcessStartInfo CreateStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Legendary supports this override natively and will keep config, credentials,
        // manifests and temporary metadata inside this Retromind-owned directory.
        startInfo.Environment[ConfigPathEnvironmentVariable] = _configDirectory;
        startInfo.Environment.Remove("VLC_PLUGIN_PATH");
        AppImageToolResolver.ConfigureBundledToolEnvironment(startInfo, executablePath);

        foreach (var argument in arguments)
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
