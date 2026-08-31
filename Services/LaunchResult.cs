namespace Retromind.Services;

/// <summary>
/// Reports whether a launch request was handed off successfully.
/// Runtime/tracking failures after that handoff do not turn a successful launch into a failure.
/// </summary>
public sealed record LaunchResult(
    LaunchOutcome Outcome,
    string? ErrorMessage,
    string? MissingWatchedProcessName,
    int? ExitCode,
    string? ConsoleOutput)
{
    public bool IsStarted => Outcome != LaunchOutcome.StartFailed;

    public static LaunchResult Started { get; } = new(LaunchOutcome.Started, null, null, null, null);

    public static LaunchResult Failed(string errorMessage) =>
        new(LaunchOutcome.StartFailed, errorMessage, null, null, null);

    public static LaunchResult WatchedProcessNotFound(string processName, string? consoleOutput) =>
        new(LaunchOutcome.WatchedProcessNotFound, null, processName, null, consoleOutput);

    public static LaunchResult ExitedEarly(int exitCode, string? consoleOutput) =>
        new(LaunchOutcome.ExitedEarly, null, null, exitCode, consoleOutput);
}

public enum LaunchOutcome
{
    Started,
    StartFailed,
    WatchedProcessNotFound,
    ExitedEarly
}

internal enum ProcessWatchOutcome
{
    Tracked,
    AlreadyRunning,
    NotFound
}
