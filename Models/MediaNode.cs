using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Retromind.Helpers;
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
    /// Native wrapper override for this node (inherits to children).
    /// null = inherit, empty list = explicit "no wrappers", non-empty list = override.
    /// </summary>
    public List<LaunchWrapper>? NativeWrappersOverride { get; set; }

    /// <summary>
    /// Environment variable overrides for this node (inherits to children).
    /// null = inherit, empty dictionary = explicit "no node-level overrides", non-empty = override.
    /// </summary>
    public Dictionary<string, string>? EnvironmentOverrides { get; set; }
    
    /// <summary>
    /// Optional system preview theme for this node (system selection in BigMode).
    /// Matches the folder name under Themes/System (e.g. "C64", "SNES").
    /// If null/empty, the system browser uses the default layout ("Default").
    /// </summary>
    public string? SystemPreviewThemeId { get; set; }
    
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
    /// Optional path to a theme file (.axaml) used for this area.
    /// </summary>
    public string? ThemePath { get; set; }
    
    // --- Presentation Metadata ---

    /// <summary>
    /// Optional description text for this category/platform.
    /// </summary>
    [ObservableProperty] 
    private string _description = string.Empty;

    // --- BigMode fallback toggles for item artwork ---

    [ObservableProperty]
    private bool _logoFallbackEnabled = false;

    [ObservableProperty]
    private bool _wallpaperFallbackEnabled = false;

    [ObservableProperty]
    private bool _videoFallbackEnabled = false;

    [ObservableProperty]
    private bool _marqueeFallbackEnabled = false;

    private ObservableCollection<MediaAsset> _assets = new();

    public ObservableCollection<MediaAsset> Assets
    {
        get => _assets;
        set
        {
            if (ReferenceEquals(_assets, value))
                return;

            if (_assets != null)
                _assets.CollectionChanged -= OnAssetsChanged;

            _assets = value ?? new ObservableCollection<MediaAsset>();
            _assets.CollectionChanged += OnAssetsChanged;
            OnPropertyChanged();

            OnAssetsChanged(_assets, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
    
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
        // Model layer must be UI-agnostic:
        // UI thread marshalling (if needed) must be handled by the caller (ViewModel/Service).
        _activeAssets[type] = relativePath;

        if (type == AssetType.Cover)
        {
            OnPropertyChanged(nameof(PrimaryCoverPath));
            OnPropertyChanged(nameof(PrimaryCoverAbsolutePath));
        }
        if (type == AssetType.Wallpaper)
        {
            OnPropertyChanged(nameof(PrimaryWallpaperPath));
            OnPropertyChanged(nameof(PrimaryWallpaperAbsolutePath));
        }
        if (type == AssetType.Logo)
        {
            OnPropertyChanged(nameof(PrimaryLogoPath));
            OnPropertyChanged(nameof(PrimaryLogoAbsolutePath));
        }
        if (type == AssetType.Video)
        {
            OnPropertyChanged(nameof(PrimaryVideoPath));
            OnPropertyChanged(nameof(PrimaryVideoAbsolutePath));
        }
        if (type == AssetType.Marquee)
        {
            OnPropertyChanged(nameof(PrimaryMarqueePath));
            OnPropertyChanged(nameof(PrimaryMarqueeAbsolutePath));
        }
    }
    
    public string? PrimaryCoverPath => GetPrimaryAssetPath(AssetType.Cover);
    public string? PrimaryWallpaperPath => GetPrimaryAssetPath(AssetType.Wallpaper);
    public string? PrimaryLogoPath => GetPrimaryAssetPath(AssetType.Logo);
    public string? PrimaryVideoPath => GetPrimaryAssetPath(AssetType.Video);
    public string? PrimaryMarqueePath => GetPrimaryAssetPath(AssetType.Marquee);

    // Absolute variants for UI bindings (AsyncImageHelper expects absolute file paths)
    public string? PrimaryCoverAbsolutePath => GetPrimaryAssetAbsolutePath(AssetType.Cover);
    public string? PrimaryWallpaperAbsolutePath => GetPrimaryAssetAbsolutePath(AssetType.Wallpaper);
    public string? PrimaryLogoAbsolutePath => GetPrimaryAssetAbsolutePath(AssetType.Logo);
    public string? PrimaryVideoAbsolutePath => GetPrimaryAssetAbsolutePath(AssetType.Video);
    public string? PrimaryMarqueeAbsolutePath => GetPrimaryAssetAbsolutePath(AssetType.Marquee);

    public bool IsFallbackEnabled(AssetType type)
    {
        return type switch
        {
            AssetType.Logo => LogoFallbackEnabled,
            AssetType.Wallpaper => WallpaperFallbackEnabled,
            AssetType.Video => VideoFallbackEnabled,
            AssetType.Marquee => MarqueeFallbackEnabled,
            _ => true
        };
    }

    public string? GetPrimaryAssetAbsolutePath(AssetType type)
    {
        var relPath = GetPrimaryAssetPath(type);
        if (string.IsNullOrWhiteSpace(relPath))
            return null;

        return AppPaths.ResolveDataPath(relPath);
    }
    
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
        _assets.CollectionChanged += OnAssetsChanged;
    } // For JSON Deserializer

    public MediaNode(string name, NodeType type)
    {
        Name = name;
        Type = type;
        IsExpanded = true; // Expand new nodes by default for better UX
        _assets.CollectionChanged += OnAssetsChanged;
    }
    
    private void OnAssetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Model layer must be UI-agnostic:
        // Ensure the caller modifies Assets on the UI thread if the UI is bound to this collection.

        var keysToRemove = new List<AssetType>();
        foreach (var kvp in _activeAssets)
        {
            bool stillExists = Assets.Any(a => a.RelativePath == kvp.Value && a.Type == kvp.Key);
            if (!stillExists) keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove) _activeAssets.Remove(key);

        OnPropertyChanged(nameof(PrimaryCoverPath));
        OnPropertyChanged(nameof(PrimaryCoverAbsolutePath));
        OnPropertyChanged(nameof(PrimaryWallpaperPath));
        OnPropertyChanged(nameof(PrimaryWallpaperAbsolutePath));
        OnPropertyChanged(nameof(PrimaryLogoPath));
        OnPropertyChanged(nameof(PrimaryLogoAbsolutePath));
        OnPropertyChanged(nameof(PrimaryVideoPath));
        OnPropertyChanged(nameof(PrimaryVideoAbsolutePath));
        OnPropertyChanged(nameof(PrimaryMarqueePath));
        OnPropertyChanged(nameof(PrimaryMarqueeAbsolutePath));
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
