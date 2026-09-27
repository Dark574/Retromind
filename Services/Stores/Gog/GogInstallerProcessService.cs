using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services.Stores.Gog;

internal sealed record GogInstallerProcessResult(
    bool Started,
    int ExitCode,
    long DurationMs,
    string? StartErrorMessage,
    bool HasUnsupportedFlagsError,
    bool HasShellParsingError,
    bool HasTerminalSpawnError,
    bool HasRuntimeCrashError);

internal sealed record GogLinuxInstallerCompatibilityEnvironment(
    string? ShimDirectory,
    string? RealKonsolePath);

/// <summary>
/// Owns low-level GOG installer process execution and runtime diagnostics.
/// It has no dependency on Avalonia or view-model state; output is reported
/// through the supplied log callback.
/// </summary>
public sealed class GogInstallerProcessService
{
    internal ProcessStartInfo CreateStartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        HostProcessEnvironmentSanitizer.Sanitize(startInfo);
        return startInfo;
    }

    internal async Task<GogInstallerProcessResult> ExecuteAsync(
        ProcessStartInfo startInfo,
        Action<string> appendLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(appendLog);

        var hasUnsupportedFlagsError = false;
        var hasShellParsingError = false;
        var hasTerminalSpawnError = false;
        var hasRuntimeCrashError = false;

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, eventArgs) => InspectAndAppend(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => InspectAndAppend(eventArgs.Data);

        if (!process.Start())
        {
            return new GogInstallerProcessResult(
                false,
                -1,
                0,
                "Installer process could not be started.",
                false,
                false,
                false,
                false);
        }

        var stopwatch = Stopwatch.StartNew();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            // WaitForExit also drains the asynchronous stdout/stderr handlers so
            // the final installer lines are not lost immediately before disposal.
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // The process may have exited between the check and kill.
                }
            }

            throw;
        }

        stopwatch.Stop();
        appendLog($"Exit code: {process.ExitCode}");

        return new GogInstallerProcessResult(
            true,
            process.ExitCode,
            stopwatch.ElapsedMilliseconds,
            null,
            hasUnsupportedFlagsError,
            hasShellParsingError,
            hasTerminalSpawnError,
            hasRuntimeCrashError);

        void InspectAndAppend(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (LooksLikeUnsupportedFlagError(line))
                hasUnsupportedFlagsError = true;
            if (LooksLikeShellArgumentParsingError(line))
                hasShellParsingError = true;
            if (LooksLikeTerminalSpawnError(line))
                hasTerminalSpawnError = true;
            if (LooksLikeRuntimeCrashError(line))
                hasRuntimeCrashError = true;

            appendLog(line);
        }
    }

    internal GogLinuxInstallerCompatibilityEnvironment PrepareLinuxCompatibilityEnvironment(
        Action<string> appendLog)
    {
        ArgumentNullException.ThrowIfNull(appendLog);

        if (!OperatingSystem.IsLinux())
            return new GogLinuxInstallerCompatibilityEnvironment(null, null);

        var realKonsolePath = EnvironmentPathHelper.TryFindExecutableInCurrentPath("konsole");
        if (string.IsNullOrWhiteSpace(realKonsolePath))
            return new GogLinuxInstallerCompatibilityEnvironment(null, null);

        try
        {
            var shimDirectory = Path.Combine(
                Path.GetTempPath(),
                "retromind-gog-shims",
                $"konsole-{Guid.NewGuid():N}");
            Directory.CreateDirectory(shimDirectory);

            var shimPath = Path.Combine(shimDirectory, "konsole");
            const string shimScript = """
            #!/usr/bin/env bash
            real="${RETROMIND_REAL_KONSOLE:-konsole}"
            args=()
            for arg in "$@"; do
              if [[ "$arg" == "-title" ]]; then
                args+=("--title")
              else
                args+=("$arg")
              fi
            done
            exec "$real" "${args[@]}"
            """;
            File.WriteAllText(
                shimPath,
                shimScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(
                shimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            appendLog($"Linux compatibility shim enabled for konsole: {shimPath}");
            return new GogLinuxInstallerCompatibilityEnvironment(shimDirectory, realKonsolePath);
        }
        catch (Exception ex)
        {
            appendLog($"Warning: could not initialize konsole compatibility shim ({ex.Message})");
            return new GogLinuxInstallerCompatibilityEnvironment(null, null);
        }
    }

    internal static void ApplyLinuxCompatibilityEnvironment(
        ProcessStartInfo startInfo,
        GogLinuxInstallerCompatibilityEnvironment compatibilityEnvironment)
    {
        if (string.IsNullOrWhiteSpace(compatibilityEnvironment.ShimDirectory))
            return;

        var currentPath = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
            ? configuredPath
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? compatibilityEnvironment.ShimDirectory
            : compatibilityEnvironment.ShimDirectory + Path.PathSeparator + currentPath;

        if (!string.IsNullOrWhiteSpace(compatibilityEnvironment.RealKonsolePath))
            startInfo.Environment["RETROMIND_REAL_KONSOLE"] = compatibilityEnvironment.RealKonsolePath;
    }

    internal static void CleanupLinuxCompatibilityEnvironment(
        GogLinuxInstallerCompatibilityEnvironment compatibilityEnvironment)
    {
        if (string.IsNullOrWhiteSpace(compatibilityEnvironment.ShimDirectory))
            return;

        try
        {
            if (Directory.Exists(compatibilityEnvironment.ShimDirectory))
                Directory.Delete(compatibilityEnvironment.ShimDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a temporary compatibility shim.
        }
    }

    internal static void AppendRunnerEnvironmentSnapshot(
        Action<string> appendLog,
        ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(appendLog);

        string[] keys =
        [
            "PROTONPATH",
            "STEAM_COMPAT_DATA_PATH",
            "WINEPREFIX",
            "STEAM_COMPAT_CLIENT_INSTALL_PATH",
            "STEAM_COMPAT_INSTALL_PATH",
            "PROTON_LOG",
            "PROTON_LOG_DIR",
            "PROTON_USE_XALIA"
        ];

        foreach (var key in keys)
        {
            if (startInfo.Environment.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                appendLog($"ENV {key}={value}");
        }
    }

    internal static void AppendWineDosDeviceMappings(Action<string> appendLog, string prefixRoot)
    {
        ArgumentNullException.ThrowIfNull(appendLog);
        if (string.IsNullOrWhiteSpace(prefixRoot))
            return;

        var dosdevicesPath = Path.Combine(prefixRoot, "dosdevices");
        if (!Directory.Exists(dosdevicesPath))
            return;

        foreach (var path in Directory.EnumerateFileSystemEntries(dosdevicesPath).OrderBy(
                     path => path,
                     StringComparer.Ordinal))
        {
            var label = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            try
            {
                var linkTarget = File.ResolveLinkTarget(path, returnFinalTarget: false);
                appendLog($"[Windows prefix] {label} -> {linkTarget?.FullName ?? "(not a symlink)"}");
            }
            catch (Exception ex)
            {
                appendLog($"[Windows prefix] {label} -> <unresolved: {ex.Message}>");
            }
        }
    }

    internal static string FormatCommand(ProcessStartInfo startInfo)
    {
        if (startInfo.ArgumentList.Count == 0)
            return startInfo.FileName ?? string.Empty;

        var builder = new StringBuilder(startInfo.FileName ?? string.Empty);
        foreach (var argument in startInfo.ArgumentList)
        {
            builder.Append(' ');
            builder.Append(GogPlayTaskParser.QuoteArgumentIfNeeded(argument));
        }

        return builder.ToString();
    }

    internal static bool LooksLikeUnsupportedFlagError(string line)
        => line.IndexOf("unrecognized flag", StringComparison.OrdinalIgnoreCase) >= 0 ||
           line.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0 ||
           line.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static bool LooksLikeShellArgumentParsingError(string line)
        => line.IndexOf("syntax error", StringComparison.OrdinalIgnoreCase) >= 0 ||
           line.IndexOf("syntaxfehler", StringComparison.OrdinalIgnoreCase) >= 0 ||
           line.IndexOf("unexpected token", StringComparison.OrdinalIgnoreCase) >= 0 ||
           line.IndexOf("unerwarteten symbol", StringComparison.OrdinalIgnoreCase) >= 0;

    internal static bool LooksLikeTerminalSpawnError(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return (line.IndexOf("konsole", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (line.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 line.IndexOf("unbekannte option", StringComparison.OrdinalIgnoreCase) >= 0)) ||
               line.IndexOf("couldn't run mojosetup", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("xterm", StringComparison.OrdinalIgnoreCase) >= 0 &&
               line.IndexOf("option", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static bool LooksLikeRuntimeCrashError(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.IndexOf("Unhandled exception code", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("EXCEPTION_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("NtRaiseException", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
