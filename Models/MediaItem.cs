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

    // NEU: Pfad zum Wine-Prefix (relativ zu Medien/...)
    [ObservableProperty] private string? _prefixPath;

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
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // Konfigurationseinstellungen

    // Art des Starts
    public MediaType MediaType { get; set; } = MediaType.Native;
}

public enum MediaType
{
    Native, // Direkt ausführbar (Linux Binary / Shell Script)
    Emulator, // Braucht Emulator
    Video // Braucht Video Player
}

public enum PlayStatus
{
    Incomplete, // Noch nicht begonnen oder mittendrin
    Completed, // Durchgespielt / Gelesen
    Abandoned // Abgebrochen (optional für später)
}