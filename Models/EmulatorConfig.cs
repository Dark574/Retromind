using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

/// <summary>
/// Represents a configuration profile for an emulator executable.
/// </summary>
public partial class EmulatorConfig : ObservableObject
{
    /// <summary>
    /// Unique identifier for this profile.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Display name of the profile (e.g. "RetroArch SNES Core").
    /// </summary>
    [ObservableProperty] 
    private string _name = string.Empty; // initialize empty

    /// <summary>
    /// Full path to the executable file.
    /// </summary>
    [ObservableProperty] 
    private string _path = string.Empty;

    /// <summary>
    /// Command line arguments passed to the emulator.
    /// Must contain "{file}" as a placeholder for the ROM path.
    /// </summary>
    [ObservableProperty] 
    private string _arguments = "{file}";
    
    // for runners like UMU/Proton/Wine that benefit from per-game prefixes.
    [ObservableProperty]
    private bool _usesWinePrefix;
}