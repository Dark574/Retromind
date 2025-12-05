using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

public partial class EmulatorConfig : ObservableObject
{
    // Standard-Argumente (z.B. -fullscreen {file})
    [ObservableProperty] private string _arguments = "{file}";

    [ObservableProperty] private string _name = "New Emulator";

    // Pfad zur Executable (z.B. /usr/bin/snes9x)
    [ObservableProperty] private string _path = "";

    public string Id { get; set; } = Guid.NewGuid().ToString();
}