using System;
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
    /// Optionaler, theme-lokaler Pfad zu einem Hintergrundvideo für den
    /// zweiten Videokanal (z.B. "Video/bkg_anim.mp4").
    /// Wird mit BasePath kombiniert.
    /// </summary>
    public string? SecondaryBackgroundVideoPath { get; }

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

    /// <summary>
    /// Enables theme-driven attract mode. When true and AttractModeIdleInterval is set,
    /// the host may automatically scroll/select random items while the user is idle.
    /// </summary>
    public bool AttractModeEnabled { get; }

    /// <summary>
    /// Base idle interval after which the first attract-mode selection occurs.
    /// Additional multiples of this interval trigger further selections
    /// while the user remains inactive.
    /// </summary>
    public TimeSpan? AttractModeIdleInterval { get; }
    
    /// <summary>
    /// Optional theme-local sound that is played when Attract Mode starts its
    /// "spin" animation (relative to BasePath).
    /// </summary>
    public string? AttractModeSoundPath { get; }
    
    public Theme(
        Control view,
        ThemeSounds sounds,
        string basePath,
        string? secondaryBackgroundVideoPath = null,
        bool videoEnabled = true,
        string? videoSlotName = null,
        string? name = null,
        string? author = null,
        string? version = null,
        string? websiteUrl = null,
        bool attractModeEnabled = false,
        TimeSpan? attractModeIdleInterval = null,
        string? attractModeSoundPath = null)
    {
        View = view;
        Sounds = sounds;
        BasePath = basePath;
        SecondaryBackgroundVideoPath = secondaryBackgroundVideoPath;
        VideoEnabled = videoEnabled;

        VideoSlotName = string.IsNullOrWhiteSpace(videoSlotName) ? "VideoSlot" : videoSlotName;

        Name = name;
        Author = author;
        Version = version;
        WebsiteUrl = websiteUrl;
        
        AttractModeEnabled = attractModeEnabled;
        AttractModeIdleInterval = attractModeIdleInterval;
        AttractModeSoundPath = attractModeSoundPath;
    }
}