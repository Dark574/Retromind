using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

/// <summary>
/// Represents a single media entry (Game, Movie, Book, etc.) in the library.
/// Contains all metadata, file paths, launch configuration, and statistics.
/// </summary>
public partial class MediaItem : ObservableObject
{
    // --- Identification ---

    /// <summary>
    /// Unique identifier for this item.
    /// </summary>
    [ObservableProperty] 
    private string _id = Guid.NewGuid().ToString();

    /// <summary>
    /// The display title of the item.
    /// </summary>
    [ObservableProperty] 
    private string _title = string.Empty;

    // --- Core Data ---

    /// <summary>
    /// Full path to the main file (ROM, executable, or URL).
    /// </summary>
    [ObservableProperty] 
    private string? _filePath;

    /// <summary>
    /// Type of the media, determining how it is launched.
    /// </summary>
    [ObservableProperty] 
    private MediaType _mediaType = MediaType.Native;

    // --- Metadata ---

    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _genre;
    
    /// <summary>
    /// Original release date of the media.
    /// </summary>
    [ObservableProperty] private DateTime? _releaseDate;
    
    /// <summary>
    /// User rating (normalized 0-100).
    /// </summary>
    [ObservableProperty] private double _rating;
    
    /// <summary>
    /// Current play status (e.g. Completed, Abandoned).
    /// </summary>
    [ObservableProperty] private PlayStatus _status = PlayStatus.Incomplete;

    private ObservableCollection<MediaAsset> _assets = new();

    public ObservableCollection<MediaAsset> Assets 
    { 
        get => _assets;
        set
        {
            if (_assets == value) return;

            // 1. Altes Abo kündigen (falls vorhanden)
            if (_assets != null)
            {
                _assets.CollectionChanged -= OnAssetsChanged;
            }

            _assets = value;

            // 2. Neues Abo abschließen (falls neue Liste nicht null ist)
            if (_assets != null)
            {
                _assets.CollectionChanged += OnAssetsChanged;
            }

            // Optional: PropertyChanged feuern, falls jemand direkt an die Liste gebunden hat
            OnPropertyChanged();
        }
    }
    
    // Temporärer Speicher für Randomisierung (nicht persistiert)
    private readonly Dictionary<AssetType, string> _activeAssets = new();
    
    // Helper für interne Logik (gibt RELATIVEN Pfad zurück)
    public string? GetPrimaryAssetPath(AssetType type)
    {
        // 1. Prüfen ob ein Override gesetzt ist (Randomisierung)
        if (_activeAssets.TryGetValue(type, out var path))
        {
            return path;
        }
        
        // 2. Fallback: Erstes Asset in der Liste
        return Assets.FirstOrDefault(a => a.Type == type)?.RelativePath;
    }
    
    // Helper für UI-Binding (gibt ABSOLUTEN Pfad zurück)
    public string? GetPrimaryAssetAbsolutePath(AssetType type)
    {
        var relPath = GetPrimaryAssetPath(type);
        if (string.IsNullOrEmpty(relPath)) return null;
        
        // Kombiniert BaseDirectory mit relativem Pfad
        return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relPath);
    }
    
    /// <summary>
    /// Setzt explizit ein Asset als "aktiv" für die Anzeige (für Randomisierung).
    /// </summary>
    public void SetActiveAsset(AssetType type, string relativePath)
    {
        _activeAssets[type] = relativePath;
        
        switch (type)
        {
            case AssetType.Cover: OnPropertyChanged(nameof(PrimaryCoverPath)); break;
            case AssetType.Wallpaper: OnPropertyChanged(nameof(PrimaryWallpaperPath)); break;
            case AssetType.Logo: OnPropertyChanged(nameof(PrimaryLogoPath)); break;
            case AssetType.Video: OnPropertyChanged(nameof(PrimaryVideoPath)); break;
        }
    }
    
    // --- UI Properties (Binding Sources) ---
    // Nutzen jetzt Absolute Path Helper
    public string? PrimaryCoverPath => GetPrimaryAssetAbsolutePath(AssetType.Cover);
    public string? PrimaryWallpaperPath => GetPrimaryAssetAbsolutePath(AssetType.Wallpaper);
    public string? PrimaryLogoPath => GetPrimaryAssetAbsolutePath(AssetType.Logo);
    public string? PrimaryVideoPath => GetPrimaryAssetAbsolutePath(AssetType.Video);

    // --- Launch Configuration ---

    /// <summary>
    /// Link to a specific Emulator Profile ID (if MediaType is Emulator).
    /// If null, manual configuration below is used.
    /// </summary>
    [ObservableProperty] private string? _emulatorId;

    /// <summary>
    /// Custom path to the executable launcher (if not using a profile).
    /// </summary>
    [ObservableProperty] private string? _launcherPath;

    /// <summary>
    /// Command line arguments. Placeholder {file} is replaced by FilePath.
    /// </summary>
    [ObservableProperty] private string? _launcherArgs;

    /// <summary>
    /// Optional: Path to a WINE prefix directory (relative to Media root or absolute).
    /// </summary>
    [ObservableProperty] private string? _prefixPath;

    /// <summary>
    /// Optional: Name of the process to watch for playtime tracking (e.g. "hl2_linux").
    /// Useful if the launcher exits immediately (like Steam).
    /// </summary>
    [ObservableProperty] private string? _overrideWatchProcess;

    // --- Statistics ---

    [ObservableProperty] private DateTime? _lastPlayed;
    [ObservableProperty] private int _playCount;
    [ObservableProperty] private TimeSpan _totalPlayTime = TimeSpan.Zero;

    // --- Constructors ---

    public MediaItem(string title)
    {
        Title = title;
        // Abonniere Änderungen an der Assets-Liste
        Assets.CollectionChanged += OnAssetsChanged;
    }
    
    // Default-Konstruktor (für JSON) muss das Event auch abonnieren
    public MediaItem() 
    { 
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
        
        // 1. Clean up active assets cache
        // If an asset was removed that was currently "active" (randomized winner),
        // we must remove it from the dictionary so the fallback (First) works again.
        
        var keysToRemove = new List<AssetType>();
        foreach (var kvp in _activeAssets)
        {
            // Check if the path stored in active assets still exists in the collection
            bool stillExists = Assets.Any(a => a.RelativePath == kvp.Value && a.Type == kvp.Key);
            if (!stillExists)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            _activeAssets.Remove(key);
        }

        // 2. Notify UI
        OnPropertyChanged(nameof(PrimaryCoverPath));
        OnPropertyChanged(nameof(PrimaryWallpaperPath));
        OnPropertyChanged(nameof(PrimaryLogoPath));
        OnPropertyChanged(nameof(PrimaryVideoPath));
    }
}

/// <summary>
/// Defines how the media item is launched.
/// </summary>
public enum MediaType
{
    /// <summary>
    /// Directly executable (Binary, Shell Script).
    /// </summary>
    Native, 
    /// <summary>
    /// Launched via an emulator core/executable.
    /// </summary>
    Emulator, 
    /// <summary>
    /// External command or URL Protocol (e.g. steam://, heroic://).
    /// </summary>
    Command 
}

/// <summary>
/// Completion status of the item.
/// </summary>
public enum PlayStatus
{
    Incomplete, 
    Completed, 
    Abandoned 
}