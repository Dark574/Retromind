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
    /// <summary>
    /// Pre-created view instance for this theme. For host-level themes (main BigMode),
    /// this is typically used once and kept alive as long as the theme is active.
    /// For system subthemes, prefer <see cref="CreateView"/> instead to avoid
    /// reusing a single control across different parents.
    /// </summary>
    public Control View { get; }
    
    /// <summary>
    /// Optional factory that can create new view instances for this theme.
    /// This is especially useful for system subthemes in BigMode where we want
    /// fast switching between categories without reusing the same UserControl
    /// instance (which would cause "already has a visual parent" crashes).
    /// </summary>
    private readonly Func<Control>? _viewFactory;
    
    public ThemeSounds Sounds { get; }
    public string BasePath { get; }

    /// <summary>
    /// Optional theme-local path to a background video for the secondary
    /// video channel (e.g. "Video/bkg_anim.mp4"), resolved against BasePath.
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
        string? attractModeSoundPath = null,
        Func<Control>? viewFactory = null)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
        Sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        BasePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
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
        
        _viewFactory = viewFactory;
    }
    
    /// <summary>
    /// Creates a new view instance for this theme. When a factory has been
    /// provided (e.g. by ThemeLoader), it is used to build a fresh control.
    /// Otherwise, this falls back to the original <see cref="View"/> instance.
    ///
    /// For host-level themes (main BigMode), using View directly is fine
    /// because the theme view is only attached once. For system subthemes,
    /// hosts should always call CreateView() to avoid reusing the same
    /// UserControl across different ContentControls.
    /// </summary>
    public Control CreateView()
    {
        if (_viewFactory != null)
        {
            return _viewFactory();
        }

        return View;
    }
}