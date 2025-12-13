using Avalonia.Controls;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Represents a loaded theme, containing its visual component (View),
/// sound definitions, and base file path.
/// </summary>
public class Theme
{
    public Control View { get; }
    public ThemeSounds Sounds { get; }
    public string BasePath { get; }

    public Theme(Control view, ThemeSounds sounds, string basePath)
    {
        View = view;
        Sounds = sounds;
        BasePath = basePath;
    }
}