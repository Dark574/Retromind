using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
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

    /// <summary>
    /// Optional: Pfad zu einer Theme-Datei (.axaml), die für diesen Bereich genutzt werden soll.
    /// </summary>
    public string? ThemePath { get; set; }
    
    // --- Presentation Metadata ---

    /// <summary>
    /// Optional description text for this category/platform.
    /// </summary>
    [ObservableProperty] 
    private string _description = string.Empty;

    public ObservableCollection<MediaAsset> Assets { get; set; } = new();
    
    private readonly Dictionary<AssetType, string> _activeAssets = new();
    
    public string? GetPrimaryAssetPath(AssetType type)
    {
        if (_activeAssets.TryGetValue(type, out var path))
        {
            return path;
        }
        return Assets.FirstOrDefault(a => a.Type == type)?.RelativePath;
    }

    public void SetActiveAsset(AssetType type, string relativePath)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => SetActiveAsset(type, relativePath));
            return;
        }
        
        _activeAssets[type] = relativePath;
        
        if (type == AssetType.Cover) OnPropertyChanged(nameof(PrimaryCoverPath));
        if (type == AssetType.Wallpaper) OnPropertyChanged(nameof(PrimaryWallpaperPath));
        if (type == AssetType.Logo) OnPropertyChanged(nameof(PrimaryLogoPath));
        if (type == AssetType.Video) OnPropertyChanged(nameof(PrimaryVideoPath));
    }
    
    public string? PrimaryCoverPath => GetPrimaryAssetPath(AssetType.Cover);
    public string? PrimaryWallpaperPath => GetPrimaryAssetPath(AssetType.Wallpaper);
    public string? PrimaryLogoPath => GetPrimaryAssetPath(AssetType.Logo);
    public string? PrimaryVideoPath => GetPrimaryAssetPath(AssetType.Video);
    
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

    public MediaNode()
    {
        Assets.CollectionChanged += OnAssetsChanged;
    } // For JSON Deserializer

    public MediaNode(string name, NodeType type)
    {
        Name = name;
        Type = type;
        IsExpanded = true; // Expand new nodes by default for better UX
        Assets.CollectionChanged += OnAssetsChanged;
    }
    
    private void OnAssetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sicherstellen, dass die UI-Updates auf dem UI-Thread passieren
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => OnAssetsChanged(sender, e));
            return;
        }
        
        var keysToRemove = new List<AssetType>();
        foreach (var kvp in _activeAssets)
        {
            bool stillExists = Assets.Any(a => a.RelativePath == kvp.Value && a.Type == kvp.Key);
            if (!stillExists) keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove) _activeAssets.Remove(key);

        OnPropertyChanged(nameof(PrimaryCoverPath));
        OnPropertyChanged(nameof(PrimaryWallpaperPath));
        OnPropertyChanged(nameof(PrimaryLogoPath));
        OnPropertyChanged(nameof(PrimaryVideoPath));
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