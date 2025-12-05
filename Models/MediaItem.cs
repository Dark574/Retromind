using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Retromind.Models;

public partial class MediaItem : ObservableObject
{
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string? _developer;

    // ID des Emulator-Profils (null = Manuell konfiguriert)
    [ObservableProperty] private string? _emulatorId;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private DateTime? _lastPlayed;

    // Argumente. Platzhalter {file} wird durch FilePath ersetzt.
    // Beispiel: "--fullscreen {file}"
    [ObservableProperty] private string? _launcherArgs;

    // Pfad zum ausführenden Programm (z.B. Emulator, Video-Player). 
    // Wenn leer, wird FilePath direkt ausgeführt (Native).
    [ObservableProperty] private string? _launcherPath;
    [ObservableProperty] private string? _logoPath;
    [ObservableProperty] private string? _musicPath;
    [ObservableProperty] private int _playCount;

    // Pfad zum Wine-Prefix (relativ zu Medien/...)
    [ObservableProperty] private string? _prefixPath;

    // --- NEUE FELDER FÜR PLAYTIME ---

    // Gesamtspielzeit
    [ObservableProperty] private TimeSpan _totalPlayTime = TimeSpan.Zero;

    // Optionaler Prozessname für manuelles Tracking (z.B. "hl2_linux")
    [ObservableProperty] private string? _overrideWatchProcess;
    
    // Metadaten
    [ObservableProperty] private DateTime? _releaseDate;
    // Rating (0-100)
    [ObservableProperty] private double _rating;
    [ObservableProperty] private PlayStatus _status = PlayStatus.Incomplete;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _wallpaperPath;

    public MediaItem()
    {
    } // Für JSON Deserializer

    public MediaItem(string title)
    {
        Title = title;
    }

    // Basis-Informationen
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();

    // Konfigurationseinstellungen

    // (Stelle sicher, dass MediaType vom Typ dieses Enums ist)
    [ObservableProperty] private MediaType _mediaType = MediaType.Native;
}

public enum MediaType
{
    Native, // Direkt ausführbar (Linux Binary / Shell Script)
    Emulator, // Braucht Emulator
    Command // Externer Befehl / URL Protocol (Steam, Heroic, Browser)
}

public enum PlayStatus
{
    Incomplete, // Noch nicht begonnen oder mittendrin
    Completed, // Durchgespielt / Gelesen
    Abandoned // Abgebrochen (optional für später)
}