using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

/// <summary>
/// Represents a configuration profile for an emulator executable
/// </summary>
public partial class EmulatorConfig : ObservableObject
{
    public enum RunnerIntent
    {
        Auto,
        UmuProton,
        Wine,
        Generic
    }

    /// <summary>
    /// XDG environment handling mode for emulator launches.
    /// Inherit: keep process environment as-is.
    /// Host: remove XDG_* overrides so host defaults are used.
    /// Custom: apply explicit XDG_* paths from this profile.
    /// </summary>
    public enum XdgOverrideMode
    {
        Inherit,
        Host,
        Custom
    }
    
    /// <summary>
    /// Unique identifier for this profile
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Display name of the profile (e.g. "RetroArch SNES Core")
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// Full path to the executable file
    /// </summary>
    [ObservableProperty]
    private string _path = string.Empty;

    /// <summary>
    /// Command line arguments passed to the emulator
    /// Must contain "{file}" as a placeholder for the ROM path
    /// </summary>
    [ObservableProperty]
    private string _arguments = "{file}";

    /// <summary>
    /// If true, Retromind will generate an .m3u playlist for multi-disc items and pass the playlist
    /// as "{file}" to the emulator (instead of launching Disc 1 directly)
    /// </summary>
    [ObservableProperty]
    private bool _usePlaylistForMultiDisc;

    // For runners like UMU/Proton/Wine that benefit from per-game prefixes
    [ObservableProperty]
    private bool _usesWinePrefix;

    /// <summary>
    /// Intent hint for runtime version filtering and environment wiring.
    /// Auto keeps backward-compatible heuristics.
    /// </summary>
    [ObservableProperty]
    private RunnerIntent _runnerType = RunnerIntent.Auto;

    /// <summary>
    /// Optional default Wine/Proton runtime version used by items that inherit this emulator.
    /// Item-level selection can override this.
    /// </summary>
    [ObservableProperty]
    private string? _defaultRunnerVersionId;
    
    /// <summary>
    /// Controls how XDG_* variables are handled when this emulator profile launches.
    /// Default is Host for compatibility with external launchers/runtimes.
    /// </summary>
    [ObservableProperty]
    private XdgOverrideMode _xdgMode = XdgOverrideMode.Host;

    /// <summary>
    /// Optional custom XDG paths used when <see cref="XdgOverrideMode"/> is set to Custom.
    /// Relative paths are resolved against AppPaths.DataRoot.
    /// </summary>
    [ObservableProperty] private string? _xdgConfigPath;
    [ObservableProperty] private string? _xdgDataPath;
    [ObservableProperty] private string? _xdgCachePath;
    [ObservableProperty] private string? _xdgStatePath;

    /// <summary>
    /// Optional emulator-level wrapper chain used as the base for node and item settings.
    /// For example: gamemoderun, mangohud, env FOO=bar
    /// </summary>
    public List<LaunchWrapper>? NativeWrappersOverride { get; set; }
    
    /// <summary>
    /// Per-emulator environment variable overrides
    /// These are applied before item-level overrides, so a MediaItem.EnvironmentOverrides
    /// entry with the same key will win over this value
    /// Typical use-cases: PROTONPATH, WINEDEBUG, DXVK_HUD, etc
    /// </summary>
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
}
