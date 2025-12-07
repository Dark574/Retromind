using System;
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

    // --- Assets (Relative or Absolute Paths) ---

    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string? _wallpaperPath;
    [ObservableProperty] private string? _logoPath;
    [ObservableProperty] private string? _musicPath;

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

    public MediaItem() { } // For JSON Deserializer

    public MediaItem(string title)
    {
        Title = title;
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