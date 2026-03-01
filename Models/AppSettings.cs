using System.Collections.Generic;

namespace Retromind.Models;

/// <summary>
/// Represents the persistent application settings, serialized to JSON
/// Contains layout preferences, theme, last state, and configurations for emulators/scrapers
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Schema version for migration purposes
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When true, newly selected launch file paths will be stored relative
    /// to the portable data root (AppPaths.DataRoot). When false, absolute
    /// paths are stored (classic behavior)
    /// </summary>
    public bool PreferPortableLaunchPaths { get; set; } = false;

    /// <summary>
    /// When true and running as AppImage, Retromind redirects HOME and XDG_* paths
    /// into a local "Home" folder next to the AppImage for full portability.
    /// Requires restart to take effect.
    /// </summary>
    public bool UsePortableHomeInAppImage { get; set; } = false;

    /// <summary>
    /// Optional manual Steam library paths (folders containing steamapps or steamapps itself).
    /// If empty, Retromind uses automatic discovery only.
    /// </summary>
    public List<string> SteamLibraryPaths { get; set; } = new();

    /// <summary>
    /// Optional manual Heroic GOG config paths (heroic folder, gog_store folder, or installed.json).
    /// If empty, Retromind uses automatic discovery only.
    /// </summary>
    public List<string> HeroicGogConfigPaths { get; set; } = new();

    /// <summary>
    /// Optional manual Heroic Epic config paths (heroic folder, epic_store folder, or installed.json).
    /// If empty, Retromind uses automatic discovery only.
    /// </summary>
    public List<string> HeroicEpicConfigPaths { get; set; } = new();
    
    // --- Native wrapper defaults (C: global -> node -> item) ---

    /// <summary>
    /// Global default wrapper chain for native launches (Linux)
    /// If empty, native apps launch directly
    /// </summary>
    public List<LaunchWrapper> DefaultNativeWrappers { get; set; } = new();
    
    /// <summary>
    /// Preferred LibVLC/FFmpeg hardware decoding mode for BigMode preview videos
    /// Valid values are implementation-defined (e.g. "none", "auto", "vaapi")
    /// Default is "none" for maximum compatibility on unknown systems
    /// </summary>
    public string? VlcHardwareDecodeMode { get; set; } = "none";
    
    /// <summary>
    /// Controls whether selecting an item in the main media grid should
    /// automatically start playback of its primary music asset (if any)
    /// When false, selection never starts background music automatically
    /// </summary>
    public bool EnableSelectionMusicPreview { get; set; } = true;
    
    // --- UI Layout ---

    public double TreeColumnWidth { get; set; } = 250;
    public double DetailColumnWidth { get; set; } = 300;
    
    /// <summary>
    /// Zoom level for the media grid tiles
    /// </summary>
    public double ItemWidth { get; set; } = 150;
    public bool IsDarkTheme { get; set; } = false; 

    // --- State Persistence ---

    /// <summary>
    /// ID of the last selected tree node (to restore selection on startup)
    /// </summary>
    public string? LastSelectedNodeId { get; set; }

    /// <summary>
    /// ID of the last selected media item
    /// </summary>
    public string? LastSelectedMediaId { get; set; }

    // --- Big Mode State ---
    /// <summary>
    /// The navigation path of nodes that were ENTERED
    /// </summary>
    public List<string>? LastBigModeNavigationPath { get; set; }
        
    /// <summary>
    /// The ID of the last SELECTED node (can be a category or an item)
    /// </summary>
    public string? LastBigModeSelectedNodeId { get; set; }
    
    /// <summary>
    /// Flag to remember if the last view was a list of items (true) or categories (false)
    /// </summary>
    public bool LastBigModeWasItemView { get; set; }
    
    // --- Configurations ---

    /// <summary>
    /// List of configured emulator profiles
    /// </summary>
    public List<EmulatorConfig> Emulators { get; set; } = new();
    
    /// <summary>
    /// List of configured metadata scrapers (API keys, credentials)
    /// </summary>
    public List<ScraperConfig> Scrapers { get; set; } = new();
}
