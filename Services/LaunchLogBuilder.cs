using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

internal sealed partial class LaunchLogBuilder
{
    private static readonly string[] DiagnosticEnvironmentKeys =
    [
        "GAMEID",
        "PROTONPATH",
        "STEAM_COMPAT_DATA_PATH",
        "WINEPREFIX",
        "WINE",
        "WINEARCH",
        "PROTON_LOG",
        "UMU_LOG",
        "DXVK_HUD",
        "HOME",
        "PATH",
        "LD_LIBRARY_PATH",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_CACHE_HOME",
        "XDG_STATE_HOME"
    ];

    private readonly MediaItem _item;
    private readonly DateTimeOffset _startedAt;
    private readonly string? _runnerDescription;
    private readonly string? _wrapperDescription;
    private readonly bool _recordsStatistics;
    private string? _executable;
    private string? _arguments;
    private string? _workingDirectory;
    private string? _gamePath;
    private IReadOnlyList<KeyValuePair<string, string>> _environment = [];

    public LaunchLogBuilder(
        MediaItem item,
        EmulatorConfig? emulator,
        AppSettings settings,
        IReadOnlyList<LaunchWrapper>? wrappers,
        bool recordsStatistics)
    {
        _item = item;
        _startedAt = DateTimeOffset.Now;
        _recordsStatistics = recordsStatistics;
        _runnerDescription = ResolveRunnerDescription(item, emulator, settings);
        _wrapperDescription = wrappers is { Count: > 0 }
            ? string.Join(" -> ", wrappers
                .Where(wrapper => !string.IsNullOrWhiteSpace(wrapper.Path))
                .Select(wrapper => string.IsNullOrWhiteSpace(wrapper.Args)
                    ? wrapper.Path
                    : $"{wrapper.Path} {wrapper.Args}"))
            : null;
    }

    public void CaptureProcessStart(
        ProcessStartInfo startInfo,
        string? gamePath,
        IReadOnlyDictionary<string, string>? effectiveEnvironment)
    {
        _executable = startInfo.FileName;
        _arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(' ', startInfo.ArgumentList.Select(QuoteIfNeeded))
            : startInfo.Arguments;
        _workingDirectory = startInfo.WorkingDirectory;
        _gamePath = gamePath;
        _environment = CaptureEnvironment(startInfo, effectiveEnvironment);
    }

    public string Build(
        string outcome,
        TimeSpan runtime,
        int? exitCode = null,
        string? errorMessage = null,
        string? consoleOutput = null)
    {
        var log = new StringBuilder()
            .AppendLine("Retromind launch log")
            .AppendLine("Privacy note: Common secret fields are redacted automatically; review the log before sharing.")
            .AppendLine()
            .AppendLine($"Started: {_startedAt:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Title: {SanitizeLine(_item.Title)}")
            .AppendLine($"Media item ID: {_item.Id}")
            .AppendLine($"Media type: {_item.MediaType}")
            .AppendLine($"Statistics: {(_recordsStatistics ? "enabled" : "disabled (test launch)")}")
            .AppendLine($"Outcome: {outcome}")
            .AppendLine($"Runtime: {FormatRuntime(runtime)}");

        if (exitCode != null)
            log.AppendLine($"Exit code: {exitCode}");

        log.AppendLine()
            .AppendLine($"Executable: {ValueOrUnavailable(_executable)}")
            .AppendLine($"Arguments: {ValueOrUnavailable(RedactKnownSecrets(RedactArguments(_arguments)))}")
            .AppendLine($"Working directory: {ValueOrUnavailable(_workingDirectory)}")
            .AppendLine($"Game: {ValueOrUnavailable(_gamePath)}")
            .AppendLine($"Runner: {ValueOrUnavailable(_runnerDescription)}")
            .AppendLine($"Wrappers: {ValueOrUnavailable(RedactKnownSecrets(RedactArguments(_wrapperDescription)))}");

        if (_environment.Count > 0)
        {
            log.AppendLine().AppendLine("Environment:");
            foreach (var pair in _environment)
            {
                var value = IsSensitiveName(pair.Key) ? "<redacted>" : SanitizeLine(pair.Value);
                log.AppendLine($"{pair.Key}={value}");
            }
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
            log.AppendLine().AppendLine("Error:").AppendLine(RedactKnownSecrets(errorMessage.Trim()));

        if (!string.IsNullOrWhiteSpace(consoleOutput))
            log.AppendLine().AppendLine("Console output:").AppendLine(RedactKnownSecrets(consoleOutput.Trim()));

        log.AppendLine()
            .AppendLine($"Retromind: {typeof(LauncherService).Assembly.GetName().Version}")
            .AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Operating system: {RuntimeInformation.OSDescription}");

        return log.ToString();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> CaptureEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? effectiveEnvironment)
    {
        var keys = new HashSet<string>(DiagnosticEnvironmentKeys, StringComparer.Ordinal);
        if (effectiveEnvironment != null)
        {
            foreach (var key in effectiveEnvironment.Keys)
                keys.Add(key);
        }

        var result = new List<KeyValuePair<string, string>>();
        foreach (var key in keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!startInfo.Environment.TryGetValue(key, out var value) || value == null)
                continue;

            var inheritedValue = Environment.GetEnvironmentVariable(key);
            var explicitlyConfigured = effectiveEnvironment?.ContainsKey(key) == true;
            if (!explicitlyConfigured && string.Equals(value, inheritedValue, StringComparison.Ordinal))
                continue;

            result.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }

    private static string? ResolveRunnerDescription(
        MediaItem item,
        EmulatorConfig? emulator,
        AppSettings settings)
    {
        var runnerId = !string.IsNullOrWhiteSpace(item.RunnerVersionId)
            ? item.RunnerVersionId
            : emulator?.DefaultRunnerVersionId;
        var runner = RunnerVersionEnvironmentHelper.FindRunnerVersionById(settings, runnerId);
        if (runner == null)
            return null;

        var resolvedPath = RunnerVersionPathHelper.ResolveConfiguredPath(runner.Path) ?? runner.Path;
        return $"{runner.Name} ({runner.Kind}) - {resolvedPath}";
    }

    private static bool IsSensitiveName(string name)
    {
        var normalized = name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        return normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("PASSWD", StringComparison.Ordinal) ||
               normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("APIKEY", StringComparison.Ordinal) ||
               normalized.Contains("AUTH", StringComparison.Ordinal) ||
               normalized.Contains("COOKIE", StringComparison.Ordinal) ||
               normalized.Contains("CREDENTIAL", StringComparison.Ordinal);
    }

    private static string? RedactArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return arguments;

        return SensitiveArgumentRegex().Replace(
            arguments,
            match => match.Groups["prefix"].Value + "<redacted>");
    }

    private string? RedactKnownSecrets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        foreach (var secret in _environment
                     .Where(pair => IsSensitiveName(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                     .Select(pair => pair.Value)
                     .Distinct(StringComparer.Ordinal)
                     .OrderByDescending(secret => secret.Length))
        {
            value = value.Replace(secret, "<redacted>", StringComparison.Ordinal);
        }

        return value;
    }

    [GeneratedRegex(
        "(?<prefix>(?:--?|/)?(?:password|passwd|token|secret|api[-_]?key|authorization|cookie|credential)(?:\\s*=\\s*|\\s+))(?:\\\"[^\\\"]*\\\"|'[^']*'|\\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveArgumentRegex();

    private static string FormatRuntime(TimeSpan runtime)
    {
        var hours = (int)runtime.TotalHours;
        return $"{hours:00}:{runtime.Minutes:00}:{runtime.Seconds:00}";
    }

    private static string QuoteIfNeeded(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static string ValueOrUnavailable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<not available>" : SanitizeLine(value);

    private static string SanitizeLine(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
