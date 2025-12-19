using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Helpers;

namespace Retromind.Models;

/// <summary>
/// Represents a single media entry (Game, Movie, Book, etc.) in the library.
/// Contains all metadata, file references, launch configuration, and statistics.
/// </summary>
public partial class MediaItem : ObservableObject
{
    // --- Identification ---

    /// <summary>
    /// Native wrapper override for this item.
    /// null = inherit, empty list = explicit "no wrappers", non-empty list = override.
    /// </summary>
    public List<LaunchWrapper>? NativeWrappersOverride { get; set; }

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
    /// File references for this item (multi-disc, multi-part, etc.).
    /// For now we primarily support Absolute paths (portable Retromind, external media).
    /// </summary>
    [ObservableProperty]
    private List<MediaFileRef> _files = new();

    /// <summary>
    /// Type of the media, determining how it is launched.
    /// </summary>
    [ObservableProperty]
    private MediaType _mediaType = MediaType.Native;

    // --- Metadata ---

    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private string? _series;
    [ObservableProperty] private string? _players;

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

    /// <summary>
    /// Gets or sets a value indicating whether this item is marked as a favorite.
    /// </summary>
    [ObservableProperty] private bool _isFavorite;

    [ObservableProperty]
    private ObservableCollection<string> _tags = new();

    private ObservableCollection<MediaAsset> _assets = new();

    public ObservableCollection<MediaAsset> Assets
    {
        get => _assets;
        set
        {
            if (_assets == value) return;

            // Unsubscribe old collection (if any)
            if (_assets != null)
            {
                _assets.CollectionChanged -= OnAssetsChanged;
            }

            _assets = value;

            // Subscribe new collection (if not null)
            if (_assets != null)
            {
                _assets.CollectionChanged += OnAssetsChanged;
            }

            OnPropertyChanged();
        }
    }

    // Temporary storage for randomization (not persisted)
    private readonly Dictionary<AssetType, string> _activeAssets = new();

    // Helper for internal logic (returns RELATIVE path)
    public string? GetPrimaryAssetPath(AssetType type)
    {
        // 1) Check if an override is set (randomization)
        if (_activeAssets.TryGetValue(type, out var path))
        {
            return path;
        }

        // 2) Fallback: first asset in the list
        return Assets.FirstOrDefault(a => a.Type == type)?.RelativePath;
    }

    // Helper for UI binding (returns ABSOLUTE path)
    public string? GetPrimaryAssetAbsolutePath(AssetType type)
    {
        var relPath = GetPrimaryAssetPath(type);
        if (string.IsNullOrEmpty(relPath)) return null;

        return AppPaths.ResolveDataPath(relPath);
    }

    /// <summary>
    /// Sets an asset explicitly as "active" for display (used by randomization).
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
            case AssetType.Marquee: OnPropertyChanged(nameof(PrimaryMarqueePath)); break;
            case AssetType.Banner: OnPropertyChanged(nameof(PrimaryBannerPath)); break;
        }
    }

    /// <summary>
    /// Clears all forced overrides (random images) and reverts back to defaults.
    /// </summary>
    public void ResetActiveAssets()
    {
        if (_activeAssets.Count > 0)
        {
            _activeAssets.Clear();
            OnPropertyChanged(nameof(PrimaryCoverPath));
            OnPropertyChanged(nameof(PrimaryWallpaperPath));
            OnPropertyChanged(nameof(PrimaryLogoPath));
            OnPropertyChanged(nameof(PrimaryVideoPath));
            OnPropertyChanged(nameof(PrimaryMarqueePath));
            OnPropertyChanged(nameof(PrimaryBannerPath));
        }
    }

    // --- UI Properties (Binding Sources) ---
    public string? PrimaryCoverPath => GetPrimaryAssetAbsolutePath(AssetType.Cover);
    public string? PrimaryWallpaperPath => GetPrimaryAssetAbsolutePath(AssetType.Wallpaper);
    public string? PrimaryLogoPath => GetPrimaryAssetAbsolutePath(AssetType.Logo);
    public string? PrimaryVideoPath => GetPrimaryAssetAbsolutePath(AssetType.Video);
    public string? PrimaryMarqueePath => GetPrimaryAssetAbsolutePath(AssetType.Marquee);
    public string? PrimaryBannerPath => GetPrimaryAssetAbsolutePath(AssetType.Banner);

    // --- Launch: Multi-file helpers ---

    /// <summary>
    /// Returns the primary file reference used for default launching (Disc 1 / first entry).
    /// </summary>
    public MediaFileRef? GetPrimaryFile()
    {
        if (Files is not { Count: > 0 })
            return null;

        // Prefer Index=1, then smallest Index, then first.
        var byIndex = Files
            .Where(f => f.Index.HasValue && f.Index.Value > 0)
            .OrderBy(f => f.Index!.Value)
            .ToList();

        var disc1 = byIndex.FirstOrDefault(f => f.Index == 1);
        if (disc1 != null) return disc1;

        if (byIndex.Count > 0) return byIndex[0];

        return Files[0];
    }

    /// <summary>
    /// Resolves the primary file to a full path for launching.
    /// For now we support Absolute paths. Future kinds can be added without changing callers.
    /// </summary>
    public string? GetPrimaryLaunchPath()
    {
        var primary = GetPrimaryFile();
        if (primary == null) return null;

        if (primary.Kind == MediaFileKind.Absolute)
            return string.IsNullOrWhiteSpace(primary.Path) ? null : primary.Path;

        // Future: MountRelative / LibraryRelative resolution can be implemented here.
        return string.IsNullOrWhiteSpace(primary.Path) ? null : primary.Path;
    }

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
    /// Command line arguments. Placeholder {file} is replaced by the primary launch file.
    /// </summary>
    [ObservableProperty] private string? _launcherArgs;

    /// <summary>
    /// Optional: Path to a WINE prefix directory (relative to library root or absolute).
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
        Assets.CollectionChanged += OnAssetsChanged;
    }

    public MediaItem()
    {
        Assets.CollectionChanged += OnAssetsChanged;
    }

    private void OnAssetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Model layer must be UI-agnostic:
        // Ensure the caller modifies Assets on the UI thread if the UI is bound to this collection.

        var keysToRemove = new List<AssetType>();
        foreach (var kvp in _activeAssets)
        {
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

        OnPropertyChanged(nameof(PrimaryCoverPath));
        OnPropertyChanged(nameof(PrimaryWallpaperPath));
        OnPropertyChanged(nameof(PrimaryLogoPath));
        OnPropertyChanged(nameof(PrimaryVideoPath));
        OnPropertyChanged(nameof(PrimaryMarqueePath));
        OnPropertyChanged(nameof(PrimaryBannerPath));
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