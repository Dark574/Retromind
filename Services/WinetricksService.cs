using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.Services;

public sealed record WinetricksRequest(
    string PrefixRoot,
    string Verbs,
    IReadOnlyDictionary<string, string> EnvironmentOverrides,
    bool IsProton,
    bool IsUmu,
    string UmuRunnerPath);

internal sealed record WinetricksExecutionPlan(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string CompatRoot,
    string WinePrefix,
    bool UsesUmu,
    string? MissingBundledWinetricksPath);

/// <summary>
/// Prepares Wine/Proton prefixes and executes Winetricks independently from UI state.
/// </summary>
public sealed class WinetricksService
{
    private readonly string _libraryRoot;

    public WinetricksService(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("A library root is required.", nameof(libraryRoot));

        _libraryRoot = Path.GetFullPath(libraryRoot);
    }

    public void PreparePrefix(string prefixRoot, bool isProton, bool isUmu)
    {
        if (string.IsNullOrWhiteSpace(prefixRoot))
            throw new ArgumentException("A prefix root is required.", nameof(prefixRoot));

        var (compatRoot, winePrefix) = ResolvePrefixPaths(prefixRoot, isProton, isUmu);
        Directory.CreateDirectory(compatRoot);
        Directory.CreateDirectory(winePrefix);
        EnsurePortableGamesDriveMapping(winePrefix);
    }

    public bool TryOpenPrefixFolder(string prefixRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(prefixRoot))
                return false;

            Directory.CreateDirectory(prefixRoot);

            var startInfo = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { prefixRoot }
            };

            HostProcessEnvironmentSanitizer.Sanitize(startInfo);
            Process.Start(startInfo)?.Dispose();
            return true;
        }
        catch
        {
            // Opening a folder is a convenience action and must not break the editor.
            return false;
        }
    }

    public async Task RunAsync(
        WinetricksRequest request,
        Action<string> appendLog)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appendLog);

        WinetricksExecutionPlan plan;
        try
        {
            plan = PrepareExecution(request);
        }
        catch (Exception ex)
        {
            appendLog($"Error while preparing Winetricks: {ex.Message}");
            return;
        }

        appendLog($"Prefix: {plan.CompatRoot}");
        if (plan.Environment.TryGetValue("PROTONPATH", out var protonPath))
        {
            appendLog($"PROTONPATH: {protonPath}");
            if (!string.IsNullOrWhiteSpace(plan.MissingBundledWinetricksPath))
            {
                var modeNote = plan.UsesUmu ? "using umu-run winetricks" : "using system winetricks";
                appendLog($"Note: missing {plan.MissingBundledWinetricksPath} ({modeNote})");
            }
        }

        appendLog(plan.UsesUmu ? "Runner: umu-run winetricks" : "Runner: system winetricks");
        if (plan.Environment.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compatPath))
            appendLog($"STEAM_COMPAT_DATA_PATH: {compatPath}");
        if (plan.Environment.TryGetValue("WINEPREFIX", out var winePrefixValue))
            appendLog($"WINEPREFIX: {winePrefixValue}");
        if (!plan.UsesUmu && request.IsProton && plan.Environment.TryGetValue("WINE", out var wineValue))
            appendLog($"WINE: {wineValue}");

        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        if (request.IsProton && !environment.ContainsKey("UMU_LOG"))
        {
            environment["UMU_LOG"] = "debug";
            appendLog("UMU_LOG=debug (verbose winetricks output)");
        }

        var argsText = plan.Arguments.Count > 0 ? string.Join(' ', plan.Arguments) : string.Empty;
        appendLog($"> {plan.FileName} {argsText}".Trim());

        await RunProcessWithLogAsync(
                plan.FileName,
                plan.Arguments,
                environment,
                appendLog)
            .ConfigureAwait(false);
        AppendWinetricksLogSummary(appendLog, plan.WinePrefix);
    }

    internal WinetricksExecutionPlan PrepareExecution(WinetricksRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PrefixRoot))
            throw new ArgumentException("A prefix root is required.", nameof(request));

        var environment = new Dictionary<string, string>(
            request.EnvironmentOverrides,
            StringComparer.Ordinal);
        var useUmu = request.IsUmu;
        string? missingBundledWinetricksPath = null;

        if (request.IsProton &&
            environment.TryGetValue("PROTONPATH", out var protonPath) &&
            !string.IsNullOrWhiteSpace(protonPath))
        {
            var protonWinetricksPath = Path.Combine(protonPath, "protonfixes", "winetricks");
            if (!File.Exists(protonWinetricksPath))
            {
                missingBundledWinetricksPath = protonWinetricksPath;
                useUmu = false;
                ApplyProtonWineFallback(environment, protonPath);
            }
        }

        var (compatRoot, winePrefix) = ResolvePrefixPaths(
            request.PrefixRoot,
            request.IsProton,
            useUmu);

        if (useUmu && request.IsProton)
            EnsureUmuWinetricksDirectory(environment);

        Directory.CreateDirectory(compatRoot);
        Directory.CreateDirectory(winePrefix);
        EnsurePortableGamesDriveMapping(winePrefix);
        ApplyPrefixEnvironment(environment, compatRoot, winePrefix, request.IsProton);

        var arguments = SplitArguments(request.Verbs);
        var fileName = "winetricks";
        if (useUmu)
        {
            fileName = string.IsNullOrWhiteSpace(request.UmuRunnerPath)
                ? "umu-run"
                : request.UmuRunnerPath;
            arguments.Insert(0, "winetricks");
        }

        return new WinetricksExecutionPlan(
            fileName,
            arguments,
            environment,
            compatRoot,
            winePrefix,
            useUmu,
            missingBundledWinetricksPath);
    }

    internal static (string CompatRoot, string WinePrefix) ResolvePrefixPaths(
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
            var pfxDirectory = Path.Combine(prefixRoot, "pfx");
            var pfxDriveC = Path.Combine(pfxDirectory, "drive_c");
            if (Directory.Exists(pfxDriveC) || Directory.Exists(pfxDirectory))
                winePrefix = pfxDirectory;
        }

        return (compatRoot, winePrefix);
    }

    internal static List<string> SplitArguments(string input)
    {
        var arguments = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return arguments;

        var current = new StringBuilder();
        var inQuotes = false;
        var quoteCharacter = '"';

        foreach (var character in input)
        {
            if (inQuotes)
            {
                if (character == quoteCharacter)
                {
                    inQuotes = false;
                    continue;
                }

                current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                inQuotes = true;
                quoteCharacter = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
            arguments.Add(current.ToString());

        return arguments;
    }

    private async Task RunProcessWithLogAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<string> appendLog)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        HostProcessEnvironmentSanitizer.Sanitize(startInfo);

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        foreach (var pair in environmentOverrides)
            startInfo.EnvironmentVariables[pair.Key] = pair.Value;

        try
        {
            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    appendLog(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                    appendLog(eventArgs.Data);
            };

            if (!process.Start())
            {
                appendLog("Failed to start winetricks process.");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(false);
            appendLog($"Exit code: {process.ExitCode}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            appendLog($"Error: executable not found: {fileName}");
            appendLog("Check that winetricks/umu-run is installed and in PATH.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 13)
        {
            appendLog($"Error: permission denied when launching: {fileName}");
        }
        catch (Exception ex)
        {
            appendLog($"Error: {ex.Message}");
        }
    }

    private static void ApplyPrefixEnvironment(
        IDictionary<string, string> environment,
        string compatRoot,
        string winePrefix,
        bool isProton)
    {
        if (isProton)
            environment["STEAM_COMPAT_DATA_PATH"] = compatRoot;

        environment["WINEPREFIX"] = winePrefix;
    }

    private void EnsurePortableGamesDriveMapping(string winePrefix)
    {
        if (string.IsNullOrWhiteSpace(winePrefix))
            return;

        try
        {
            var dosDevicesDirectory = Path.Combine(winePrefix, "dosdevices");
            Directory.CreateDirectory(dosDevicesDirectory);

            var driveCPath = Path.Combine(winePrefix, "drive_c");
            Directory.CreateDirectory(driveCPath);
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDirectory, "c:", "../drive_c");

            var gamesRoot = Path.Combine(_libraryRoot, "Games");
            Directory.CreateDirectory(gamesRoot);

            var relativeTarget = Path.GetRelativePath(dosDevicesDirectory, gamesRoot);
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDirectory, "d:", relativeTarget);
        }
        catch
        {
            // Missing drive mappings must not block prefix operations.
        }
    }

    private static void AppendWinetricksLogSummary(Action<string> appendLog, string winePrefix)
    {
        if (string.IsNullOrWhiteSpace(winePrefix))
            return;

        try
        {
            var logPath = Path.Combine(winePrefix, "winetricks.log");
            if (!File.Exists(logPath))
            {
                appendLog($"winetricks.log not found: {logPath}");
                return;
            }

            var lines = File.ReadAllLines(logPath);
            if (lines.Length == 0)
            {
                appendLog($"winetricks.log is empty: {logPath}");
                return;
            }

            appendLog("winetricks.log:");
            const int maxLines = 50;
            var start = Math.Max(0, lines.Length - maxLines);
            for (var index = start; index < lines.Length; index++)
                appendLog(lines[index]);
        }
        catch (Exception ex)
        {
            appendLog($"Failed to read winetricks.log: {ex.Message}");
        }
    }

    private static void EnsureUmuWinetricksDirectory(IReadOnlyDictionary<string, string> environment)
    {
        if (!environment.TryGetValue("PROTONPATH", out var protonPath) ||
            string.IsNullOrWhiteSpace(protonPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(protonPath, "protonfixes"));
        }
        catch
        {
            // Missing access should not block Winetricks entirely.
        }
    }

    private static void ApplyProtonWineFallback(IDictionary<string, string> environment, string protonPath)
    {
        if (string.IsNullOrWhiteSpace(protonPath))
            return;

        var binDirectory = Path.Combine(protonPath, "files", "bin");
        var wine = Path.Combine(binDirectory, "wine");
        var wineserver = Path.Combine(binDirectory, "wineserver");
        var wine64 = Path.Combine(binDirectory, "wine64");

        if (File.Exists(wine))
            environment["WINE"] = wine;
        if (File.Exists(wineserver))
            environment["WINESERVER"] = wineserver;
        if (File.Exists(wine64))
            environment["WINE64"] = wine64;

        if (!Directory.Exists(binDirectory))
            return;

        const string minimalHostPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        var basePath = environment.TryGetValue("PATH", out var existingPath)
            ? existingPath
            : minimalHostPath;
        environment["PATH"] = string.IsNullOrWhiteSpace(basePath)
            ? binDirectory
            : binDirectory + Path.PathSeparator + basePath;

        // Do not force LD_LIBRARY_PATH/WINEDLLPATH here. Mixing Proton's X11
        // libraries with host drivers can prevent Wine windows from opening.
    }

    private static string GetParentOrSelf(string path)
    {
        var parent = Directory.GetParent(path)?.FullName;
        return string.IsNullOrWhiteSpace(parent) ? path : parent;
    }
}
