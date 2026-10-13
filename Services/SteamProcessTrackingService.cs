using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services;

/// <summary>
/// Tracks Linux game processes by the exact Steam App ID inherited through their environment.
/// Optional console diagnostics expose the observed process lifecycle without changing it.
/// </summary>
public class SteamProcessTrackingService
{
    internal const string DiagnosticsEnvironmentVariable = "RETROMIND_STEAM_PROCESS_DIAGNOSTICS";
    private static readonly TimeSpan InitialActivityTimeout = TimeSpan.FromSeconds(40);

    private static readonly (string Name, byte[] Prefix)[] SteamAppIdVariables =
    [
        ("SteamAppId", "SteamAppId="u8.ToArray()),
        ("SteamGameId", "SteamGameId="u8.ToArray()),
        ("STEAM_COMPAT_APP_ID", "STEAM_COMPAT_APP_ID="u8.ToArray())
    ];

    private static readonly HashSet<string> KnownHelperCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "reaper",
        "pressure-vessel",
        "pressure-vessel-wrap",
        "pv-adverb",
        "i386-linux-gnu-capsule-capture-libs",
        "x86_64-linux-gnu-capsule-capture-libs",
        "steam.exe",
        "wine",
        "wine64",
        "wine-preloader",
        "wine64-preloader",
        "wineserver",
        "wineboot.exe",
        "services.exe",
        "winedevice.exe",
        "plugplay.exe",
        "svchost.exe",
        "explorer.exe",
        "rpcss.exe",
        "tabtip.exe",
        "xalia.exe",
        "crashpad_handler.exe",
        "steamservice.exe",
        "conhost.exe",
        "cmd.exe",
        "start.exe",
        "bootstrapper-v2",
        "cpatch",
        "iscriptevaluator.exe",
        "dxsetup.exe",
        "infinst.exe",
        "msiexec",
        "msiexec.exe",
        "regsvr32.exe",
        "rundll32.exe",
        "unitycrashhandler32.exe",
        "unitycrashhandler64.exe"
    };

    private static readonly HashSet<string> KnownWrapperProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "reaper",
        "pressure-vessel",
        "pressure-vessel-wrap",
        "pv-adverb",
        "srt-bwrap",
        "ldconfig",
        "i386-linux-gnu-capsule-capture-libs",
        "x86_64-linux-gnu-capsule-capture-libs",
        "steam-runtime-launch-client"
    };

    private readonly string _procRoot;
    private readonly Action<string> _writeLog;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _initialPollInterval;
    private readonly TimeSpan _steadyPollInterval;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _candidateConfirmationDuration;
    private readonly bool _diagnosticsEnabled;
    private readonly object _observationGate = new();
    private readonly Dictionary<long, ActiveSteamProcessObservation> _activeObservations = [];
    private long _nextAttemptId;

    public SteamProcessTrackingService()
        : this(
            "/proc",
            Console.WriteLine,
            IsEnabledValue(Environment.GetEnvironmentVariable(DiagnosticsEnvironmentVariable)),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5))
    {
    }

    internal SteamProcessTrackingService(
        string procRoot,
        Action<string> writeLog,
        bool enabled,
        TimeSpan observationDuration,
        TimeSpan initialPollInterval,
        TimeSpan steadyPollInterval,
        TimeSpan quietPeriod,
        TimeSpan candidateConfirmationDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(procRoot);
        ArgumentNullException.ThrowIfNull(writeLog);

        _procRoot = procRoot;
        _writeLog = writeLog;
        _diagnosticsEnabled = enabled;
        _startupTimeout = observationDuration;
        _initialPollInterval = initialPollInterval;
        _steadyPollInterval = steadyPollInterval;
        _quietPeriod = quietPeriod;
        _candidateConfirmationDuration = candidateConfirmationDuration;
    }

    internal virtual SteamProcessTrackingSession? Prepare(uint appId, string? title)
    {
        if (!OperatingSystem.IsLinux() || appId == 0)
            return null;

        try
        {
            return new SteamProcessTrackingSession(
                Interlocked.Increment(ref _nextAttemptId),
                appId,
                SanitizeForLog(title),
                CaptureProcessSnapshot());
        }
        catch (Exception ex)
        {
            WriteLog($"Could not create the pre-launch process snapshot: {ex.Message}");
            return null;
        }
    }

    internal virtual async Task<SteamProcessTrackingResult> TrackAsync(
        SteamProcessTrackingSession? session,
        Action<IReadOnlyCollection<int>>? onGameProcessesDetected = null,
        CancellationToken cancellationToken = default)
    {
        if (session == null)
            return SteamProcessTrackingResult.NotTracked;

        var observation = new ActiveSteamProcessObservation(session, cancellationToken);
        List<ActiveSteamProcessObservation> supersededObservations;
        lock (_observationGate)
        {
            supersededObservations = _activeObservations.Values
                .Where(active =>
                    (!active.IsConfirmed || active.Session.AppId == session.AppId) &&
                    active.TryMarkSuperseded())
                .ToList();
            foreach (var superseded in supersededObservations)
                superseded.Cancellation.Cancel();

            _activeObservations[session.AttemptId] = observation;
        }

        foreach (var superseded in supersededObservations)
        {
            WriteLog(
                $"Attempt {superseded.Session.AttemptId} for AppID {superseded.Session.AppId} " +
                $"was discarded because Steam launch attempt {session.AttemptId} started. " +
                "Only confirmed sessions for a different AppID remain active.");
        }

        return await ObserveRegisteredAsync(observation, onGameProcessesDetected)
            .ConfigureAwait(false);
    }

    internal Task<SteamProcessTrackingResult> ObserveAsync(
        SteamProcessTrackingSession session,
        CancellationToken cancellationToken)
        => ObserveAsync(
            session,
            cancellationToken,
            tryConfirm: null,
            onGameProcessesDetected: null);

    private async Task<SteamProcessTrackingResult> ObserveAsync(
        SteamProcessTrackingSession session,
        CancellationToken cancellationToken,
        Func<bool>? tryConfirm,
        Action<IReadOnlyCollection<int>>? onGameProcessesDetected)
    {
        ArgumentNullException.ThrowIfNull(session);

        var initialActivityTimeout = Min(_startupTimeout, InitialActivityTimeout);
        WriteLog(
            $"Attempt {session.AttemptId}: waiting up to {FormatDuration(initialActivityTimeout)} for " +
            $"initial Steam activity and up to {FormatDuration(_startupTimeout)} for a likely game " +
            $"process belonging to \"{session.Title}\" (AppID {session.AppId}).");

        var stopwatch = Stopwatch.StartNew();
        var preExistingProcesses = session.PreExistingProcesses;
        var activeProcesses = new Dictionary<int, SteamProcessMatch>();
        var completedProcesses = new HashSet<SteamProcessIdentity>();
        var unmatchedAttempts = new Dictionary<SteamProcessIdentity, int>();
        var ignoredProcesses = new HashSet<SteamProcessIdentity>();
        TimeSpan? firstActivityAt = null;
        TimeSpan? candidateGroupActiveSince = null;
        TimeSpan? confirmedSessionStartedAt = null;
        TimeSpan? quietPeriodStartedAt = null;
        TimeSpan? abandonedLauncherQuietPeriodStartedAt = null;
        var detectedProcessCount = 0;
        var isConfirmed = false;
        var interactiveLauncherWasSeen = false;
        var waitingForNextGroupWasLogged = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentProcessIds = EnumerateProcessIds().ToHashSet();

            foreach (var activeProcess in activeProcesses.ToArray())
            {
                if (currentProcessIds.Contains(activeProcess.Key) &&
                    TryReadProcessIdentity(activeProcess.Key, out var currentIdentity) &&
                    currentIdentity == activeProcess.Value.Identity)
                {
                    continue;
                }

                activeProcesses.Remove(activeProcess.Key);
                completedProcesses.Add(activeProcess.Value.Identity);
                WriteLog(
                    $"Attempt {session.AttemptId} T+{FormatElapsed(stopwatch.Elapsed)} AppID {session.AppId}: " +
                    $"PID={activeProcess.Key}, Name={activeProcess.Value.ProcessName} exited.");
            }

            RemoveExitedProcessAttempts(unmatchedAttempts, currentProcessIds);

            foreach (var processId in currentProcessIds)
            {
                if (activeProcesses.ContainsKey(processId) ||
                    !TryReadProcessIdentity(processId, out var identity) ||
                    preExistingProcesses.Contains(identity) ||
                    ignoredProcesses.Contains(identity) ||
                    completedProcesses.Contains(identity))
                {
                    continue;
                }

                if (TryInspectProcess(identity, session.AppId, out var match))
                {
                    activeProcesses[processId] = match;
                    interactiveLauncherWasSeen |= LooksLikeInteractiveLauncher(match);
                    unmatchedAttempts.Remove(identity);
                    firstActivityAt ??= stopwatch.Elapsed;
                    quietPeriodStartedAt = null;
                    waitingForNextGroupWasLogged = false;
                    detectedProcessCount++;
                    WriteLog(
                        $"Attempt {session.AttemptId} T+{FormatElapsed(stopwatch.Elapsed)} " +
                        $"AppID {session.AppId}: " +
                        $"PID={match.ProcessId}, PPID={match.ParentProcessId}, " +
                        $"Name={match.ProcessName}, Executable={match.ExecutableName}, " +
                        $"Command={match.CommandName}, Role={GetDiagnosticRole(match)}, " +
                        $"Match={match.MatchedVariables} appeared.");
                    continue;
                }

                // A freshly forked process may be observed just before exec has
                // installed its final environment. Retry it a few times only.
                var attempts = unmatchedAttempts.GetValueOrDefault(identity) + 1;
                if (attempts >= 4)
                {
                    unmatchedAttempts.Remove(identity);
                    ignoredProcesses.Add(identity);
                }
                else
                {
                    unmatchedAttempts[identity] = attempts;
                }
            }

            var activeCandidateCount = activeProcesses.Values.Count(IsLikelyGameProcess);
            if (!isConfirmed)
            {
                if (activeCandidateCount > 0)
                {
                    // Steam game starts frequently hand off between short-lived processes
                    // (for example shell/EAC bootstrap -> game). Confirm uninterrupted
                    // candidate-group activity rather than requiring one PID to survive.
                    candidateGroupActiveSince ??= stopwatch.Elapsed;
                    if (stopwatch.Elapsed - candidateGroupActiveSince >= _candidateConfirmationDuration)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (tryConfirm?.Invoke() ?? true)
                        {
                            isConfirmed = true;
                            confirmedSessionStartedAt = candidateGroupActiveSince;
                            NotifyGameProcessesDetected(
                                activeProcesses,
                                onGameProcessesDetected);
                            WriteLog(
                                $"Attempt {session.AttemptId} AppID {session.AppId}: likely game process group " +
                                $"confirmed after {_candidateConfirmationDuration.TotalSeconds:0.#} seconds " +
                                "of continuous activity.");
                        }
                    }
                }
                else
                {
                    candidateGroupActiveSince = null;
                }

                if (!isConfirmed &&
                    firstActivityAt != null &&
                    activeProcesses.Count == 0 &&
                    !waitingForNextGroupWasLogged)
                {
                    WriteLog(
                        $"Attempt {session.AttemptId} AppID {session.AppId}: the current helper/setup " +
                        "process group ended without confirming a game process; observation continues.");
                    waitingForNextGroupWasLogged = true;
                }

                if (interactiveLauncherWasSeen && activeProcesses.Count == 0)
                {
                    abandonedLauncherQuietPeriodStartedAt ??= stopwatch.Elapsed;
                    if (stopwatch.Elapsed - abandonedLauncherQuietPeriodStartedAt >= _quietPeriod)
                    {
                        WriteLog(
                            $"Attempt {session.AttemptId} AppID {session.AppId}: the recognized game " +
                            $"launcher closed and no game process appeared for " +
                            $"{_quietPeriod.TotalSeconds:0.#} seconds.");
                        return SteamProcessTrackingResult.NotTracked;
                    }
                }
                else
                {
                    abandonedLauncherQuietPeriodStartedAt = null;
                }

                var pendingTimeout = firstActivityAt == null
                    ? Min(_startupTimeout, InitialActivityTimeout)
                    : _startupTimeout;
                if (!isConfirmed && stopwatch.Elapsed >= pendingTimeout)
                {
                    var activityDescription = firstActivityAt == null
                        ? "no matching process appeared"
                        : $"{detectedProcessCount} matching helper/setup process instance(s) appeared, " +
                          "but no likely game process remained active long enough";
                    WriteLog(
                        $"Attempt {session.AttemptId} AppID {session.AppId}: startup observation " +
                        $"finished after {FormatDuration(pendingTimeout)}; {activityDescription}.");
                    return SteamProcessTrackingResult.NotTracked;
                }
            }

            if (isConfirmed)
            {
                if (activeCandidateCount == 0)
                {
                    quietPeriodStartedAt ??= stopwatch.Elapsed;
                    if (stopwatch.Elapsed - quietPeriodStartedAt >= _quietPeriod)
                    {
                        var measuredSessionDuration = quietPeriodStartedAt.Value -
                                                      confirmedSessionStartedAt!.Value;
                        WriteLog(
                            $"Attempt {session.AttemptId} AppID {session.AppId}: session boundary " +
                            $"observed after {FormatElapsed(measuredSessionDuration)}; " +
                            $"{detectedProcessCount} matching process instance(s) found. No likely " +
                            $"game process remained for {_quietPeriod.TotalSeconds:0.#} seconds.");
                        return new SteamProcessTrackingResult(
                            WasTracked: true,
                            measuredSessionDuration);
                    }
                }
                else
                {
                    quietPeriodStartedAt = null;
                    NotifyGameProcessesDetected(
                        activeProcesses,
                        onGameProcessesDetected);
                }
            }

            var elapsed = stopwatch.Elapsed;
            var pollInterval = firstActivityAt == null && elapsed < TimeSpan.FromSeconds(10)
                ? _initialPollInterval
                : _steadyPollInterval;
            if (!isConfirmed)
            {
                var pendingTimeout = firstActivityAt == null
                    ? Min(_startupTimeout, InitialActivityTimeout)
                    : _startupTimeout;
                var startupTimeRemaining = pendingTimeout - elapsed;
                if (startupTimeRemaining <= TimeSpan.Zero)
                    continue;

                if (startupTimeRemaining < pollInterval)
                    pollInterval = startupTimeRemaining;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool TryMatchSteamAppId(
        ReadOnlySpan<byte> environment,
        uint expectedAppId,
        out string matchedVariables)
    {
        var matches = new List<string>();
        var offset = 0;

        while (offset < environment.Length)
        {
            var terminator = environment[offset..].IndexOf((byte)0);
            var length = terminator >= 0 ? terminator : environment.Length - offset;
            var entry = environment.Slice(offset, length);

            foreach (var variable in SteamAppIdVariables)
            {
                if (!entry.StartsWith(variable.Prefix))
                    continue;

                var value = Encoding.ASCII.GetString(entry[variable.Prefix.Length..]);
                if (uint.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var appId) &&
                    appId == expectedAppId)
                {
                    matches.Add(variable.Name);
                }
            }

            if (terminator < 0)
                break;

            offset += length + 1;
        }

        matchedVariables = string.Join(',', matches);
        return matches.Count > 0;
    }

    internal static bool IsEnabledValue(string? value)
    {
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLikelyGameProcess(SteamProcessMatch match)
    {
        var processName = NormalizeProcessName(match.ProcessName);
        var executableName = NormalizeProcessName(match.ExecutableName);
        var commandName = NormalizeProcessName(match.CommandName);
        if (string.IsNullOrEmpty(commandName) || commandName == "?")
            commandName = executableName;
        if (string.IsNullOrEmpty(commandName) || commandName == "?")
            commandName = processName;

        if (LooksLikeInteractiveLauncher(match) ||
            KnownWrapperProcessNames.Contains(processName) ||
            processName.StartsWith("python3", StringComparison.OrdinalIgnoreCase) ||
            KnownHelperCommands.Contains(commandName) ||
            commandName.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase) ||
            commandName.StartsWith("vcredist", StringComparison.OrdinalIgnoreCase) ||
            commandName.StartsWith("ue4prereqsetup", StringComparison.OrdinalIgnoreCase) ||
            commandName.StartsWith("dotnetfx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !commandName.EndsWith("capsule-capture-libs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInteractiveLauncher(SteamProcessMatch match)
    {
        return HasLauncherMarker(match.ProcessName) ||
               HasLauncherMarker(match.ExecutableName) ||
               HasLauncherMarker(match.CommandName);
    }

    private static bool HasLauncherMarker(string? value)
    {
        var name = NormalizeProcessName(value);
        return name.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
               // Linux /proc/<pid>/comm truncates names after 15 characters.
               name.EndsWith("launche", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SteamProcessTrackingResult> ObserveRegisteredAsync(
        ActiveSteamProcessObservation observation,
        Action<IReadOnlyCollection<int>>? onGameProcessesDetected)
    {
        try
        {
            return await ObserveAsync(
                    observation.Session,
                    observation.Cancellation.Token,
                    () => MarkObservationConfirmed(observation),
                    onGameProcessesDetected)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (observation.Cancellation.IsCancellationRequested)
        {
            if (observation.IsSuperseded)
                return SteamProcessTrackingResult.NotTracked;

            throw;
        }
        catch (Exception ex)
        {
            WriteLog(
                $"Attempt {observation.Session.AttemptId} AppID {observation.Session.AppId}: " +
                $"observation failed: {ex.Message}");
            return SteamProcessTrackingResult.NotTracked;
        }
        finally
        {
            lock (_observationGate)
                _activeObservations.Remove(observation.Session.AttemptId);

            observation.Cancellation.Dispose();
        }
    }

    private bool MarkObservationConfirmed(ActiveSteamProcessObservation observation)
    {
        lock (_observationGate)
            return observation.TryMarkConfirmed();
    }

    private bool TryInspectProcess(
        SteamProcessIdentity identity,
        uint expectedAppId,
        out SteamProcessMatch match)
    {
        match = default;
        var processId = identity.ProcessId;
        var processDirectory = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture));

        try
        {
            var environment = File.ReadAllBytes(Path.Combine(processDirectory, "environ"));
            if (!TryMatchSteamAppId(environment, expectedAppId, out var matchedVariables))
                return false;

            var processName = ReadTrimmedText(Path.Combine(processDirectory, "comm"));
            var (parentProcessId, startTime) = ReadProcessStat(Path.Combine(processDirectory, "stat"));
            if (startTime != identity.StartTime)
                return false;

            var executableName = ReadExecutableName(Path.Combine(processDirectory, "exe"));
            var commandName = ReadCommandName(Path.Combine(processDirectory, "cmdline"));

            match = new SteamProcessMatch(
                identity,
                processId,
                parentProcessId,
                SanitizeForLog(processName),
                SanitizeForLog(executableName),
                SanitizeForLog(commandName),
                matchedVariables);
            return true;
        }
        catch
        {
            // Processes can disappear or deny access between any two /proc reads.
            return false;
        }
    }

    private IEnumerable<int> EnumerateProcessIds()
    {
        foreach (var directory in Directory.EnumerateDirectories(_procRoot))
        {
            if (int.TryParse(
                    Path.GetFileName(directory),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId))
            {
                yield return processId;
            }
        }
    }

    private HashSet<SteamProcessIdentity> CaptureProcessSnapshot()
    {
        var identities = new HashSet<SteamProcessIdentity>();
        foreach (var processId in EnumerateProcessIds())
        {
            if (TryReadProcessIdentity(processId, out var identity))
                identities.Add(identity);
        }

        return identities;
    }

    private void RemoveExitedProcessAttempts(
        Dictionary<SteamProcessIdentity, int> processAttempts,
        IReadOnlySet<int> currentProcessIds)
    {
        foreach (var identity in processAttempts.Keys.ToArray())
        {
            if (!currentProcessIds.Contains(identity.ProcessId) ||
                !TryReadProcessIdentity(identity.ProcessId, out var currentIdentity) ||
                currentIdentity != identity)
            {
                processAttempts.Remove(identity);
            }
        }
    }

    private static string ReadTrimmedText(string path)
        => File.Exists(path) ? File.ReadAllText(path).Trim() : "?";

    private bool TryReadProcessIdentity(
        int processId,
        out SteamProcessIdentity identity)
    {
        identity = default;
        try
        {
            var (_, startTime) = ReadProcessStat(Path.Combine(
                _procRoot,
                processId.ToString(CultureInfo.InvariantCulture),
                "stat"));
            if (startTime == 0)
                return false;

            identity = new SteamProcessIdentity(processId, startTime);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (int ParentProcessId, ulong StartTime) ReadProcessStat(string path)
    {
        var value = File.ReadAllText(path);
        var commandEnd = value.LastIndexOf(')');
        if (commandEnd < 0 || commandEnd + 2 >= value.Length)
            return (0, 0);

        var fields = value[(commandEnd + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19)
            return (0, 0);

        _ = int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId);
        _ = ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime);
        return (parentProcessId, startTime);
    }

    private static string ReadExecutableName(string path)
    {
        var linkTarget = new FileInfo(path).LinkTarget;
        return string.IsNullOrWhiteSpace(linkTarget)
            ? "?"
            : Path.GetFileName(linkTarget);
    }

    private static string ReadCommandName(string path)
    {
        if (!File.Exists(path))
            return "?";

        var arguments = Encoding.UTF8
            .GetString(File.ReadAllBytes(path))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Length == 0)
            return "?";

        var executableArgument = arguments.FirstOrDefault(argument =>
            argument.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var selectedArgument = executableArgument ?? arguments[0];
        return Path.GetFileName(selectedArgument.Replace('\\', '/'));
    }

    private void WriteLog(string message)
    {
        if (_diagnosticsEnabled)
            _writeLog($"[SteamProcessDiagnostic] {message}");
    }

    private static void NotifyGameProcessesDetected(
        IReadOnlyDictionary<int, SteamProcessMatch> activeProcesses,
        Action<IReadOnlyCollection<int>>? onGameProcessesDetected)
    {
        if (onGameProcessesDetected == null)
            return;

        var processIds = activeProcesses.Values
            .Where(IsLikelyGameProcess)
            .Select(match => match.ProcessId)
            .ToArray();
        if (processIds.Length > 0)
            onGameProcessesDetected(processIds);
    }

    private static string GetDiagnosticRole(SteamProcessMatch match)
        => IsLikelyGameProcess(match) ? "candidate" : "helper/setup";

    private static string NormalizeProcessName(string? value)
        => (Path.GetFileName(value?.Replace('\\', '/')) ?? string.Empty).Trim();

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.#} minutes"
            : $"{duration.TotalSeconds:0.#} seconds";

    private static TimeSpan Min(TimeSpan left, TimeSpan right)
        => left <= right ? left : right;

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "?";

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}

internal sealed record SteamProcessTrackingSession(
    long AttemptId,
    uint AppId,
    string Title,
    IReadOnlySet<SteamProcessIdentity> PreExistingProcesses);

internal sealed class ActiveSteamProcessObservation(
    SteamProcessTrackingSession session,
    CancellationToken cancellationToken)
{
    private int _isConfirmed;
    private int _isSuperseded;

    internal SteamProcessTrackingSession Session { get; } = session;
    internal CancellationTokenSource Cancellation { get; } =
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    internal bool IsConfirmed => Volatile.Read(ref _isConfirmed) != 0;
    internal bool IsSuperseded => Volatile.Read(ref _isSuperseded) != 0;

    internal bool TryMarkConfirmed()
    {
        if (Volatile.Read(ref _isSuperseded) != 0)
            return false;

        Interlocked.Exchange(ref _isConfirmed, 1);
        return true;
    }

    internal bool TryMarkSuperseded()
        => Interlocked.CompareExchange(ref _isSuperseded, 1, 0) == 0;
}

internal readonly record struct SteamProcessMatch(
    SteamProcessIdentity Identity,
    int ProcessId,
    int ParentProcessId,
    string ProcessName,
    string ExecutableName,
    string CommandName,
    string MatchedVariables);

internal readonly record struct SteamProcessIdentity(int ProcessId, ulong StartTime);

internal readonly record struct SteamProcessTrackingResult(
    bool WasTracked,
    TimeSpan Duration)
{
    internal static SteamProcessTrackingResult NotTracked { get; } = new(false, TimeSpan.Zero);
}
