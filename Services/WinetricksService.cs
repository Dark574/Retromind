using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

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
    private readonly IErrorService _errorService;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<string, string[]> _readLog;
    private readonly string? _processLogDirectory;
    private readonly Func<string, Stream>? _openProcessLog;

    public WinetricksService(string libraryRoot, IErrorService errorService)
        : this(libraryRoot, errorService, Process.Start, File.ReadAllLines)
    {
    }

    internal WinetricksService(string libraryRoot, IErrorService errorService,
        Func<ProcessStartInfo, Process?> startProcess, Func<string, string[]>? readLog = null,
        string? processLogDirectory = null, Func<string, Stream>? openProcessLog = null)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("A library root is required.", nameof(libraryRoot));

        _libraryRoot = Path.GetFullPath(libraryRoot);
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
        _readLog = readLog ?? File.ReadAllLines;
        _processLogDirectory = processLogDirectory;
        _openProcessLog = openProcessLog;
    }

    public void PreparePrefix(string prefixRoot, bool isProton, bool isUmu)
    {
        if (string.IsNullOrWhiteSpace(prefixRoot))
            throw new ArgumentException("A prefix root is required.", nameof(prefixRoot));

        var runner = RunnerType(isProton, isUmu);
        try
        {
            var (compatRoot, winePrefix) = ResolvePrefixPaths(prefixRoot, isProton, isUmu);
            Directory.CreateDirectory(compatRoot);
            Directory.CreateDirectory(winePrefix);
            EnsurePortableGamesDriveMapping(compatRoot, winePrefix, runner);
        }
        catch (Exception ex)
        {
            ReportFailure("Winetricks.PreparationFailed", ErrorSeverity.Error, "PreparePrefix", runner, prefixRoot, ex);
            throw; // GeneratePrefix retains its existing best-effort handling.
        }
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

    public async Task<WinetricksResult> RunAsync(
        WinetricksRequest request,
        Action<string> appendLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(appendLog);

        var secrets = GetSensitiveValues(request.EnvironmentOverrides)
            .Concat(GetSensitiveArgumentValues(SplitArguments(request.Verbs)))
            // Process output arrives line by line; also protect fragments of known multiline secrets.
            .SelectMany(value => new[] { value }.Concat(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)))
            .Distinct(StringComparer.Ordinal).ToArray();
        var redactor = new ErrorReportRedactor(new ErrorReport(
            "Winetricks.Output", ErrorSeverity.Diagnostic, nameof(WinetricksService), string.Empty,
            SensitiveValues: secrets));
        var originalAppendLog = appendLog;
        appendLog = line =>
        {
            try { originalAppendLog(redactor.Redact(line) ?? string.Empty); }
            catch { /* A closed log window or failed observer cannot break process monitoring. */ }
        };
        if (cancellationToken.IsCancellationRequested)
            return new WinetricksResult(false, FailureCategory: WinetricksFailureCategory.Cancelled);

        WinetricksExecutionPlan plan;
        try
        {
            plan = PrepareExecution(request);
        }
        catch (Exception ex)
        {
            var message = RedactFailureMessage(redactor, $"Error while preparing Winetricks: {ex.Message}");
            appendLog(message);
            ReportFailure("Winetricks.PreparationFailed", ErrorSeverity.Error, "PrepareExecution",
                RunnerType(request.IsProton, request.IsUmu), request.PrefixRoot, ex, secrets: secrets);
            return new WinetricksResult(false, FailureCategory: WinetricksFailureCategory.Preparation, ErrorMessage: message);
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

        using var processLog = new WinetricksProcessLog(
            _processLogDirectory ?? Path.Combine(AppPaths.DataRoot, "Logs", "Winetricks"),
            RunnerType(request.IsProton, plan.UsesUmu), request.Verbs, plan.WinePrefix, redactor,
            openFile: _openProcessLog);
        var result = await RunProcessWithLogAsync(
                plan.FileName,
                plan.Arguments,
                environment,
                appendLog,
                originalAppendLog,
                exitCode =>
                {
                    // Trusted numeric metadata is not an OAuth "code" credential.
                    // Keep the existing visible exit-code line without generic code-field redaction.
                    try { originalAppendLog($"Exit code: {exitCode}"); }
                    catch { /* A failed observer cannot change the process result. */ }
                },
                RunnerType(request.IsProton, plan.UsesUmu), plan.WinePrefix, redactor, secrets,
                cancellationToken, processLog)
            .ConfigureAwait(false);
        if (processLog.WriteFailure is { } logFailure)
            ReportFailure("Winetricks.LogWriteFailed", ErrorSeverity.Diagnostic, "WriteProcessLog",
                RunnerType(request.IsProton, plan.UsesUmu), plan.WinePrefix, logFailure,
                secrets: secrets, processLog: processLog);
        if (!result.IsCancelled)
            AppendWinetricksLogSummary(appendLog, plan.WinePrefix,
                RunnerType(request.IsProton, plan.UsesUmu), secrets);
        return result;
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
            EnsureUmuWinetricksDirectory(environment, request);

        Directory.CreateDirectory(compatRoot);
        Directory.CreateDirectory(winePrefix);
        EnsurePortableGamesDriveMapping(compatRoot, winePrefix, RunnerType(request.IsProton, useUmu),
            GetSensitiveValues(request.EnvironmentOverrides));
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

    private async Task<WinetricksResult> RunProcessWithLogAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environmentOverrides,
        Action<string> appendLog,
        Action<string> appendProcessOutput,
        Action<int> appendExitCode,
        string runnerType, string winePrefix, ErrorReportRedactor redactor, IReadOnlyCollection<string> secrets,
        CancellationToken cancellationToken, WinetricksProcessLog processLog)
    {
        Process? process = null;
        var started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            cancellationToken.ThrowIfCancellationRequested();
            process = _startProcess(startInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start winetricks process.");
            started = true;

            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    processLog.AppendOutput("stdout", eventArgs.Data, appendProcessOutput);
                }
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    processLog.AppendOutput("stderr", eventArgs.Data, appendProcessOutput);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            appendExitCode(exitCode);
            if (exitCode == 0)
            {
                var success = new WinetricksResult(true, exitCode);
                processLog.Complete(success);
                return success;
            }

            var message = $"Winetricks failed with exit code {exitCode}.";
            appendLog($"[ERROR] {message}");
            var failure = new WinetricksResult(false, exitCode, WinetricksFailureCategory.ProcessExecution, message);
            processLog.Complete(failure);
            ReportFailure("Winetricks.ProcessFailed", ErrorSeverity.Warning, "Execute", runnerType,
                winePrefix, exitCode: exitCode, secrets: secrets, processLog: processLog);
            return failure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync(process, appendLog).ConfigureAwait(false);
            var cancelled = new WinetricksResult(false, TryGetExitCode(process), WinetricksFailureCategory.Cancelled);
            processLog.Complete(cancelled);
            return cancelled;
        }
        catch (Exception ex)
        {
            if (started)
                await StopProcessAsync(process, appendLog).ConfigureAwait(false);
            var message = !started && ex is Win32Exception { NativeErrorCode: 2 }
                ? $"Error: executable not found: {fileName}. Check that winetricks/umu-run is installed and in PATH."
                : !started && ex is Win32Exception { NativeErrorCode: 13 }
                    ? $"Error: permission denied when launching: {fileName}"
                    : $"Error: {ex.Message}";
            message = RedactFailureMessage(redactor, message);
            appendLog(message);
            var exitCode = TryGetExitCode(process);
            var failure = new WinetricksResult(false, exitCode,
                started ? WinetricksFailureCategory.ProcessExecution : WinetricksFailureCategory.ProcessStart, message);
            processLog.Complete(failure);
            ReportFailure(started ? "Winetricks.ProcessFailed" : "Winetricks.StartFailed",
                started ? ErrorSeverity.Warning : ErrorSeverity.Error,
                started ? "MonitorProcess" : "Start", runnerType, winePrefix, ex, exitCode, secrets, processLog);
            return failure;
        }
        finally
        {
            try { process?.Dispose(); }
            catch (Exception ex)
            {
                ReportFailure("Winetricks.ProcessFailed", ErrorSeverity.Warning, "DisposeProcess", runnerType,
                    winePrefix, ex, secrets: secrets, processLog: processLog);
            }
        }
    }

    private static int? TryGetExitCode(Process? process)
    {
        try { return process is { HasExited: true } ? process.ExitCode : null; }
        catch { return null; }
    }

    private static string RedactFailureMessage(ErrorReportRedactor redactor, string message)
    {
        try { return redactor.Redact(message) ?? "Winetricks execution failed."; }
        catch { return "Winetricks execution failed. Diagnostic details could not be sanitized."; }
    }

    private static async Task StopProcessAsync(Process? process, Action<string> appendLog)
    {
        if (process == null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            // Reap the process and drain both redirected streams, but do not hang
            // the editor indefinitely if the host refuses termination/pipe cleanup.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            appendLog($"Warning: could not finish Winetricks process cleanup: {ex.Message}");
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

    private void EnsurePortableGamesDriveMapping(string compatRoot, string winePrefix, string runnerType,
        IReadOnlyCollection<string>? secrets = null)
    {
        if (string.IsNullOrWhiteSpace(winePrefix))
            return;

        Exception? failure = null;
        void CaptureFailure(Exception ex) => failure ??= ex;
        try
        {
            var dosDevicesDirectory = Path.Combine(winePrefix, "dosdevices");
            Directory.CreateDirectory(dosDevicesDirectory);

            var driveCPath = Path.Combine(winePrefix, "drive_c");
            Directory.CreateDirectory(driveCPath);
            PrefixPathHelper.EnsureDosDeviceMapping(dosDevicesDirectory, "c:", "../drive_c", CaptureFailure);

            PrefixPathHelper.EnsurePortableGamesDriveMapping(
                compatRoot,
                dosDevicesDirectory,
                _libraryRoot, CaptureFailure);
        }
        catch (Exception ex)
        {
            // Missing drive mappings must not block prefix operations.
            CaptureFailure(ex);
        }
        if (failure != null)
            ReportFailure("Winetricks.PrefixMappingFailed", ErrorSeverity.Warning, "PrepareDriveMappings",
                runnerType, winePrefix, failure, secrets: secrets);
    }

    private void AppendWinetricksLogSummary(Action<string> appendLog, string winePrefix, string runnerType,
        IReadOnlyCollection<string> secrets)
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

            var lines = _readLog(logPath);
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
            ReportFailure("Winetricks.LogReadFailed", ErrorSeverity.Diagnostic, "ReadLogSummary", runnerType,
                winePrefix, ex, secrets: secrets);
        }
    }

    private void EnsureUmuWinetricksDirectory(IReadOnlyDictionary<string, string> environment, WinetricksRequest request)
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
        catch (Exception ex)
        {
            // Missing access should not block Winetricks entirely.
            ReportFailure("Winetricks.PreparationFailed", ErrorSeverity.Error, "EnsureUmuWinetricksDirectory",
                "UMU", request.PrefixRoot, ex, secrets: GetSensitiveValues(request.EnvironmentOverrides));
        }
    }

    private void ReportFailure(string code, ErrorSeverity severity, string operation, string runnerType,
        string prefixPath, Exception? exception = null, int? exitCode = null, IReadOnlyCollection<string>? secrets = null,
        WinetricksProcessLog? processLog = null)
    {
        try
        {
            var context = new Dictionary<string, string>
            {
                ["Operation"] = operation,
                ["RunnerType"] = runnerType,
                ["PrefixPath"] = prefixPath
            };
            if (exitCode.HasValue)
                context["ExitCode"] = exitCode.Value.ToString(CultureInfo.InvariantCulture);
            if (processLog != null)
            {
                foreach (var pair in processLog.GetDiagnosticContext())
                    context[pair.Key] = pair.Value;
                if (code == "Winetricks.ProcessFailed" && !context.ContainsKey("ExitCode"))
                    context["ExitCode"] = processLog.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
            }
            _errorService.Report(new ErrorReport(code, severity, nameof(WinetricksService),
                code switch
                {
                    "Winetricks.StartFailed" => "Winetricks could not be started.",
                    "Winetricks.ProcessFailed" => "Winetricks execution failed.",
                    "Winetricks.PrefixMappingFailed" => "An optional prefix drive mapping could not be prepared.",
                    "Winetricks.LogReadFailed" => "The Winetricks log could not be read.",
                    "Winetricks.LogWriteFailed" => "The Winetricks process transcript could not be written or retained.",
                    _ => "The Winetricks prefix or execution could not be prepared."
                }, exception, CorrelationId: processLog?.CorrelationId,
                Context: context, SensitiveValues: secrets ?? GetSensitiveValues(null)));
        }
        catch
        {
            // Diagnostics never determine the execution result or prefix preparation policy.
        }
    }

    private static string RunnerType(bool isProton, bool isUmu) => isUmu ? "UMU" : isProton ? "Proton" : "Wine";

    private static string[] GetSensitiveValues(IReadOnlyDictionary<string, string>? environment)
    {
        var secrets = environment?.Where(pair => LaunchSecretRedactor.IsSensitiveName(pair.Key))
            .Select(pair => pair.Value).ToList() ?? [];
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
            if (variable.Key is string name && LaunchSecretRedactor.IsSensitiveName(name) && variable.Value is string value)
                secrets.Add(value);
        return secrets.Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> GetSensitiveArgumentValues(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index].TrimStart('-', '/');
            var separator = argument.IndexOf('=');
            var name = separator >= 0 ? argument[..separator] : argument;
            if (!LaunchSecretRedactor.IsSensitiveName(name))
                continue;
            if (separator >= 0 && separator + 1 < argument.Length)
                yield return argument[(separator + 1)..];
            else if (separator < 0 && index + 1 < arguments.Count)
                yield return arguments[++index];
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
