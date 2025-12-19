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
    
    /// <summary>
    /// Name of the control that acts as the placement slot for the video overlay.
    /// Defaults to "VideoSlot".
    /// </summary>
    public string VideoSlotName { get; }

    // Optional metadata (for UI / diagnostics / theme browser)
    public string? Name { get; }
    public string? Author { get; }
    public string? Version { get; }
    public string? WebsiteUrl { get; }

    public Theme(
        Control view,
        ThemeSounds sounds,
        string basePath,
        bool videoEnabled = true,
        string? videoSlotName = null,
        string? name = null,
        string? author = null,
        string? version = null,
        string? websiteUrl = null)
    {
        View = view;
        Sounds = sounds;
        BasePath = basePath;
        VideoEnabled = videoEnabled;

        VideoSlotName = string.IsNullOrWhiteSpace(videoSlotName) ? "VideoSlot" : videoSlotName;

        Name = name;
        Author = author;
        Version = version;
        WebsiteUrl = websiteUrl;
    }
}