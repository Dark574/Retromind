namespace Retromind.Models;

public enum WinetricksFailureCategory
{
    None,
    Preparation,
    ProcessStart,
    ProcessExecution,
    Cancelled
}

/// <summary>Result of one Winetricks execution, independent of diagnostic output.</summary>
public sealed record WinetricksResult(
    bool Success,
    int? ExitCode = null,
    WinetricksFailureCategory FailureCategory = WinetricksFailureCategory.None,
    string? ErrorMessage = null)
{
    public bool IsCancelled => FailureCategory == WinetricksFailureCategory.Cancelled;
}
