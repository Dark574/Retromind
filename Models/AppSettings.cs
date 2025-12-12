using System.Collections.Generic;

namespace Retromind.Models;

/// <summary>
/// Represents the persistent application settings, serialized to JSON.
/// Contains layout preferences, theme, last state, and configurations for emulators/scrapers.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Schema version for migration purposes.
    /// </summary>
    public int Version { get; set; } = 1;

    // --- UI Layout ---

    public double TreeColumnWidth { get; set; } = 250;
    public double DetailColumnWidth { get; set; } = 300;
    
    /// <summary>
    /// Zoom level for the media grid tiles.
    /// </summary>
    public double ItemWidth { get; set; } = 150;
    public bool IsDarkTheme { get; set; } = false; 

    // --- State Persistence ---

    /// <summary>
    /// ID of the last selected tree node (to restore selection on startup).
    /// </summary>
    public string? LastSelectedNodeId { get; set; }

    /// <summary>
    /// ID of the last selected media item.
    /// </summary>
    public string? LastSelectedMediaId { get; set; }

    // --- Big Mode State ---
    /// <summary>
    /// The navigation path of nodes that were ENTERED.
    /// </summary>
    public List<string>? LastBigModeNavigationPath { get; set; }
        
    /// <summary>
    /// The ID of the last SELECTED node (can be a category or an item).
    /// </summary>
    public string? LastBigModeSelectedNodeId { get; set; }
    
    /// <summary>
    /// Flag to remember if the last view was a list of items (true) or categories (false).
    /// </summary>
    public bool LastBigModeWasItemView { get; set; }
    
    // --- Configurations ---

    /// <summary>
    /// List of configured emulator profiles.
    /// </summary>
    public List<EmulatorConfig> Emulators { get; set; } = new();
    
    /// <summary>
    /// List of configured metadata scrapers (API keys, credentials).
    /// </summary>
    public List<ScraperConfig> Scrapers { get; set; } = new();
}