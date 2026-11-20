using System;

namespace Retromind.Models;

public enum AutomaticUpdateMode
{
    Disabled,
    DownloadOnly,
    DownloadAndInstall
}

public sealed record DownloadSettings
{
    public AutomaticUpdateMode AutomaticUpdates { get; init; } = AutomaticUpdateMode.Disabled;
    public bool LimitToTimeWindow { get; init; }
    public TimeOnly WindowStart { get; init; } = new(0, 0);
    public TimeOnly WindowEnd { get; init; } = new(6, 0);
    public decimal MegabytesPerSecond { get; init; }
    public string? GogArchiveDirectory { get; init; }
    public bool KeepGogInstallers { get; init; }

    internal long BytesPerSecond => MegabytesPerSecond <= 0
        ? 0
        : (long)Math.Max(1, Math.Min(MegabytesPerSecond, 100_000) * 1_000_000);
}
