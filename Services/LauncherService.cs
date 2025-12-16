using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Executes media items (native, emulator-based, or command/url).
/// Also tracks playtime and can configure Wine prefixes for non-native launches on Linux.
/// </summary>
public sealed class LauncherService
{
    private const int MinPlayTimeSeconds = 5;

    private readonly string _libraryRootPath;

    public LauncherService(string libraryRootPath)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
    }

    public async Task LaunchAsync(
        MediaItem item,
        EmulatorConfig? inheritedConfig = null,
        List<string>? nodePath = null,
        CancellationToken cancellationToken = default)
    {
        if (item == null) return;

        Process? process = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process = item.MediaType == MediaType.Command
                ? LaunchCommand(item)
                : LaunchNativeOrEmulator(item, inheritedConfig, nodePath);

            // Tracking strategy:
            // A) If OverrideWatchProcess is set, we track by process name (for launchers like Steam).
            // B) Otherwise, if we have a process handle, wait for it.
            // C) If neither is available (typical for URL commands), we cannot track duration reliably.
            if (!string.IsNullOrWhiteSpace(item.OverrideWatchProcess))
            {
                await WatchProcessByNameAsync(item.OverrideWatchProcess, cancellationToken).ConfigureAwait(false);
            }
            else if (process is { HasExited: false })
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // App shutdown or caller cancellation: treat as "no/partial session".
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Error during launch/tracking: {ex.Message}");
        }
        finally
        {
            stopwatch.Stop();
            EvaluateSession(item, stopwatch.Elapsed);
        }
    }

    private static Process? LaunchCommand(MediaItem item)
    {
        // For Command media we expect FilePath to be a URL/protocol or an executable,
        // and LauncherArgs to hold the argument string.
        if (string.IsNullOrWhiteSpace(item.LauncherArgs))
            return null;

        return Process.Start(new ProcessStartInfo
        {
            FileName = item.FilePath ?? "xdg-open",
            Arguments = item.LauncherArgs,
            UseShellExecute = true
        });
    }

    private Process? LaunchNativeOrEmulator(MediaItem item, EmulatorConfig? inheritedConfig, List<string>? nodePath)
    {
        try
        {
            var (fileName, templateArgs, isNativeExecution) = ResolveLaunchPlan(item, inheritedConfig);

            var useShellExecute = isNativeExecution;

            // Linux special case:
            // For shell scripts, direct execution with a working directory is often more reliable.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                fileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            {
                useShellExecute = false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = useShellExecute
            };

            startInfo.WorkingDirectory = ResolveWorkingDirectory(fileName, item.FilePath);

            // Configure Wine prefix for non-native launches (emulator/custom launcher).
            if (!isNativeExecution)
            {
                ConfigureWinePrefix(item, nodePath, startInfo);
            }

            startInfo.Arguments = isNativeExecution
                ? BuildNativeArguments(templateArgs)
                : BuildArgumentsString(item, templateArgs);

            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Failed to launch: {ex.Message}");
            return null;
        }
    }

    private static (string FileName, string? TemplateArgs, bool IsNativeExecution) ResolveLaunchPlan(
        MediaItem item,
        EmulatorConfig? inheritedConfig)
    {
        // Priority:
        // 1) Item-level custom launcher
        // 2) Inherited emulator profile
        // 3) Native execution (FilePath)
        if (!string.IsNullOrWhiteSpace(item.LauncherPath))
        {
            var args = string.IsNullOrWhiteSpace(item.LauncherArgs) ? "{file}" : item.LauncherArgs;
            return (item.LauncherPath, args, IsNativeExecution: false);
        }

        if (inheritedConfig != null)
        {
            var args = CombineTemplateArguments(inheritedConfig.Arguments, item.LauncherArgs);
            return (inheritedConfig.Path, args, IsNativeExecution: false);
        }

        if (string.IsNullOrWhiteSpace(item.FilePath))
            throw new InvalidOperationException("MediaItem.FilePath is required for native execution.");

        return (item.FilePath, item.LauncherArgs, IsNativeExecution: true);
    }

    private static string? ResolveWorkingDirectory(string fileName, string? itemFilePath)
    {
        // Prefer the launcher/executable directory.
        if (File.Exists(fileName))
            return Path.GetDirectoryName(fileName) ?? string.Empty;

        // Fallback to ROM/file directory.
        if (!string.IsNullOrWhiteSpace(itemFilePath) && File.Exists(itemFilePath))
            return Path.GetDirectoryName(itemFilePath) ?? string.Empty;

        return string.Empty;
    }

    private static string CombineTemplateArguments(string? baseArgs, string? itemArgs)
    {
        baseArgs ??= string.Empty;
        itemArgs ??= string.Empty;

        if (string.IsNullOrWhiteSpace(itemArgs))
            return baseArgs;

        // Smart injection:
        // If both base and item arguments contain "{file}",
        // we inject the item argument string into the base template at "{file}" position.
        // This allows users to control the relative position of {file} inside their custom args.
        if (baseArgs.Contains("{file}", StringComparison.Ordinal) &&
            itemArgs.Contains("{file}", StringComparison.Ordinal))
        {
            return baseArgs.Replace("{file}", itemArgs, StringComparison.Ordinal);
        }

        // Otherwise, append item args to the base args.
        return $"{baseArgs} {itemArgs}".Trim();
    }

    private static string BuildNativeArguments(string? templateArgs)
    {
        // Native apps typically should NOT receive their own path as "{file}" argument.
        // If the user left "{file}" in args (common default), we ignore it for native execution.
        if (!string.IsNullOrWhiteSpace(templateArgs) &&
            templateArgs.Contains("{file}", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return templateArgs ?? string.Empty;
    }

    private void ConfigureWinePrefix(MediaItem item, List<string>? nodePath, ProcessStartInfo startInfo)
    {
        string? prefixPath = null;
        string? relativePrefixPathToSave = null;

        // Priority 1: Existing saved path (relative to library root).
        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
        {
            prefixPath = Path.Combine(_libraryRootPath, item.PrefixPath);
        }
        // Priority 2: Generate a stable path based on the node structure.
        else if (nodePath is { Count: > 0 })
        {
            // Library/<nodePath>/Prefixes/<safeTitle>
            var relParts = new List<string>(nodePath) { "Prefixes", SanitizeForPathSegment(item.Title) };

            relativePrefixPathToSave = Path.Combine(relParts.ToArray());
            prefixPath = Path.Combine(_libraryRootPath, relativePrefixPathToSave);
        }
        // Priority 3: Fallback near the ROM/file (portable but less organized).
        else if (!string.IsNullOrWhiteSpace(item.FilePath))
        {
            var dir = Path.GetDirectoryName(item.FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                prefixPath = Path.Combine(dir, "pfx");
        }

        if (string.IsNullOrWhiteSpace(prefixPath))
            return;

        Directory.CreateDirectory(prefixPath);
        startInfo.EnvironmentVariables["WINEPREFIX"] = prefixPath;

        // Persist generated relative path.
        if (relativePrefixPathToSave != null)
            item.PrefixPath = relativePrefixPathToSave;
    }

    private static string SanitizeForPathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var safe = input.Replace(' ', '_');

        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c.ToString(), string.Empty);

        while (safe.Contains("__", StringComparison.Ordinal))
            safe = safe.Replace("__", "_", StringComparison.Ordinal);

        return safe;
    }

    private static string BuildArgumentsString(MediaItem item, string? templateArgs)
    {
        var fullPath = string.IsNullOrWhiteSpace(item.FilePath) ? string.Empty : Path.GetFullPath(item.FilePath);

        // If the path contains spaces, quote it unless the template already provides quotes around "{file}".
        var quotedPath = (!string.IsNullOrEmpty(fullPath) && fullPath.Contains(' ', StringComparison.Ordinal))
            ? $"\"{fullPath}\""
            : fullPath;

        if (string.IsNullOrWhiteSpace(templateArgs))
        {
            // Default: just the file path (quoted if needed).
            return quotedPath;
        }

        // If the user provided explicit quotes like "\"{file}\"", preserve exact quoting behavior.
        if (templateArgs.Contains("\"{file}\"", StringComparison.Ordinal))
        {
            return templateArgs.Replace("{file}", fullPath, StringComparison.Ordinal);
        }

        return templateArgs.Replace("{file}", quotedPath, StringComparison.Ordinal);
    }

    private static async Task WatchProcessByNameAsync(string processName, CancellationToken cancellationToken)
    {
        var cleanName = Path.GetFileNameWithoutExtension(processName);

        var startupTimeout = TimeSpan.FromMinutes(3);
        var startWatch = Stopwatch.StartNew();

        // Phase 1: wait for process to appear.
        while (startWatch.Elapsed < startupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Process.GetProcessesByName(cleanName).Length > 0)
                break;

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        // If not found in time, we cannot track.
        if (Process.GetProcessesByName(cleanName).Length == 0)
            return;

        // Phase 2: wait for process to disappear.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            if (Process.GetProcessesByName(cleanName).Length == 0)
                break;
        }
    }

    private static void EvaluateSession(MediaItem item, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;

        if (seconds > MinPlayTimeSeconds)
        {
            UpdateStats(item, elapsed);
            return;
        }

        // For untracked commands (e.g. steam://) we at least count an "attempt".
        if (item.MediaType == MediaType.Command && string.IsNullOrWhiteSpace(item.OverrideWatchProcess))
        {
            UpdateStats(item, TimeSpan.Zero);
        }
    }

    private static void UpdateStats(MediaItem item, TimeSpan sessionTime)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
        item.TotalPlayTime += sessionTime;
    }
}