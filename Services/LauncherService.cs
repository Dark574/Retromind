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
        bool usePlaylistForMultiDisc = false,
        CancellationToken cancellationToken = default)
    {
        if (item == null) return;

        Process? process = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            process = item.MediaType == MediaType.Command
                ? LaunchCommand(item)
                : LaunchNativeOrEmulator(item, inheritedConfig, nodePath, nativeWrappers, usePlaylistForMultiDisc);

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

            return Process.Start(psi);
        }

        // Otherwise treat as executable command.
        return Process.Start(new ProcessStartInfo
        {
            FileName = target,
            Arguments = item.LauncherArgs ?? string.Empty,
            UseShellExecute = true
        });
    }

    private Process? LaunchNativeOrEmulator(
        MediaItem item,
        EmulatorConfig? inheritedConfig,
        List<string>? nodePath,
        IReadOnlyList<LaunchWrapper>? nativeWrappers,
        bool usePlaylistForMultiDisc)
    {
        try
        {
            var (fileName, args, useShellExecute, launchFilePath) =
                ResolveLaunchPlan(item, inheritedConfig, nodePath, nativeWrappers, usePlaylistForMultiDisc);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = useShellExecute
            };

            startInfo.WorkingDirectory = ResolveWorkingDirectory(fileName, launchFilePath);

            // Linux-only project: WINEPREFIX is only applied when explicitly requested.
            // Rules:
            // - If the item already has PrefixPath -> always apply (Native wrappers included).
            // - If launched via an emulator profile with UsesWinePrefix=true -> auto-create/apply.
            var shouldApplyPrefix =
                !string.IsNullOrWhiteSpace(item.PrefixPath) ||
                (item.MediaType == MediaType.Emulator && inheritedConfig?.UsesWinePrefix == true);

            if (shouldApplyPrefix)
                ConfigureWinePrefix(item, nodePath, startInfo);

            startInfo.Arguments = args ?? string.Empty;
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

            // NOTE: Prefix decision is handled in LaunchNativeOrEmulator via shouldApplyPrefix.
            return (item.LauncherPath, args, UseShellExecute: false, LaunchFilePath: launchFilePath);
        }

        // 2) Inherited emulator profile
        if (inheritedConfig != null)
        {
            var templateArgs = CombineTemplateArguments(inheritedConfig.Arguments, item.LauncherArgs);
            var args = BuildArgumentsString(launchFilePath, templateArgs);

            // NOTE: Prefix decision is handled in LaunchNativeOrEmulator via shouldApplyPrefix.
            return (inheritedConfig.Path, args, UseShellExecute: false, LaunchFilePath: launchFilePath);
        }

        // 3) Native execution (direct or via wrappers)
        if (string.IsNullOrWhiteSpace(launchFilePath))
            throw new InvalidOperationException("MediaItem.Files must contain at least one valid file for native execution.");

        var nativeArgs = BuildNativeArguments(item.LauncherArgs);

        // Apply wrapper chain if provided and non-empty
        if (nativeWrappers is { Count: > 0 })
        {
            var folded = FoldWrappers(launchFilePath, nativeArgs, nativeWrappers);
            return (folded.FileName, folded.Args, UseShellExecute: folded.UseShellExecute, LaunchFilePath: launchFilePath);
        }

        // Direct native
        var useShellExecute = true;

        // For shell scripts, direct execution with a working directory is often more reliable.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            launchFilePath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            useShellExecute = false;
        }

        return (launchFilePath, nativeArgs, UseShellExecute: useShellExecute, LaunchFilePath: launchFilePath);
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
            var nodeFolder = Path.Combine(_libraryRootPath, Path.Combine(nodePath.ToArray()));
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
                if (f.Kind != MediaFileKind.Absolute)
                    continue;

                if (string.IsNullOrWhiteSpace(f.Path))
                    continue;

                lines.Add(f.Path);
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

    private static string? ResolveWorkingDirectory(string fileName, string? launchFilePath)
    {
        // Prefer launcher directory ONLY if it's a real file path.
        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            return Path.GetDirectoryName(fileName) ?? string.Empty;

        // Fallback to media file directory.
        if (!string.IsNullOrWhiteSpace(launchFilePath) && File.Exists(launchFilePath))
            return Path.GetDirectoryName(launchFilePath) ?? string.Empty;

        return string.Empty;
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

    private static (string FileName, string Args, bool UseShellExecute) FoldWrappers(
        string executablePath,
        string nativeArgs,
        IReadOnlyList<LaunchWrapper> wrappers)
    {
        // We interpret wrapper order as "outer -> inner".
        // Example: [gamemoderun, mangohud] -> gamemoderun mangohud <exe> <args>
        var currentFile = executablePath;
        var currentArgs = nativeArgs;

        for (int i = wrappers.Count - 1; i >= 0; i--)
        {
            var w = wrappers[i];
            if (string.IsNullOrWhiteSpace(w.Path))
                continue;

            var template = string.IsNullOrWhiteSpace(w.Args) ? "{file}" : w.Args!;
            var child = QuoteIfNeeded(currentFile);

            var argsWithChild = template.Contains("{file}", StringComparison.Ordinal)
                ? template.Replace("{file}", child, StringComparison.Ordinal)
                : $"{template} {child}";

            if (!string.IsNullOrWhiteSpace(currentArgs))
                argsWithChild = $"{argsWithChild} {currentArgs}";

            currentFile = w.Path;
            currentArgs = NormalizeWhitespace(argsWithChild);
        }

        // Wrappers are almost always "native-like"; UseShellExecute true is fine in many cases.
        // For safety on Linux, disable shell execute for .sh wrappers.
        var useShellExecute = true;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            currentFile.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            useShellExecute = false;
        }

        return (currentFile, currentArgs, useShellExecute);
    }

    private static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
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

    private void ConfigureWinePrefix(MediaItem item, List<string>? nodePath, ProcessStartInfo startInfo)
    {
        string? prefixPath = null;
        string? relativePrefixPathToSave = null;

        // Prefix base folder on library/app level (portable).
        // Library/Prefixes/<itemId_Title>
        var prefixesBaseRel = "Prefixes";

        // Priority 1: Existing saved path (relative to library root).
        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
        {
            prefixPath = Path.Combine(_libraryRootPath, item.PrefixPath);
        }
        else
        {
            // Priority 2: Stable, human-friendly per-item folder.
            var safeTitle = SanitizeForPathSegment(item.Title);

            // Keep both: stable id + readable title
            // Example: Prefixes/123e4567-e89b-12d3-a456-426614174000_My_Game
            var folderName = $"{item.Id}_{safeTitle}";

            relativePrefixPathToSave = Path.Combine(prefixesBaseRel, folderName);
            prefixPath = Path.Combine(_libraryRootPath, relativePrefixPathToSave);
        }

        if (string.IsNullOrWhiteSpace(prefixPath))
            return;

        Directory.CreateDirectory(prefixPath);
        startInfo.EnvironmentVariables["WINEPREFIX"] = prefixPath;

        // Persist generated relative path (portable).
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

        // Avoid absurdly long folder names (portable FS sanity).
        const int maxLen = 80;
        if (safe.Length > maxLen)
            safe = safe[..maxLen];

        return safe;
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

        // if there is no time recorded, no need to record something
        if (seconds <= 0)
            return;

        // Above the minimum threshold, we add the measured time.
        // Below it, we only record that the item was started (PlayCount/LastPlayed),
        // but we do not add to TotalPlayTime.
        var effectiveSessionTime = seconds > MinPlayTimeSeconds
            ? elapsed
            : TimeSpan.Zero;

        UpdateStats(item, effectiveSessionTime);
    }

    private static void UpdateStats(MediaItem item, TimeSpan sessionTime)
    {
        item.LastPlayed = DateTime.Now;
        item.PlayCount++;
        item.TotalPlayTime += sessionTime;
    }
}