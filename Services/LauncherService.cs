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

    private readonly string _libraryRootPath;
    private readonly AppSettings _settings;

    public LauncherService(string libraryRootPath, AppSettings settings)
    {
        _libraryRootPath = libraryRootPath ?? throw new ArgumentNullException(nameof(libraryRootPath));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task LaunchAsync(
        MediaItem item,
        EmulatorConfig? inheritedConfig = null,
        List<string>? nodePath = null,
        IReadOnlyList<LaunchWrapper>? nativeWrappers = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null,
        bool usePlaylistForMultiDisc = false,
        CancellationToken cancellationToken = default)
    {
        if (item == null) return;

        Process? process = null;
        var stopwatch = Stopwatch.StartNew();
        var shouldRecordSession = false;
        var elapsed = TimeSpan.Zero;

        try
        {
            process = item.MediaType == MediaType.Command
                ? LaunchCommand(item, environmentOverrides)
                : LaunchNativeOrEmulator(item, inheritedConfig, nodePath, nativeWrappers, usePlaylistForMultiDisc, environmentOverrides);

            // Tracking strategy:
            // A) If OverrideWatchProcess is set, we track by process name (for launchers like Steam).
            // B) Otherwise, if we have a process handle, wait for it.
            // C) If neither is available (typical for URL commands), we cannot track duration reliably.
            if (!string.IsNullOrWhiteSpace(item.OverrideWatchProcess))
            {
                shouldRecordSession = await WatchProcessByNameAsync(item.OverrideWatchProcess, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (process is { HasExited: false })
            {
                shouldRecordSession = true;
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (process != null)
            {
                // Process started but already exited (very fast failure or immediate exit).
                // Still count as a launch attempt.
                shouldRecordSession = true;
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
            elapsed = stopwatch.Elapsed;
            process?.Dispose();
        }

        if (shouldRecordSession)
            await EvaluateSessionAsync(item, elapsed).ConfigureAwait(false);
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
            return null;

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
            ApplyEnvironmentOverrides(psi, environmentOverrides);

            return Process.Start(psi);
        }

        // Otherwise treat as executable command
        var hasEnvOverrides = environmentOverrides is { Count: > 0 };
        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = item.LauncherArgs ?? string.Empty,
            UseShellExecute = !hasEnvOverrides
        };
        startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, target, launchFilePath: null);
        ApplyEnvironmentOverrides(startInfo, environmentOverrides);
        return Process.Start(startInfo);
    }

    private Process? LaunchNativeOrEmulator(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        List<string>? nodePath,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        bool usePlaylistForMultiDisc,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        try
        {
            var (fileName, args, useShellExecute, launchFilePath) =
                ResolveLaunchPlan(item, inheritedConfig, nodePath, nativeWrappers, usePlaylistForMultiDisc);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName
            };

            startInfo.WorkingDirectory = ResolveWorkingDirectory(item.WorkingDirectory, fileName, launchFilePath);

            // Linux-only project: WINEPREFIX is only applied when explicitly requested
            // Rules:
            // - If the item already has PrefixPath -> always apply (Native wrappers included)
            // - If launched via an emulator profile with UsesWinePrefix=true -> auto-create/apply
            var shouldApplyPrefix =
                !string.IsNullOrWhiteSpace(item.PrefixPath) ||
                (item.MediaType == MediaType.Emulator && inheritedConfig?.UsesWinePrefix == true);

            var hasEnvOverrides =
                (environmentOverrides?.Count ?? 0) > 0 ||
                ((environmentOverrides == null) &&
                 ((inheritedConfig?.EnvironmentOverrides?.Count ?? 0) > 0 ||
                  (item.EnvironmentOverrides?.Count ?? 0) > 0));

            // Ensure env vars + wrapper arguments are honored (shell exec can drop env vars).
            var requiresDirectExec = shouldApplyPrefix ||
                                     hasEnvOverrides ||
                                     (nativeWrappers is { Count: > 0 }) ||
                                     !string.IsNullOrWhiteSpace(args);

            startInfo.UseShellExecute = requiresDirectExec ? false : useShellExecute;

            var prefixInitialized = true;
            if (shouldApplyPrefix)
            {
                var isUmu = IsUmuBased(item, inheritedConfig, nativeWrappers, environmentOverrides);
                var isProton = isUmu || IsProtonBased(item, inheritedConfig, nativeWrappers, environmentOverrides);
                prefixInitialized = ConfigureWinePrefix(item, nodePath, startInfo, isProton, isUmu);
            }

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

            if (shouldApplyPrefix && !prefixInitialized)
                ApplyWineArchOverride(startInfo, item.WineArchOverride);
            
            startInfo.Arguments = args ?? string.Empty;
            LogIfEnvSet(startInfo, "PROTONPATH");
            LogIfEnvSet(startInfo, "STEAM_COMPAT_DATA_PATH");
            LogIfEnvSet(startInfo, "WINEPREFIX");
            // DEBUG: log the exact command-line we are about to run
            Debug.WriteLine($"[Launcher] START: {startInfo.FileName} {startInfo.Arguments}");

            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Failed to launch: {ex.Message}");
            return null;
        }
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

            var fileName = item.LauncherPath;
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
            var templateArgs = CombineTemplateArguments(inheritedConfig.Arguments, item.LauncherArgs);
            var args = BuildArgumentsString(launchFilePath, templateArgs);

            var fileName = inheritedConfig.Path;
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
        var useShell = true;

        // For shell scripts, direct execution with a working directory is often more reliable.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            launchFilePath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            useShell = false;
        }

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

            outerFileName = w.Path.Trim();
            outerArgs = NormalizeWhitespace(argsWithChild);
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
                        resolved = AppPaths.ResolveDataPath(f.Path);
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
    {
        var rawPath = Path.Combine(_libraryRootPath, Path.Combine(nodePath.ToArray()));

        var sanitizedStack = new string[nodePath.Count];
        for (var i = 0; i < nodePath.Count; i++)
        {
            sanitizedStack[i] = PathHelper.SanitizePathSegment(nodePath[i]);
        }
        var sanitizedPath = Path.Combine(_libraryRootPath, Path.Combine(sanitizedStack));

        if (string.Equals(rawPath, sanitizedPath, StringComparison.Ordinal))
            return rawPath;

        // Prefer existing raw paths for backward compatibility.
        if (Directory.Exists(rawPath))
            return rawPath;

        return sanitizedPath;
    }

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

    private static void ApplyWineArchOverride(ProcessStartInfo startInfo, string? wineArchOverride)
    {
        if (string.IsNullOrWhiteSpace(wineArchOverride))
            return;

        var normalized = wineArchOverride.Trim().ToLowerInvariant();
        if (normalized != "win32" && normalized != "win64")
            return;

        startInfo.EnvironmentVariables["WINEARCH"] = normalized;
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
        if (string.IsNullOrWhiteSpace(templateArgs))
            return string.Empty;

        // For direct native execution the executable is already FileName, so "{file}" is a marker and removed.
        var args = templateArgs;
        args = args.Replace("\"{file}\"", string.Empty, StringComparison.Ordinal);
        args = args.Replace("{file}", string.Empty, StringComparison.Ordinal);
        return NormalizeWhitespace(args);
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Simple, allocation-light normalization.
        // (Avoid Regex here; this can run often during preview/launch.)
        Span<char> buffer = stackalloc char[value.Length];
        int w = 0;
        bool lastWasSpace = false;

        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (lastWasSpace) continue;
                buffer[w++] = ' ';
                lastWasSpace = true;
            }
            else
            {
                buffer[w++] = c;
                lastWasSpace = false;
            }
        }

        // Trim one leading/trailing space if present
        int start = 0;
        int length = w;

        if (length > 0 && buffer[0] == ' ')
        {
            start++;
            length--;
        }
        if (length > 0 && buffer[start + length - 1] == ' ')
        {
            length--;
        }

        return length <= 0 ? string.Empty : new string(buffer.Slice(start, length));
    }

    private bool ConfigureWinePrefix(
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
            return true;

        var prefixRoot = prefixPath;
        var winePrefixPath = prefixPath;

        if (isUmu)
        {
            // UMU expects WINEPREFIX to be the compat root; it will create <root>/pfx as a symlink.
            if (PrefixPathHelper.IsPfxPath(prefixPath))
            {
                var parent = Directory.GetParent(prefixPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    prefixRoot = parent;
                    winePrefixPath = parent;
                }
            }
            else
            {
                winePrefixPath = prefixPath;
            }
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
        }

        var prefixInitialized = PrefixPathHelper.IsWinePrefixInitialized(winePrefixPath);

        // Ensure basic prefix structure
        Directory.CreateDirectory(prefixRoot);
        Directory.CreateDirectory(winePrefixPath);
        
        // drive_c + dosdevices are needed so we can add an additional portable D: drive.
        var driveCPath    = Path.Combine(winePrefixPath, "drive_c");
        var dosDevicesDir = Path.Combine(winePrefixPath, "dosdevices");
        
        Directory.CreateDirectory(driveCPath);
        Directory.CreateDirectory(dosDevicesDir);
        
        // Ensure C: mapping (relative to dosdevices)
        //   c: -> ../drive_c
        EnsureDosDeviceMapping(dosDevicesDir, "c:", "../drive_c");
        
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
            EnsureDosDeviceMapping(dosDevicesDir, "d:", relativeTarget);
        }
        
        if (isProton)
            startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = prefixRoot;

        // Apply WINEPREFIX to the launched process
        startInfo.EnvironmentVariables["WINEPREFIX"] = winePrefixPath;

        // Persist generated relative path (portable).
        if (relativePrefixPathToSave != null)
            item.PrefixPath = relativePrefixPathToSave;

        return prefixInitialized;
    }

    private static bool IsProtonBased(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (environmentOverrides is { Count: > 0 } && HasProtonHints(environmentOverrides))
            return true;

        if (environmentOverrides == null &&
            (HasProtonHints(inheritedConfig?.EnvironmentOverrides) ||
             HasProtonHints(item.EnvironmentOverrides)))
        {
            return true;
        }

        if (ContainsProtonToken(item.LauncherPath) ||
            ContainsProtonToken(inheritedConfig?.Path))
        {
            return true;
        }

        return nativeWrappers != null &&
               nativeWrappers.Any(w => ContainsProtonToken(w.Path));
    }

    private static bool HasProtonHints(IReadOnlyDictionary<string, string>? env)
        => env != null && env.Keys.Any(k =>
            string.Equals(k, "PROTONPATH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(k, "STEAM_COMPAT_DATA_PATH", StringComparison.OrdinalIgnoreCase));

    private static bool IsUmuBased(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        IReadOnlyDictionary<string, string>? environmentOverrides)
    {
        if (environmentOverrides is { Count: > 0 } && HasUmuHints(environmentOverrides))
            return true;

        if (environmentOverrides == null &&
            (HasUmuHints(inheritedConfig?.EnvironmentOverrides) ||
             HasUmuHints(item.EnvironmentOverrides)))
        {
            return true;
        }

        if (ContainsUmuToken(item.LauncherPath) ||
            ContainsUmuToken(inheritedConfig?.Path))
        {
            return true;
        }

        return nativeWrappers != null &&
               nativeWrappers.Any(w => ContainsUmuToken(w.Path));
    }

    private static bool HasUmuHints(IReadOnlyDictionary<string, string>? env)
        => env != null && env.Keys.Any(k =>
            k.StartsWith("UMU_", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsUmuToken(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains("umu", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsProtonToken(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           path.Contains("proton", StringComparison.OrdinalIgnoreCase);


    

    /// <summary>
    /// Ensures a dosdevices mapping like "d:" -> "../../Games" exists.
    /// On Linux this is implemented as a symbolic link, which is what Wine/Proton expect.
    /// Existing mappings are left untouched.
    /// </summary>
    private static void EnsureDosDeviceMapping(string dosDevicesDir, string driveName, string relativeTarget)
    {
        if (string.IsNullOrWhiteSpace(driveName))
            throw new ArgumentException("Drive name must not be empty.", nameof(driveName));

        if (!driveName.EndsWith(":", StringComparison.Ordinal))
            throw new ArgumentException("Drive name must end with ':' (e.g. 'd:').", nameof(driveName));

        Directory.CreateDirectory(dosDevicesDir);

        var linkPath    = Path.Combine(dosDevicesDir, driveName);
        var targetValue = relativeTarget.Replace('\\', '/'); // Wine/Proton typically use Unix-style paths here

        // If there is already something at this path, we assume the user knows what they are doing
        // and do not override it automatically.
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
            return;

        try
        {
            #if NET6_0_OR_GREATER
            // .NET on Linux can create directory symlinks directly.
            File.CreateSymbolicLink(linkPath, targetValue);
            #else
            // Fallback: call ln -s on Linux.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return;

            var psi = new ProcessStartInfo
            {
                FileName               = "ln",
                ArgumentList           = { "-s", targetValue, linkPath },
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            #endif
        }
        catch (Exception ex)
        {
            // Best-effort only – prefix wiring must not crash the launcher.
            Debug.WriteLine($"[Launcher] Failed to create dosdevices mapping {driveName} -> {relativeTarget}: {ex.Message}");
        }
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
        
        // First, replace all the extra placeholders
        var result = templateArgs
            .Replace("{fileDir}", fileDir, StringComparison.Ordinal)
            .Replace("{fileName}", fileName, StringComparison.Ordinal)
            .Replace("{fileBase}", fileBase, StringComparison.Ordinal);

        // If the user provided explicit quotes like "\"{file}\"", preserve exact quoting behavior
        if (result.Contains("\"{file}\"", StringComparison.Ordinal))
        {
            return result.Replace("{file}", fullPath, StringComparison.Ordinal);
        }

        // Standard case: {file} is replaced – if present – with a possibly quoted path
        var quotedPath = (!string.IsNullOrEmpty(fullPath) && fullPath.Contains(' ', StringComparison.Ordinal))
            ? $"\"{fullPath}\""
            : fullPath;
        
        return result.Replace("{file}", quotedPath, StringComparison.Ordinal);
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

    private static async Task<bool> WatchProcessByNameAsync(string processName, CancellationToken cancellationToken)
    {
        var cleanName = Path.GetFileNameWithoutExtension(processName);

        var startupTimeout = TimeSpan.FromMinutes(3);
        var startWatch = Stopwatch.StartNew();

        // If the process is already running, do not block waiting for it to exit.
        // This avoids hanging the launch flow when watching long-running launchers (e.g. Steam).
        if (IsProcessRunning(cleanName))
            return false;

        // Phase 1: wait for process to appear.
        while (startWatch.Elapsed < startupTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsProcessRunning(cleanName))
                break;

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        // If not found in time, we cannot track.
        if (!IsProcessRunning(cleanName))
            return false;

        // Phase 2: wait for process to disappear.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

            if (!IsProcessRunning(cleanName))
                break;
        }

        return true;
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

    private static void UpdateStats(MediaItem item, TimeSpan sessionTime)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
        item.TotalPlayTime += sessionTime;
    }
}
