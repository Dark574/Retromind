using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

/// <summary>
/// Represents a configured Wine/Proton runtime version entry.
/// The path can be DataRoot-relative (portable) or absolute (external install).
/// </summary>
public partial class RunnerVersionConfig : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private RunnerVersionKind _kind = RunnerVersionKind.Proton;

    [ObservableProperty]
    private RunnerVersionSourceType _sourceType = RunnerVersionSourceType.ExternalPath;

    [ObservableProperty]
    private string _path = string.Empty;

    /// <summary>
    /// Optional source tag for downloaded versions (e.g. "GE-Proton10-34").
    /// </summary>
    [ObservableProperty]
    private string? _releaseTag;
}

public enum RunnerVersionKind
{
    Proton = 0,
    Wine = 1
}

public enum RunnerVersionSourceType
{
    ManagedDownload = 0,
    ExternalPath = 1
}
