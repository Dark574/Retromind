using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

/// <summary>
/// Represents a node in the library tree structure (e.g., a Folder, Platform, or Category).
/// Can contain child nodes (sub-folders) and media items (games).
/// </summary>
public partial class MediaNode : ObservableObject
{
    // --- Core Identity ---

    /// <summary>
    /// Unique identifier for state persistence (e.g., remembering selection).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Display name of the node.
    /// </summary>
    [ObservableProperty] 
    private string _name = string.Empty;

    /// <summary>
    /// Type of the node (Root Area vs. Group).
    /// Used for visual distinction (icons).
    /// </summary>
    [ObservableProperty] 
    private NodeType _type;

    // --- UI State ---

    /// <summary>
    /// Whether the tree node is currently expanded in the UI.
    /// </summary>
    [ObservableProperty] 
    private bool _isExpanded; 

    // --- Configuration (Inheritable) ---

    /// <summary>
    /// Controls cover randomization behavior.
    /// null = Inherit from parent, true = Enabled, false = Disabled.
    /// </summary>
    [ObservableProperty] 
    private bool? _randomizeCovers;

    /// <summary>
    /// Controls background music randomization behavior.
    /// null = Inherit from parent, true = Enabled, false = Disabled.
    /// </summary>
    [ObservableProperty] 
    private bool? _randomizeMusic;
    
    /// <summary>
    /// ID of the default emulator profile for items in this node.
    /// If null, items inherit from parent node.
    /// </summary>
    public string? DefaultEmulatorId { get; set; }

    // --- Presentation Metadata ---

    /// <summary>
    /// Optional description text for this category/platform.
    /// </summary>
    [ObservableProperty] 
    private string _description = string.Empty;

    /// <summary>
    /// Path to a representative image (e.g. console hardware icon).
    /// </summary>
    [ObservableProperty] 
    private string? _coverPath;

    /// <summary>
    /// Path to a background fanart/wallpaper.
    /// </summary>
    [ObservableProperty] 
    private string? _wallpaperPath;
    
    /// <summary>
    /// Path to a video preview (e.g. platform intro).
    /// </summary>
    [ObservableProperty] 
    private string? _videoPath;

    /// <summary>
    /// Path to a logo (e.g. system logo).
    /// </summary>
    [ObservableProperty] 
    private string? _logoPath;
    
    // --- Content ---

    /// <summary>
    /// Sub-folders / Child nodes.
    /// </summary>
    public ObservableCollection<MediaNode> Children { get; set; } = new();

    /// <summary>
    /// Media items directly contained in this node.
    /// </summary>
    public ObservableCollection<MediaItem> Items { get; set; } = new();

    // --- Constructors ---

    public MediaNode() { } // For JSON Deserializer

    public MediaNode(string name, NodeType type)
    {
        Name = name;
        Type = type;
        IsExpanded = true; // Expand new nodes by default for better UX
    }
}

/// <summary>
/// Visual type of the node.
/// </summary>
public enum NodeType
{
    /// <summary>
    /// Top-level category (e.g. "Games", "Movies").
    /// </summary>
    Area,
    /// <summary>
    /// Sub-category or platform (e.g. "SNES", "Action").
    /// </summary>
    Group
}