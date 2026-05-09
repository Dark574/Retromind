using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel
{
    private void AddEnvironmentVariable()
    {
        EnvironmentOverrides.Add(new EnvVarRow
        {
            IsInherited = false,
            Source = Strings.Common_SourceItem
        });
    }

    private void RemoveEnvironmentVariable(EnvVarRow? row)
    {
        if (row == null) return;
        EnvironmentOverrides.Remove(row);
    }

    private void AddCustomField()
    {
        CustomFields.Add(new CustomFieldRow());
    }

    private void RemoveCustomField(CustomFieldRow? row)
    {
        if (row == null) return;
        CustomFields.Remove(row);
    }

    private void GeneratePrefix()
    {
        // Only generate if not already set (user might have a custom path)
        if (HasPrefix) return;

        var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(Title);
        var folderName = $"{_originalItem.Id}_{safeTitle}";
        PrefixPath = Path.Combine("Prefixes", folderName);

        try
        {
            var prefixRoot = ResolvePrefixRoot();
            if (string.IsNullOrWhiteSpace(prefixRoot))
                return;

            var env = BuildEffectiveEnvironmentOverrides();
            var isUmu = IsUmuBased(env);
            var isProton = isUmu || IsProtonBased(env);
            var (compatRoot, winePrefix) = ResolvePrefixPathsForWinetricks(prefixRoot, isProton, isUmu);

            Directory.CreateDirectory(compatRoot);
            Directory.CreateDirectory(winePrefix);
            EnsurePortableGamesDriveMapping(winePrefix);
        }
        catch
        {
            // best-effort: generation should still provide the path even if folder creation fails
        }
    }

    private void OpenPrefixFolder()
    {
        try
        {
            if (!HasPrefix) return;

            var folder = Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, PrefixPath));
            Directory.CreateDirectory(folder);

            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { folder }
            };

            HostProcessEnvironmentSanitizer.Sanitize(psi);
            Process.Start(psi)?.Dispose();
        }
        catch
        {
            // best-effort: opening a folder must not break the dialog
        }
    }

    private void ClearPrefix()
    {
        PrefixPath = string.Empty;
    }

    private bool CanRunWinetricks(Window? _)
        => HasPrefix &&
           !IsWinetricksRunning &&
           !string.IsNullOrWhiteSpace(WinetricksVerbs);

    private void SetWinetricksRunning(bool value)
    {
        if (UiThreadHelper.CheckAccess())
            IsWinetricksRunning = value;
        else
            UiThreadHelper.Post(() => IsWinetricksRunning = value);
    }

    private async Task RunWinetricksAsync(Window? owner)
    {
        if (!CanRunWinetricks(owner))
            return;

        var verbs = WinetricksVerbs.Trim();
        var prefixRoot = ResolvePrefixRoot();

        if (string.IsNullOrWhiteSpace(prefixRoot))
            return;

        SetWinetricksRunning(true);

        try
        {
            var env = BuildEffectiveEnvironmentOverrides();
            var isUmu = IsUmuBased(env);
            var isProton = isUmu || IsProtonBased(env);

            foreach (var key in env.Keys.ToList())
                env[key] = EnvironmentPathHelper.NormalizeDataRootPathIfNeeded(key, env[key]);

            var useUmu = isUmu;
            string? protonPathValue = null;
            string? protonWinetricksPath = null;

            if (isProton && env.TryGetValue("PROTONPATH", out protonPathValue) &&
                !string.IsNullOrWhiteSpace(protonPathValue))
            {
                protonWinetricksPath = Path.Combine(protonPathValue, "protonfixes", "winetricks");
                if (!File.Exists(protonWinetricksPath))
                {
                    // Fallback: use host winetricks + Proton wine binaries when
                    // Proton's bundled helper is unavailable.
                    useUmu = false;
                    ApplyProtonWineFallback(env, protonPathValue);
                }
            }

            var (compatRoot, winePrefix) = ResolvePrefixPathsForWinetricks(prefixRoot, isProton, useUmu);

            if (!PrefixPathHelper.IsWinePrefixInitialized(winePrefix))
                ApplyWineArchOverride(env, WineArchSelection);

            if (useUmu && isProton)
                EnsureUmuWinetricksCwd(env);

            Directory.CreateDirectory(compatRoot);
            Directory.CreateDirectory(winePrefix);
            EnsurePortableGamesDriveMapping(winePrefix);

            ApplyPrefixEnvironment(env, compatRoot, winePrefix, isProton);

            var (fileName, arguments) = BuildWinetricksCommand(verbs, useUmu);

            var logVm = new ProcessLogViewModel("Winetricks");
            var logView = new ProcessLogView { DataContext = logVm };

            if (owner != null)
                logView.Show(owner);
            else
                logView.Show();

            AppendLog(logVm, $"Prefix: {compatRoot}");
            if (env.TryGetValue("PROTONPATH", out var protonPathEnv))
            {
                AppendLog(logVm, $"PROTONPATH: {protonPathEnv}");
                if (!string.IsNullOrWhiteSpace(protonWinetricksPath) &&
                    !File.Exists(protonWinetricksPath))
                {
                    var modeNote = useUmu ? "using umu-run winetricks" : "using system winetricks";
                    AppendLog(logVm, $"Note: missing {protonWinetricksPath} ({modeNote})");
                }
            }
            AppendLog(logVm, useUmu ? "Runner: umu-run winetricks" : "Runner: system winetricks");
            if (env.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compatPath))
                AppendLog(logVm, $"STEAM_COMPAT_DATA_PATH: {compatPath}");
            if (env.TryGetValue("WINEPREFIX", out var winePrefixValue))
                AppendLog(logVm, $"WINEPREFIX: {winePrefixValue}");
            if (!useUmu && isProton && env.TryGetValue("WINE", out var wineValue))
                AppendLog(logVm, $"WINE: {wineValue}");

            if (isProton && !env.ContainsKey("UMU_LOG"))
            {
                env["UMU_LOG"] = "debug";
                AppendLog(logVm, "UMU_LOG=debug (verbose winetricks output)");
            }

            var argsText = arguments.Count > 0 ? string.Join(' ', arguments) : string.Empty;
            AppendLog(logVm, $"> {fileName} {argsText}".Trim());

            await RunProcessWithLogAsync(fileName, arguments, env, logVm).ConfigureAwait(false);
            AppendWinetricksLogSummary(logVm, winePrefix);
        }
        finally
        {
            SetWinetricksRunning(false);
        }
    }

    private static async Task RunProcessWithLogAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environmentOverrides,
        ProcessLogViewModel logVm)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Start from a host-compatible baseline (avoid inheriting portable AppImage HOME/XDG by default).
        // Explicit per-item/per-emulator overrides are applied afterwards and can still opt back into portable.
        HostProcessEnvironmentSanitizer.Sanitize(startInfo);

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        foreach (var kv in environmentOverrides)
            startInfo.EnvironmentVariables[kv.Key] = kv.Value;

        try
        {
            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    AppendLog(logVm, e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    AppendLog(logVm, e.Data);
            };

            if (!process.Start())
            {
                AppendLog(logVm, "Failed to start winetricks process.");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync().ConfigureAwait(false);
            AppendLog(logVm, $"Exit code: {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            AppendLog(logVm, $"Error: executable not found: {fileName}");
            AppendLog(logVm, "Check that winetricks/umu-run is installed and in PATH.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 13)
        {
            AppendLog(logVm, $"Error: permission denied when launching: {fileName}");
        }
        catch (Exception ex)
        {
            AppendLog(logVm, $"Error: {ex.Message}");
        }
        finally
        {
            UiThreadHelper.Post(() => logVm.IsRunning = false);
        }
    }

    private static void AppendLog(ProcessLogViewModel logVm, string line)
    {
        UiThreadHelper.Post(() => logVm.AppendLine(line));
    }

    private Dictionary<string, string> BuildEffectiveEnvironmentOverrides()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        var emulator = ResolveSelectedEmulatorConfig();
        if (emulator?.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in emulator.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                env[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(emulator?.DefaultRunnerVersionId))
        {
            RunnerVersionEnvironmentHelper.ApplyRunnerToEnvironment(
                env,
                _settings,
                emulator,
                emulator.DefaultRunnerVersionId);
        }

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = PathHelper.GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            foreach (var node in chain)
            {
                if (node.EnvironmentOverrides == null)
                    continue;

                if (node.EnvironmentOverrides.Count > 0)
                {
                    foreach (var kv in node.EnvironmentOverrides)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key))
                            continue;

                        env[kv.Key.Trim()] = kv.Value ?? string.Empty;
                    }
                }

                break;
            }
        }

        foreach (var row in EnvironmentOverrides)
        {
            if (row.IsInherited)
                continue;

            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            env[row.Key.Trim()] = row.Value ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(SelectedRunnerVersion?.Id))
        {
            RunnerVersionEnvironmentHelper.ApplyRunnerToEnvironment(
                env,
                _settings,
                emulator,
                SelectedRunnerVersion.Id);
        }

        ApplyEmulatorXdgOverridesForPreview(env, emulator);
        ApplyXdgOverridesForPreview(env);

        return env;
    }

    private static void ApplyEmulatorXdgOverridesForPreview(Dictionary<string, string> env, EmulatorConfig? emulator)
    {
        if (emulator == null)
            return;

        switch (emulator.XdgMode)
        {
            case EmulatorConfig.XdgOverrideMode.Inherit:
                return;

            case EmulatorConfig.XdgOverrideMode.Host:
                env.Remove("XDG_CONFIG_HOME");
                env.Remove("XDG_DATA_HOME");
                env.Remove("XDG_CACHE_HOME");
                env.Remove("XDG_STATE_HOME");
                return;

            case EmulatorConfig.XdgOverrideMode.Custom:
                ApplyXdgOverride(env, "XDG_CONFIG_HOME", emulator.XdgConfigPath);
                ApplyXdgOverride(env, "XDG_DATA_HOME", emulator.XdgDataPath);
                ApplyXdgOverride(env, "XDG_CACHE_HOME", emulator.XdgCachePath);
                ApplyXdgOverride(env, "XDG_STATE_HOME", emulator.XdgStatePath);
                return;
        }
    }

    private void ApplyXdgOverridesForPreview(Dictionary<string, string> env)
    {
        // Keep preview behavior aligned with runtime launch behavior:
        // item-level XDG applies to Native + Emulator, but not Command.
        if (MediaType == MediaType.Command)
            return;

        ApplyXdgOverride(env, "XDG_CONFIG_HOME", XdgConfigPath);
        ApplyXdgOverride(env, "XDG_DATA_HOME", XdgDataPath);
        ApplyXdgOverride(env, "XDG_CACHE_HOME", XdgCachePath);
        ApplyXdgOverride(env, "XDG_STATE_HOME", XdgStatePath);
    }

    private static void ApplyXdgOverride(
        Dictionary<string, string> env,
        string key,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var resolved = Path.IsPathRooted(value)
            ? value
            : AppPaths.ResolveDataPath(value);

        env[key] = resolved;
    }

    private static void ApplyPrefixEnvironment(
        Dictionary<string, string> env,
        string compatRoot,
        string winePrefix,
        bool isProton)
    {
        if (isProton)
            env["STEAM_COMPAT_DATA_PATH"] = compatRoot;

        env["WINEPREFIX"] = winePrefix;
    }

    private static void ApplyWineArchOverride(Dictionary<string, string> env, WineArchOption selection)
    {
        var value = selection switch
        {
            WineArchOption.Win32 => "win32",
            WineArchOption.Win64 => "win64",
            _ => null
        };

        if (string.IsNullOrWhiteSpace(value))
            return;

        env["WINEARCH"] = value;
    }

    private static void EnsurePortableGamesDriveMapping(string winePrefix)
    {
        if (string.IsNullOrWhiteSpace(winePrefix))
            return;

        try
        {
            var dosDevicesDir = Path.Combine(winePrefix, "dosdevices");
            Directory.CreateDirectory(dosDevicesDir);

            var driveCPath = Path.Combine(winePrefix, "drive_c");
            Directory.CreateDirectory(driveCPath);
            EnsureDosDeviceMapping(dosDevicesDir, "c:", "../drive_c");

            var libraryRoot = Path.GetFullPath(AppPaths.LibraryRoot);
            var gamesRoot = Path.Combine(libraryRoot, "Games");
            Directory.CreateDirectory(gamesRoot);

            var relativeTarget = Path.GetRelativePath(dosDevicesDir, gamesRoot);
            EnsureDosDeviceMapping(dosDevicesDir, "d:", relativeTarget);
        }
        catch
        {
            // best-effort only: missing D: mapping must not block prefix operations
        }
    }

    private static void EnsureDosDeviceMapping(string dosDevicesDir, string driveName, string relativeTarget)
    {
        if (string.IsNullOrWhiteSpace(driveName))
            throw new ArgumentException("Drive name must not be empty.", nameof(driveName));

        if (!driveName.EndsWith(":", StringComparison.Ordinal))
            throw new ArgumentException("Drive name must end with ':' (e.g. 'd:').", nameof(driveName));

        Directory.CreateDirectory(dosDevicesDir);

        var linkPath = Path.Combine(dosDevicesDir, driveName);
        var targetValue = relativeTarget.Replace('\\', '/');

        if (File.Exists(linkPath) || Directory.Exists(linkPath))
            return;

        try
        {
            File.CreateSymbolicLink(linkPath, targetValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Winetricks] Failed to create dosdevices mapping {driveName} -> {relativeTarget}: {ex.Message}");
        }
    }

    private static void AppendWinetricksLogSummary(ProcessLogViewModel logVm, string winePrefix)
    {
        if (string.IsNullOrWhiteSpace(winePrefix))
            return;

        try
        {
            var logPath = Path.Combine(winePrefix, "winetricks.log");
            if (!File.Exists(logPath))
            {
                AppendLog(logVm, $"winetricks.log not found: {logPath}");
                return;
            }

            var lines = File.ReadAllLines(logPath);
            if (lines.Length == 0)
            {
                AppendLog(logVm, $"winetricks.log is empty: {logPath}");
                return;
            }

            AppendLog(logVm, "winetricks.log:");
            const int maxLines = 50;
            var start = Math.Max(0, lines.Length - maxLines);
            for (var i = start; i < lines.Length; i++)
                AppendLog(logVm, lines[i]);
        }
        catch (Exception ex)
        {
            AppendLog(logVm, $"Failed to read winetricks.log: {ex.Message}");
        }
    }

    private static void EnsureUmuWinetricksCwd(Dictionary<string, string> env)
    {
        if (!env.TryGetValue("PROTONPATH", out var protonPath) ||
            string.IsNullOrWhiteSpace(protonPath))
        {
            return;
        }

        try
        {
            var dir = Path.Combine(protonPath, "protonfixes");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            // best-effort: missing access should not block winetricks entirely
        }
    }

    private static (string CompatRoot, string WinePrefix) ResolvePrefixPathsForWinetricks(
        string prefixRoot,
        bool isProton,
        bool isUmu)
    {
        var compatRoot = prefixRoot;
        var winePrefix = prefixRoot;

        if (isUmu)
        {
            if (PrefixPathHelper.IsPfxPath(prefixRoot))
            {
                compatRoot = GetParentOrSelf(prefixRoot);
                winePrefix = compatRoot;
            }
            else
            {
                compatRoot = prefixRoot;
                winePrefix = prefixRoot;
            }

            return (compatRoot, winePrefix);
        }

        if (isProton)
        {
            if (PrefixPathHelper.IsPfxPath(prefixRoot))
            {
                winePrefix = prefixRoot;
                compatRoot = GetParentOrSelf(prefixRoot);
            }
            else
            {
                var pfxPath = Path.Combine(prefixRoot, "pfx");
                var rootInitialized = PrefixPathHelper.IsWinePrefixInitialized(prefixRoot);
                var pfxInitialized = PrefixPathHelper.IsWinePrefixInitialized(pfxPath);

                compatRoot = prefixRoot;
                winePrefix = rootInitialized && !pfxInitialized ? prefixRoot : pfxPath;
            }

            return (compatRoot, winePrefix);
        }

        if (PrefixPathHelper.IsPfxPath(prefixRoot))
        {
            winePrefix = prefixRoot;
            compatRoot = GetParentOrSelf(prefixRoot);
            return (compatRoot, winePrefix);
        }

        var driveC = Path.Combine(prefixRoot, "drive_c");
        if (!Directory.Exists(driveC))
        {
            var pfxDir = Path.Combine(prefixRoot, "pfx");
            var pfxDriveC = Path.Combine(pfxDir, "drive_c");
            if (Directory.Exists(pfxDriveC) || Directory.Exists(pfxDir))
                winePrefix = pfxDir;
        }

        return (compatRoot, winePrefix);
    }

    private static string GetParentOrSelf(string path)
    {
        var parent = Directory.GetParent(path)?.FullName;
        return string.IsNullOrWhiteSpace(parent) ? path : parent;
    }

    private (string FileName, List<string> Arguments) BuildWinetricksCommand(string verbs, bool useUmu)
    {
        var args = SplitArgs(verbs);

        if (useUmu)
        {
            var runner = ResolveUmuRunnerPath();
            args.Insert(0, "winetricks");
            return (runner, args);
        }

        return ("winetricks", args);
    }

    private static void ApplyProtonWineFallback(Dictionary<string, string> env, string protonPath)
    {
        if (string.IsNullOrWhiteSpace(protonPath))
            return;

        var binDir = Path.Combine(protonPath, "files", "bin");
        var wine = Path.Combine(binDir, "wine");
        var wineserver = Path.Combine(binDir, "wineserver");
        var wine64 = Path.Combine(binDir, "wine64");

        if (File.Exists(wine))
            env["WINE"] = wine;
        if (File.Exists(wineserver))
            env["WINESERVER"] = wineserver;
        if (File.Exists(wine64))
            env["WINE64"] = wine64;

        if (Directory.Exists(binDir))
        {
            const string minimalHostPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            var basePath = env.TryGetValue("PATH", out var existingPath)
                ? existingPath
                : minimalHostPath;
            env["PATH"] = string.IsNullOrWhiteSpace(basePath)
                ? binDir
                : binDir + Path.PathSeparator + basePath;
        }

        // Do not force LD_LIBRARY_PATH/WINEDLLPATH here.
        // Mixing Proton-bundled X11 libs with host drivers can break window creation
        // (e.g. xf86vm assertions). Let the selected wine binary manage its runtime libs.
    }

    private string ResolveUmuRunnerPath()
    {
        var candidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            candidate = LauncherPath;

        if (string.IsNullOrWhiteSpace(candidate))
            return "umu-run";

        if (!LaunchRuntimeHelper.ContainsUmuToken(candidate))
            return "umu-run";

        return ResolveExecutablePath(candidate);
    }

    private static string ResolveExecutablePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        if (path.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            path.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return AppPaths.ResolveDataPath(path);
        }

        return path;
    }

    private string ResolvePrefixRoot()
    {
        if (string.IsNullOrWhiteSpace(PrefixPath))
            return string.Empty;

        return Path.IsPathRooted(PrefixPath)
            ? PrefixPath
            : Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, PrefixPath));
    }

    private bool IsProtonBased(Dictionary<string, string> env)
    {
        if (ContainsProtonHints(env))
            return true;

        var pathCandidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            pathCandidate = LauncherPath;

        return !string.IsNullOrWhiteSpace(pathCandidate) &&
               LaunchRuntimeHelper.ContainsProtonToken(pathCandidate);
    }

    private static bool ContainsProtonHints(Dictionary<string, string> env)
        => env.Keys.Any(k =>
            string.Equals(k, "PROTONPATH", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(k, "STEAM_COMPAT_DATA_PATH", StringComparison.OrdinalIgnoreCase));

    private bool IsUmuBased(Dictionary<string, string> env)
    {
        if (ContainsUmuHints(env))
            return true;

        var pathCandidate = ResolveSelectedEmulatorConfig()?.Path;

        if (MediaType == MediaType.Emulator && IsManualEmulator && !string.IsNullOrWhiteSpace(LauncherPath))
            pathCandidate = LauncherPath;

        return !string.IsNullOrWhiteSpace(pathCandidate) &&
               LaunchRuntimeHelper.ContainsUmuToken(pathCandidate);
    }

    private static bool ContainsUmuHints(Dictionary<string, string> env)
        => env.Keys.Any(k => k.StartsWith("UMU_", StringComparison.OrdinalIgnoreCase));

    private static List<string> SplitArgs(string input)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return args;

        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '"';

        foreach (var c in input)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inQuotes = true;
                quoteChar = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
    }
}
