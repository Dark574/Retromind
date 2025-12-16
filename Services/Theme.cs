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

    /// <summary>
    /// If false, the host should disable any video overlay/preview even if the theme contains a video slot.
    /// Defaults to true when not specified by the theme.
    /// </summary>
    public bool VideoEnabled { get; }
    
    public Theme(Control view, ThemeSounds sounds, string basePath, bool videoEnabled = true)
    {
        View = view;
        Sounds = sounds;
        BasePath = basePath;
        VideoEnabled = videoEnabled;
    }
}