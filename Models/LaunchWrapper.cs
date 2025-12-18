namespace Retromind.Models;

/// <summary>
/// Represents one wrapper step for launching native apps.
/// Examples: gamemoderun, mangohud, prime-run, env.
/// Use "{file}" in Args to mark where the wrapped executable should be inserted.
/// </summary>
public sealed class LaunchWrapper
{
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Optional arguments for the wrapper. If null/empty, "{file}" is assumed.
    /// </summary>
    public string? Args { get; set; }
}