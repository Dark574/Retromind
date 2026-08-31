using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
    private static readonly TimeSpan WatchProcessStartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WatchProcessStartupPollInterval = TimeSpan.FromSeconds(1);

    private readonly string _libraryRootPath;
    private readonly AppSettings _settings;

    public LauncherService(string libraryRootPath, AppSettings settings)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

        Process? process = null;
        ProcessOutputCapture? outputCapture = null;
        var stopwatch = Stopwatch.StartNew();
        var shouldRecordSession = false;
        var watchedProcessName = string.IsNullOrWhiteSpace(item.OverrideWatchProcess)
            ? null
            : item.OverrideWatchProcess;
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
                watchedProcessWasAlreadyRunning = IsProcessRunning(GetWatchProcessName(watchedProcessName));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Launcher] Could not inspect watched process before launch: {ex.Message}");
            }
        }

        try
        {
            process = item.MediaType == MediaType.Command
                ? LaunchCommand(item, environmentOverrides)
                : LaunchNativeOrEmulator(item, inheritedConfig, nodePath, nativeWrappers, usePlaylistForMultiDisc, environmentOverrides);
            processStartedAt = stopwatch.Elapsed;
            outputCapture = ProcessOutputCapture.TryStart(process);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            process?.Dispose();
            Debug.WriteLine($"[Launcher] Failed to launch: {ex.Message}");
            return LaunchResult.Failed(ex.Message);
        }

        try
        {
            // Tracking strategy:
            // A) If OverrideWatchProcess is set, we track by process name (for launchers like Steam).
            // B) Otherwise, if we have a process handle, wait for it.
            // C) If neither is available (typical for URL commands), we cannot track duration reliably.
            if (watchedProcessName != null)
            {
                var watchOutcome = await WatchProcessByNameAsync(
                        watchedProcessName,
                        watchedProcessWasAlreadyRunning,
                        cancellationToken)
                    .ConfigureAwait(false);
                shouldRecordSession = watchOutcome == ProcessWatchOutcome.Tracked;
                if (watchOutcome == ProcessWatchOutcome.NotFound)
                    missingWatchedProcessName = watchedProcessName;
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

        if (shouldRecordSession && recordStatistics)
        {
            try
            {
                await EvaluateSessionAsync(item, elapsed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Launcher] Failed to record session statistics: {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(missingWatchedProcessName))
            return LaunchResult.WatchedProcessNotFound(missingWatchedProcessName, consoleOutput);

        if (exitCode is not null and not 0 &&
            processStartedAt is { } startedAt &&
            elapsed - startedAt <= TimeSpan.FromSeconds(EarlyFailureThresholdSeconds))
        {
            return LaunchResult.ExitedEarly(exitCode.Value, consoleOutput);
        }

        return LaunchResult.Started;
    }

    private static Process? LaunchCommand(
        MediaItem item,
        IReadOnlyDictionary<string, string>? environmentOverrides)
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
            HostProcessEnvironmentSanitizer.Sanitize(psi);
            SanitizeAppImageRuntimeEnvironment(psi);
            SanitizeStorePortableEnvironment(psi, target, forceStoreCompatSanitization: true);
            ApplyEnvironmentOverrides(psi, environmentOverrides);

            return StartProcess(psi);
        }

        // Otherwise treat as executable command
        var hasEnvOverrides = environmentOverrides is { Count: > 0 };
        var forceDirectExec = hasEnvOverrides || IsRunningInsideAppImageRuntime();
        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = item.LauncherArgs ?? string.Empty,
            // In AppImage mode we force direct exec so runtime env sanitization can always apply.
            UseShellExecute = !forceDirectExec
        };
        startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, target, launchFilePath: null);
        SanitizeAppImageRuntimeEnvironment(startInfo);
        SanitizeFlatpakPortableEnvironment(startInfo, item.LauncherArgs);
        SanitizeStorePortableEnvironment(startInfo, item.LauncherArgs, forceStoreCompatSanitization: true);
        ApplyEnvironmentOverrides(startInfo, environmentOverrides);
        ApplyXdgOverrides(startInfo, item);
        return StartProcess(startInfo);
    }

    private Process? LaunchNativeOrEmulator(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        List<string>? nodePath,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        bool usePlaylistForMultiDisc,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        var (fileName, args, useShellExecute, launchFilePath) =
            ResolveLaunchPlan(item, inheritedConfig, nodePath, nativeWrappers, usePlaylistForMultiDisc);

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
        var isAppImageRuntime = IsRunningInsideAppImageRuntime();

        // Ensure env vars + wrapper arguments are honored (shell exec can drop env vars).
        var requiresDirectExec = isAppImageRuntime ||
                                 shouldApplyPrefix ||
                                 hasEnvOverrides ||
                                 (nativeWrappers is { Count: > 0 }) ||
                                 !string.IsNullOrWhiteSpace(args);

        startInfo.UseShellExecute = requiresDirectExec ? false : useShellExecute;

        if (shouldApplyPrefix)
            ConfigureWinePrefix(item, nodePath, startInfo, isProtonLaunch, isUmuLaunch);
            
        SanitizeAppImageRuntimeEnvironment(startInfo);
        SanitizeFlatpakPortableEnvironment(startInfo, args);
        SanitizeStorePortableEnvironment(startInfo, args, forceStoreCompatSanitization: true);

        // Apply environment overrides (node/emulator/item merged by caller when provided).
        if (environmentOverrides is { Count: > 0 })
        {
            ApplyEnvironmentOverrides(startInfo, environmentOverrides);
        }
        else
        {
            // Apply emulator-level environment overrides (base layer)
            if (inheritedConfig?.EnvironmentOverrides is { Count: > 0 })
                ApplyEnvironmentOverrides(startInfo, inheritedConfig.EnvironmentOverrides);

            // Apply per-item environment overrides (e.g. PROTONPATH, PROTON_LOG, DXVK_HUD)
            if (item.EnvironmentOverrides is { Count: > 0 })
                ApplyEnvironmentOverrides(startInfo, item.EnvironmentOverrides);
        }

        ApplyEmulatorXdgOverrides(startInfo, inheritedConfig);
        ApplyXdgOverrides(startInfo, item);

        startInfo.Arguments = args ?? string.Empty;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            item.MediaType == MediaType.Native &&
            !string.IsNullOrWhiteSpace(launchFilePath))
        {
            LinuxFileSystemHelper.EnsureExecutableBitBestEffort(launchFilePath);
        }

        LogIfEnvSet(startInfo, "PROTONPATH");
        LogIfEnvSet(startInfo, "STEAM_COMPAT_DATA_PATH");
        LogIfEnvSet(startInfo, "WINEPREFIX");
        // DEBUG: log the exact command-line we are about to run
        Debug.WriteLine($"[Launcher] START: {startInfo.FileName} {startInfo.Arguments}");

        return StartProcess(startInfo);
    }

    private static Process? StartProcess(ProcessStartInfo startInfo)
    {
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

    private static string BuildCommandDisplay(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList.Select(QuoteIfNeeded))
            : startInfo.Arguments;

        return string.IsNullOrWhiteSpace(arguments)
            ? QuoteIfNeeded(startInfo.FileName)
            : $"{QuoteIfNeeded(startInfo.FileName)} {arguments}";
    }

    private (string FileName, string? Args, bool UseShellExecute, string? LaunchFilePath) ResolveLaunchPlan(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        List<string>? nodePath,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        bool usePlaylistForMultiDisc)
    {
        // Determine which file should be passed into {file}.
        // Default: primary file (Disc 1 / first entry).
        // Optional: generate an .m3u playlist for multi-disc items and pass the playlist path instead.
        var launchFilePath = ResolveLaunchFilePath(item, nodePath, usePlaylistForMultiDisc);

        // 1) Item-level custom launcher (wrapper/emulator/etc.) always wins
        if (!string.IsNullOrWhiteSpace(item.LauncherPath))
        {
            var templateArgs = string.IsNullOrWhiteSpace(item.LauncherArgs) ? "{file}" : item.LauncherArgs;
            var args = BuildArgumentsString(launchFilePath, templateArgs);

            var fileName = ResolveConfiguredExecutablePath(item.LauncherPath);
            var useShellExecute = false;

            // If there is a wrapper chain, wrap the item-level launcher as inner command.
            if (nativeWrappers is { Count: > 0 })
            {
                var inner = string.IsNullOrWhiteSpace(args)
                    ? QuoteIfNeeded(fileName)
                    : $"{QuoteIfNeeded(fileName)} {args}";

                var folded = FoldWrappers(innerExecutable: inner, nativeWrappers);
                fileName = folded.FileName;
                args = folded.Args;
                useShellExecute = folded.UseShellExecute;
            }

            return (fileName, args, useShellExecute, LaunchFilePath: launchFilePath);
        }

        // 2) Inherited emulator profile
        if (inheritedConfig != null)
        {
            var templateArgs = LaunchArgumentHelper.CombineTemplateArguments(inheritedConfig.Arguments, item.LauncherArgs);
            var args = BuildArgumentsString(launchFilePath, templateArgs);

            var fileName = ResolveConfiguredExecutablePath(inheritedConfig.Path);
            var useShellExecute = false;

            // Apply wrapper chain around the emulator command if present
            if (nativeWrappers is { Count: > 0 })
            {
                var inner = string.IsNullOrWhiteSpace(args)
                    ? QuoteIfNeeded(fileName)
                    : $"{QuoteIfNeeded(fileName)} {args}";

                var folded = FoldWrappers(innerExecutable: inner, nativeWrappers);
                fileName = folded.FileName;
                args = folded.Args;
                useShellExecute = folded.UseShellExecute;
            }

            return (fileName, args, useShellExecute, LaunchFilePath: launchFilePath);
        }

        // 3) Native execution (direct or via wrappers)
        if (string.IsNullOrWhiteSpace(launchFilePath))
            throw new InvalidOperationException("MediaItem.Files must contain at least one valid file for native execution.");

        var nativeArgs = BuildNativeArguments(item.LauncherArgs);

        // Apply wrapper chain if provided and non-empty
        if (nativeWrappers is { Count: > 0 })
        {
            // Here the inner executable is the actual media file itself.
            var inner = string.IsNullOrWhiteSpace(nativeArgs)
                ? QuoteIfNeeded(launchFilePath)
                : $"{QuoteIfNeeded(launchFilePath)} {nativeArgs}";

            var folded = FoldWrappers(innerExecutable: inner, nativeWrappers);
            return (folded.FileName, folded.Args, UseShellExecute: folded.UseShellExecute, LaunchFilePath: launchFilePath);
        }

        // Direct native
        // On Linux, UseShellExecute=true routes through xdg-open/desktop handlers and can be
        // blocked for executables ("not allowed to launch executable in this context").
        // Native game binaries should run directly.
        var useShell = !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        return (launchFilePath, nativeArgs, UseShellExecute: useShell, LaunchFilePath: launchFilePath);
    }

    /// <summary>
    /// Folds a wrapper chain around an already composed inner command line.
    /// Example: inner = "myemu \"rom.smc\" --option", wrappers = [gamemoderun, mangohud]
    /// result: FileName = "gamemoderun", Args = "mangohud myemu \"rom.smc\" --option"
    /// </summary>
    private static (string FileName, string Args, bool UseShellExecute) FoldWrappers(
        string innerExecutable,
        IReadOnlyList<LaunchWrapper> wrappers)
    {
        var current = innerExecutable;
        string? outerFileName = null;
        string outerArgs = string.Empty;

        // We interpret wrapper order as "outer -> inner".
        // Example: [gamemoderun, mangohud] -> gamemoderun mangohud <inner>
        for (int i = wrappers.Count - 1; i >= 0; i--)
        {
            var w = wrappers[i];
            if (string.IsNullOrWhiteSpace(w.Path))
                continue;

            var template = string.IsNullOrWhiteSpace(w.Args) ? "{file}" : w.Args!;
            var argsWithChild = template.Contains("{file}", StringComparison.Ordinal)
                ? template.Replace("{file}", current, StringComparison.Ordinal)
                : $"{template} {current}";

            var resolvedWrapperPath = ResolveConfiguredExecutablePath(w.Path);
            if (string.IsNullOrWhiteSpace(resolvedWrapperPath))
                continue;

            outerFileName = resolvedWrapperPath;
            outerArgs = LaunchArgumentHelper.NormalizeWhitespace(argsWithChild);
            current = string.IsNullOrWhiteSpace(outerArgs)
                ? outerFileName
                : $"{outerFileName} {outerArgs}";
        }

        if (string.IsNullOrWhiteSpace(outerFileName))
        {
            // No valid wrapper path found; fall back to a best-effort split that respects quotes.
            var (fallbackFileName, fallbackArgs) = SplitCommandLinePreservingArgs(current);

            var fallbackUseShellExecute = true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
                fallbackFileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            {
                fallbackUseShellExecute = false;
            }

            return (fallbackFileName, fallbackArgs, fallbackUseShellExecute);
        }

        // Avoid splitting by space: wrapper paths may contain spaces.
        var useShellExecute = true;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            outerFileName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            useShellExecute = false;
        }

        return (outerFileName, outerArgs, useShellExecute);
    }
    
    private string? ResolveLaunchFilePath(MediaItem item, List<string>? nodePath, bool usePlaylistForMultiDisc)
    {
        var primary = item.GetPrimaryLaunchPath();
        if (string.IsNullOrWhiteSpace(primary))
            return null;

        if (!usePlaylistForMultiDisc)
            return primary;

        if (item.Files is not { Count: > 1 })
            return primary;

        // Playlists are stored inside the selected node folder:
        // <LibraryRoot>/<NodePath>/Playlists/<itemId>_<GameTitle>.m3u
        if (nodePath is not { Count: > 0 })
            return primary;

        var playlistPath = CreateOrUpdatePlaylist(item, nodePath);
        return string.IsNullOrWhiteSpace(playlistPath) ? primary : playlistPath;
    }

    private string? CreateOrUpdatePlaylist(MediaItem item, List<string> nodePath)
    {
        try
        {
            var nodeFolder = ResolveNodeFolder(nodePath);
            var playlistsFolder = Path.Combine(nodeFolder, "Playlists");
            Directory.CreateDirectory(playlistsFolder);

            var safeTitle = SanitizeForFilename(item.Title);
            var fileName = $"{item.Id}_{safeTitle}.m3u";
            var fullPath = Path.Combine(playlistsFolder, fileName);

            // Build playlist lines in a stable order (Index ascending, then Label, then Path).
            var ordered = new List<MediaFileRef>(item.Files);
            ordered.Sort(static (a, b) =>
            {
                var ai = a.Index ?? int.MaxValue;
                var bi = b.Index ?? int.MaxValue;
                var c = ai.CompareTo(bi);
                if (c != 0) return c;

                c = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;

                return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

            var lines = new List<string>(capacity: ordered.Count);
            foreach (var f in ordered)
            {
                if (string.IsNullOrWhiteSpace(f.Path))
                    continue;

                // Resolve the file path to an absolute path:
                // - Absolute   -> use as-is
                // - LibraryRelative -> resolve relative to DataRoot (portable mode)
                // Other kinds are currently ignored until a concrete semantics is defined
                string resolved;
                switch (f.Kind)
                {
                    case MediaFileKind.Absolute:
                        resolved = f.Path;
                        break;

                    case MediaFileKind.LibraryRelative:
                        resolved = AppPaths.ResolveDataPathInsideRootOrEmpty(f.Path);
                        if (string.IsNullOrWhiteSpace(resolved))
                            continue;
                        break;

                    default:
                        continue;
                }

                lines.Add(resolved);
            }

            // If we ended up with an invalid playlist, do not create anything.
            if (lines.Count == 0)
                return null;

            File.WriteAllLines(fullPath, lines);
            return fullPath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Failed to create playlist: {ex.Message}");
            return null;
        }
    }

    private string ResolveNodeFolder(List<string> nodePath)
        => PathHelper.ResolveNodeFolder(nodePath, _libraryRootPath);

    private static string SanitizeForFilename(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var sanitized = input.Replace(' ', '_');

        foreach (var c in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(c.ToString(), string.Empty);

        while (sanitized.Contains("__", StringComparison.Ordinal))
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);

        // Keep filenames at a reasonable length for portability/usability.
        const int maxLen = 80;
        if (sanitized.Length > maxLen)
            sanitized = sanitized[..maxLen];

        return sanitized;
    }

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

    private static string ResolveConfiguredExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
            return trimmed;

        // Keep command tokens (e.g. "flatpak", "retroarch") PATH-resolved.
        // Relative paths with separators are treated as DataRoot-relative for portability.
        if (!trimmed.Contains('/') &&
            !trimmed.Contains('\\') &&
            !trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return AppPaths.ResolveDataPath(trimmed);
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

    private static void ApplyEnvironmentOverrides(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides == null || overrides.Count == 0)
            return;

        foreach (var kv in overrides)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            var key = kv.Key.Trim();
            var value = EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(key, kv.Value);
            startInfo.EnvironmentVariables[key] = value ?? string.Empty;
        }
    }

    private static void LogIfEnvSet(ProcessStartInfo startInfo, string key)
    {
        if (startInfo.EnvironmentVariables.ContainsKey(key))
        {
            var value = startInfo.EnvironmentVariables[key];
            if (!string.IsNullOrWhiteSpace(value))
                Debug.WriteLine($"[Launcher] ENV {key}={value}");
        }
    }

    private static void ApplyXdgOverrides(ProcessStartInfo startInfo, MediaItem item)
    {
        // Item-level XDG overrides apply to Native and Emulator launches.
        // Command entries (URL/protocol/external command) are excluded.
        if (item.MediaType == MediaType.Command)
            return;

        // Priority model:
        // Item overrides should win over emulator-level XDG and base environment.
        ApplyXdgPath(startInfo, "XDG_CONFIG_HOME", item.XdgConfigPath, overwriteExisting: true);
        ApplyXdgPath(startInfo, "XDG_DATA_HOME", item.XdgDataPath, overwriteExisting: true);
        ApplyXdgPath(startInfo, "XDG_CACHE_HOME", item.XdgCachePath, overwriteExisting: true);
        ApplyXdgPath(startInfo, "XDG_STATE_HOME", item.XdgStatePath, overwriteExisting: true);
    }

    private static void ApplyXdgPath(ProcessStartInfo startInfo, string key, string? value, bool overwriteExisting = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!overwriteExisting && startInfo.EnvironmentVariables.ContainsKey(key))
            return;

        var resolved = Path.IsPathRooted(value)
            ? value
            : AppPaths.ResolveDataPath(value);

        startInfo.EnvironmentVariables[key] = resolved;
    }

    private static void ApplyEmulatorXdgOverrides(ProcessStartInfo startInfo, EmulatorConfig? emulator)
    {
        if (emulator == null)
            return;

        switch (emulator.XdgMode)
        {
            case EmulatorConfig.XdgOverrideMode.Inherit:
                return;

            case EmulatorConfig.XdgOverrideMode.Host:
                startInfo.EnvironmentVariables.Remove("XDG_CONFIG_HOME");
                startInfo.EnvironmentVariables.Remove("XDG_DATA_HOME");
                startInfo.EnvironmentVariables.Remove("XDG_CACHE_HOME");
                startInfo.EnvironmentVariables.Remove("XDG_STATE_HOME");
                return;

            case EmulatorConfig.XdgOverrideMode.Custom:
                SetXdgPath(startInfo, "XDG_CONFIG_HOME", emulator.XdgConfigPath);
                SetXdgPath(startInfo, "XDG_DATA_HOME", emulator.XdgDataPath);
                SetXdgPath(startInfo, "XDG_CACHE_HOME", emulator.XdgCachePath);
                SetXdgPath(startInfo, "XDG_STATE_HOME", emulator.XdgStatePath);
                return;
        }
    }

    private static void SetXdgPath(ProcessStartInfo startInfo, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var resolved = Path.IsPathRooted(value)
            ? value
            : AppPaths.ResolveDataPath(value);

        startInfo.EnvironmentVariables[key] = resolved;
    }

    private static void SanitizeAppImageRuntimeEnvironment(ProcessStartInfo startInfo)
    {
        // Environment variable overrides require direct execution.
        if (startInfo.UseShellExecute)
            return;

        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var appDir = Environment.GetEnvironmentVariable("APPDIR");
        if (string.IsNullOrWhiteSpace(appImage) && string.IsNullOrWhiteSpace(appDir))
            return;

        if (startInfo.EnvironmentVariables.ContainsKey("LD_LIBRARY_PATH"))
        {
            var currentLd = startInfo.EnvironmentVariables["LD_LIBRARY_PATH"];
            if (!string.IsNullOrWhiteSpace(currentLd))
            {
                var appDirPrefixes = BuildAppImageLdPrefixes(appDir);
                var filtered = currentLd
                    .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(path =>
                        !string.IsNullOrWhiteSpace(path) &&
                        !IsAppImageInjectedLdSegment(path, appDirPrefixes))
                    .ToArray();

                if (filtered.Length == 0)
                    startInfo.EnvironmentVariables.Remove("LD_LIBRARY_PATH");
                else
                    startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] = string.Join(':', filtered);
            }
        }
        
        // Prevent AppImage-bundled VLC plugins from being forced into external processes.
        if (startInfo.EnvironmentVariables.ContainsKey("VLC_PLUGIN_PATH"))
            startInfo.EnvironmentVariables.Remove("VLC_PLUGIN_PATH");
    }

    private static bool IsRunningInsideAppImageRuntime()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPDIR"));
    }

    private static void SanitizeFlatpakPortableEnvironment(ProcessStartInfo startInfo, string? launchArgsHint = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        if (!IsFlatpakLaunch(startInfo, launchArgsHint))
            return;

        var portableHomeRoot = NormalizePathForComparison(Path.Combine(AppPaths.DataRoot, "Home"));
        if (string.IsNullOrWhiteSpace(portableHomeRoot))
            return;

        RemoveIfPortableXdgPath(startInfo, "XDG_CONFIG_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "XDG_DATA_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "XDG_CACHE_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "XDG_STATE_HOME", portableHomeRoot);
    }

    private static bool IsFlatpakLaunch(ProcessStartInfo startInfo, string? launchArgsHint)
    {
        var fileName = Path.GetFileName(startInfo.FileName)?.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (string.Equals(fileName, "flatpak", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(fileName, "env", StringComparison.OrdinalIgnoreCase))
            return false;

        var commandToken = TryGetFirstExecutableTokenFromEnvArgs(startInfo.Arguments ?? launchArgsHint);
        if (string.IsNullOrWhiteSpace(commandToken))
            return false;

        var token = Path.GetFileName(commandToken.Trim('"', '\''));
        return string.Equals(token, "flatpak", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveIfPortableXdgPath(ProcessStartInfo startInfo, string key, string portableHomeRoot)
    {
        if (!startInfo.EnvironmentVariables.ContainsKey(key))
            return;

        var value = startInfo.EnvironmentVariables[key];
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalizedValue = NormalizePathForComparison(value);
        if (normalizedValue.Equals(portableHomeRoot, StringComparison.Ordinal) ||
            normalizedValue.StartsWith(portableHomeRoot + "/", StringComparison.Ordinal))
        {
            startInfo.EnvironmentVariables.Remove(key);
        }
    }

    private static void SanitizeStorePortableEnvironment(
        ProcessStartInfo startInfo,
        string? launchArgsHint,
        bool forceStoreCompatSanitization = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        if (!forceStoreCompatSanitization &&
            !IsStoreLaunch(startInfo, launchArgsHint) &&
            !IsSteamCompatLaunch(startInfo, launchArgsHint))
        {
            return;
        }

        var portableHomeRoot = NormalizePathForComparison(Path.Combine(AppPaths.DataRoot, "Home"));
        if (string.IsNullOrWhiteSpace(portableHomeRoot))
            return;

        RemoveIfPortableXdgPath(startInfo, "XDG_CONFIG_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "XDG_DATA_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "XDG_CACHE_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "XDG_STATE_HOME", portableHomeRoot);
        RemoveIfPortableXdgPath(startInfo, "DOTNET_CLI_HOME", portableHomeRoot);

        if (!startInfo.EnvironmentVariables.ContainsKey("HOME"))
            return;

        var homeValue = startInfo.EnvironmentVariables["HOME"];
        if (string.IsNullOrWhiteSpace(homeValue))
            return;

        var normalizedHome = NormalizePathForComparison(homeValue);
        if (!normalizedHome.Equals(portableHomeRoot, StringComparison.Ordinal) &&
            !normalizedHome.StartsWith(portableHomeRoot + "/", StringComparison.Ordinal))
        {
            return;
        }

        var realHome = EnvironmentPathHelper.TryGetRealUserHomePath();
        if (!string.IsNullOrWhiteSpace(realHome))
            startInfo.EnvironmentVariables["HOME"] = realHome;
        else
            startInfo.EnvironmentVariables.Remove("HOME");
    }

    private static bool IsStoreLaunch(ProcessStartInfo startInfo, string? launchArgsHint)
    {
        var fileName = Path.GetFileName(startInfo.FileName)?.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (IsStoreCommandToken(fileName))
            return true;

        if (string.Equals(fileName, "xdg-open", StringComparison.OrdinalIgnoreCase))
            return LooksLikeStoreUri(launchArgsHint);

        if (!string.Equals(fileName, "env", StringComparison.OrdinalIgnoreCase))
            return false;

        var commandToken = TryGetFirstExecutableTokenFromEnvArgs(startInfo.Arguments ?? launchArgsHint);
        if (string.IsNullOrWhiteSpace(commandToken))
            return false;

        return IsStoreCommandToken(commandToken);
    }

    private static bool IsStoreCommandToken(string token)
    {
        var executable = Path.GetFileName(token.Trim('"', '\''));
        return string.Equals(executable, "steam", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(executable, "heroic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeStoreUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return trimmed.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("heroic://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("gog://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("epic://", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsSteamCompatLaunch(ProcessStartInfo startInfo, string? launchArgsHint)
    {
        var fileName = Path.GetFileName(startInfo.FileName)?.Trim();
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (IsSteamCompatCommandToken(fileName))
            return true;

        if (string.Equals(fileName, "env", StringComparison.OrdinalIgnoreCase))
        {
            var commandToken = TryGetFirstExecutableTokenFromEnvArgs(startInfo.Arguments ?? launchArgsHint);
            if (!string.IsNullOrWhiteSpace(commandToken))
                return IsSteamCompatCommandToken(commandToken);
        }

        // Wrapper case: e.g. "gamemoderun umu-run ...".
        var firstArgToken = SplitCommandLinePreservingArgs(startInfo.Arguments ?? launchArgsHint ?? string.Empty).FileName;
        return IsSteamCompatCommandToken(firstArgToken);
    }

    private static bool IsSteamCompatCommandToken(string token)
    {
        var executable = Path.GetFileName(token.Trim('"', '\''));
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        if (executable.StartsWith("umu", StringComparison.OrdinalIgnoreCase))
            return true;

        return executable.Contains("proton", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildAppImageLdPrefixes(string? appDir)
    {
        if (string.IsNullOrWhiteSpace(appDir))
            return Array.Empty<string>();

        return
        [
            NormalizePathForComparison(Path.Combine(appDir, "usr", "lib", "vlc", "lib")),
            NormalizePathForComparison(Path.Combine(appDir, "usr", "lib"))
        ];
    }

    private static bool IsAppImageInjectedLdSegment(string segment, IReadOnlyList<string> appDirPrefixes)
    {
        var normalizedSegment = NormalizePathForComparison(segment);
        if (string.IsNullOrWhiteSpace(normalizedSegment))
            return false;

        foreach (var prefix in appDirPrefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                continue;

            if (normalizedSegment.Equals(prefix, StringComparison.Ordinal))
                return true;

            if (normalizedSegment.StartsWith(prefix + "/", StringComparison.Ordinal))
                return true;
        }

        // Fallback for AppImage runtimes where APPDIR is missing but mount paths leaked.
        if (normalizedSegment.StartsWith("/tmp/.mount_", StringComparison.Ordinal))
        {
            if (normalizedSegment.Contains("/usr/lib/vlc/lib", StringComparison.Ordinal))
                return true;

            if (normalizedSegment.EndsWith("/usr/lib", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string NormalizePathForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var value = path.Replace('\\', '/').Trim();
        while (value.EndsWith("/", StringComparison.Ordinal))
            value = value[..^1];

        return value;
    }

    private static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
    }

    private static (string FileName, string Args) SplitCommandLinePreservingArgs(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (string.Empty, string.Empty);

        int i = 0;
        while (i < commandLine.Length && char.IsWhiteSpace(commandLine[i]))
            i++;

        if (i >= commandLine.Length)
            return (string.Empty, string.Empty);

        bool inQuotes = false;
        char quoteChar = '"';
        var fileName = new System.Text.StringBuilder();

        for (; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                    continue;
                }

                fileName.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
                break;

            fileName.Append(c);
        }

        var args = i < commandLine.Length
            ? commandLine[i..].TrimStart()
            : string.Empty;

        return (fileName.ToString(), args);
    }

    private static string BuildNativeArguments(string? templateArgs)
    {
        if (string.IsNullOrWhiteSpace(templateArgs))
            return string.Empty;

        // For direct native execution the executable is already FileName, so "{file}" is a marker and removed.
        var args = templateArgs;
        args = args.Replace("\"{file}\"", string.Empty, StringComparison.Ordinal);
        args = args.Replace("{file}", string.Empty, StringComparison.Ordinal);
        return LaunchArgumentHelper.NormalizeWhitespace(args);
    }

    private void ConfigureWinePrefix(
        MediaItem item,
        List<string>? nodePath,
        ProcessStartInfo startInfo,
        bool isProton,
        bool isUmu)
    {
        string? prefixPath = null;
        string? relativePrefixPathToSave = null;

        // Prefix base folder on library/app level (portable).
        // Library/Prefixes/<itemId_Title>
        var prefixesBaseRel = "Prefixes";

        // Priority 1: Existing saved path (relative to library root).
        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
        {
            var storedPath = item.PrefixPath.Trim();
            if (_settings.PreferPortableLaunchPaths)
            {
                var portableStoredPath =
                    PrefixPathHelper.ConvertPathToLibraryRelativeIfInsideLibraryRoot(storedPath, _libraryRootPath);
                if (!string.Equals(portableStoredPath, storedPath, StringComparison.Ordinal))
                {
                    storedPath = portableStoredPath ?? storedPath;
                    relativePrefixPathToSave = storedPath;
                }
            }

            prefixPath = Path.IsPathRooted(storedPath)
                ? Path.GetFullPath(storedPath)
                : Path.Combine(_libraryRootPath, storedPath);
        }
        else
        {
            // Priority 2: Stable, human-friendly per-item folder.
            var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(item.Title);

            // Keep both: stable id + readable title
            // Example: Prefixes/123e4567-e89b-12d3-a456-426614174000_My_Game
            var folderName = $"{item.Id}_{safeTitle}";

            relativePrefixPathToSave = Path.Combine(prefixesBaseRel, folderName);
            prefixPath = Path.Combine(_libraryRootPath, relativePrefixPathToSave);
        }

        if (string.IsNullOrWhiteSpace(prefixPath))
            return;

        var prefixRoot = prefixPath;
        var winePrefixPath = prefixPath;
        var launchWinePrefixPath = prefixPath;

        if (isUmu)
        {
            // UMU expects WINEPREFIX to be the compat root; it will create <root>/pfx as a symlink.
            string pfxPath;
            if (PrefixPathHelper.IsPfxPath(prefixPath))
            {
                pfxPath = prefixPath;
                var parent = Directory.GetParent(prefixPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    prefixRoot = parent;
                }
            }
            else
            {
                pfxPath = Path.Combine(prefixPath, "pfx");
            }

            var rootInitialized = PrefixPathHelper.IsWinePrefixInitialized(prefixRoot);
            var pfxInitialized = PrefixPathHelper.IsWinePrefixInitialized(pfxPath);
            winePrefixPath = rootInitialized && !pfxInitialized ? prefixRoot : pfxPath;
            launchWinePrefixPath = prefixRoot;
        }
        else if (isProton)
        {
            // Proton/UMU typically use "<prefix>/pfx" as the actual Wine prefix.
            // For legacy prefixes that already have a root drive_c (and no pfx),
            // keep using the root to avoid "losing" settings/installations.
            if (PrefixPathHelper.IsPfxPath(prefixPath))
            {
                winePrefixPath = prefixPath;
                var parent = Directory.GetParent(prefixPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    prefixRoot = parent;
            }
            else
            {
                var pfxPath = Path.Combine(prefixPath, "pfx");
                var rootInitialized = PrefixPathHelper.IsWinePrefixInitialized(prefixPath);
                var pfxInitialized = PrefixPathHelper.IsWinePrefixInitialized(pfxPath);

                if (rootInitialized && !pfxInitialized)
                {
                    winePrefixPath = prefixPath;
                }
                else
                {
                    winePrefixPath = pfxPath;
                }
            }

            launchWinePrefixPath = winePrefixPath;
        }
        else
        {
            // Wine: prefer an existing root prefix, but fall back to "<prefix>/pfx" if present.
            if (PrefixPathHelper.IsPfxPath(prefixPath))
            {
                winePrefixPath = prefixPath;
                var parent = Directory.GetParent(prefixPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    prefixRoot = parent;
            }
            else
            {
                var driveC = Path.Combine(prefixPath, "drive_c");
                if (!Directory.Exists(driveC))
                {
                    var pfxDir = Path.Combine(prefixPath, "pfx");
                    var pfxDriveC = Path.Combine(pfxDir, "drive_c");
                    if (Directory.Exists(pfxDriveC) || Directory.Exists(pfxDir))
                        winePrefixPath = pfxDir;
                }
            }

            launchWinePrefixPath = winePrefixPath;
        }

        // Ensure basic prefix structure
        Directory.CreateDirectory(prefixRoot);

        // For UMU we avoid pre-creating "<root>/pfx" (owned by umu-run),
        // but still scaffold compat-root dosdevices so portable drive mappings remain available.
        var scaffoldPrefixPath =
            (isUmu && !string.Equals(winePrefixPath, prefixRoot, StringComparison.OrdinalIgnoreCase))
                ? prefixRoot
                : winePrefixPath;

        Directory.CreateDirectory(scaffoldPrefixPath);

        var dosDevicesDir = Path.Combine(scaffoldPrefixPath, "dosdevices");
        Directory.CreateDirectory(dosDevicesDir);
        
        // If UMU is expected to materialize/use "<compat-root>/pfx", avoid turning the compat
        // root into a full classic Wine prefix (drive_c/c:). Keep only dosdevices for portable drives.
        var isUmuCompatRootScaffold =
            isUmu &&
            string.Equals(scaffoldPrefixPath, prefixRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(winePrefixPath, prefixRoot, StringComparison.OrdinalIgnoreCase);

        if (!isUmuCompatRootScaffold)
        {
            // drive_c + c: mapping for regular Wine-style prefixes.
            var driveCPath = Path.Combine(scaffoldPrefixPath, "drive_c");
            Directory.CreateDirectory(driveCPath);
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDir, "c:", "../drive_c");
        }
        
        var libraryRoot = Path.GetFullPath(_libraryRootPath); // .../Library
        var prefixFull = Path.GetFullPath(prefixRoot);
        var libraryRootWithSep = libraryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? libraryRoot
            : libraryRoot + Path.DirectorySeparatorChar;

        var isPrefixInsideLibrary =
            prefixFull.StartsWith(libraryRootWithSep, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(prefixFull, libraryRoot, StringComparison.OrdinalIgnoreCase);

        if (isPrefixInsideLibrary)
        {
            var gamesRoot = Path.Combine(libraryRoot, "Games");
            Directory.CreateDirectory(gamesRoot);

            var relativeTarget = Path.GetRelativePath(dosDevicesDir, gamesRoot);
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDir, "d:", relativeTarget);
        }
        
        if (isProton)
            startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = prefixRoot;

        // Apply WINEPREFIX to the launched process
        startInfo.EnvironmentVariables["WINEPREFIX"] = launchWinePrefixPath;

        // Persist generated relative path (portable).
        if (relativePrefixPathToSave != null)
            item.PrefixPath = relativePrefixPathToSave;

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

    private static string BuildArgumentsString(string? filePath, string? templateArgs)
    {
        var fullPath = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetFullPath(filePath);

        // The caller can use additional placeholders, which we derive directly from the path:
        // - {fileDir}  -> Directory (without a trailing slash)
        // - {fileName} -> Filename with extension
        // - {fileBase} -> Filename without extension (e.g., ROM shortname for MAME)
        var fileDir = string.Empty;
        var fileName = string.Empty;
        var fileBase = string.Empty;
        
        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            fileDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            fileName = Path.GetFileName(fullPath);
            fileBase = string.IsNullOrEmpty(fileName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(fileName);
        }

        // If no template is specified, we only return the (possibly quoted) path.
        if (string.IsNullOrWhiteSpace(templateArgs))
        {
            if (string.IsNullOrEmpty(fullPath))
                return string.Empty;

            return fullPath.Contains(' ', StringComparison.Ordinal)
                ? $"\"{fullPath}\""
                : fullPath;
        }

        static string QuoteIfNeededOrEmpty(string value)
            => string.IsNullOrEmpty(value)
                ? string.Empty
                : (value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value);

        static string ReplacePlaceholder(string input, string name, string rawValue)
        {
            var explicitToken = $"\"{{{name}}}\"";
            if (input.Contains(explicitToken, StringComparison.Ordinal))
                return input.Replace($"{{{name}}}", rawValue, StringComparison.Ordinal);

            var quotedValue = QuoteIfNeededOrEmpty(rawValue);
            return input.Replace($"{{{name}}}", quotedValue, StringComparison.Ordinal);
        }

        // Replace placeholders with proper quoting, preserving explicit quotes if provided by the user.
        var result = templateArgs;
        result = ReplacePlaceholder(result, "fileDir", fileDir);
        result = ReplacePlaceholder(result, "fileName", fileName);
        result = ReplacePlaceholder(result, "fileBase", fileBase);
        result = ReplacePlaceholder(result, "file", fullPath);

        return result;
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
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

    private static Task<ProcessWatchOutcome> WatchProcessByNameAsync(
        string processName,
        bool wasRunningBeforeLaunch,
        CancellationToken cancellationToken)
        => WatchProcessByNameAsync(
            processName,
            wasRunningBeforeLaunch,
            WatchProcessStartupTimeout,
            WatchProcessStartupPollInterval,
            cancellationToken);

    internal static async Task<ProcessWatchOutcome> WatchProcessByNameAsync(
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

    private static string GetWatchProcessName(string processName)
        => Path.GetFileNameWithoutExtension(processName);

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

    private static void UpdateStats(MediaItem item, TimeSpan sessionTime)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
        item.TotalPlayTime += sessionTime;
    }

    private sealed class ProcessOutputCapture : IDisposable
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

        private ProcessOutputCapture(Process process)
        {
            _process = process;
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;
        }

        public static ProcessOutputCapture? TryStart(Process? process)
        {
            if (process == null ||
                !process.StartInfo.RedirectStandardOutput ||
                !process.StartInfo.RedirectStandardError)
            {
                return null;
            }

            var capture = new ProcessOutputCapture(process);
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

            AppendTail(_standardOutput, args.Data);
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
            {
                _errorCompleted.TrySetResult();
                return;
            }

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
}
