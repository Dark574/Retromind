using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Retromind.Models;

namespace Retromind.Services;

public enum MediaFileType
{
    Cover,
    Logo,
    Wallpaper,
    Music
}

public class FileManagementService
{
    private const string RootFolderName = "Medien";

    /// <summary>
    ///     Importiert eine Asset-Datei (Cover, Wallpaper) in die portable Struktur.
    ///     Wenn die Datei schon existiert, wird ein Zähler (_01, _02) angehängt.
    /// </summary>
    public string ImportAsset(string sourceFilePath, MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        // 1. Basis-Verzeichnis der App
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 2. Ziel-Ordner Struktur bauen: Medien/Bereich/Gruppe/...
        var paths = new List<string> { RootFolderName };
        paths.AddRange(nodePath);
        paths.Add(fileType.ToString()); // Unterordner z.B. "Cover"

        var relativeDirectory = Path.Combine(paths.ToArray());
        var absoluteDirectory = Path.Combine(baseDir, relativeDirectory);

        // Ordner erstellen falls nicht existent
        if (!Directory.Exists(absoluteDirectory)) Directory.CreateDirectory(absoluteDirectory);

        // 3. Zieldateiname generieren
        var extension = Path.GetExtension(sourceFilePath);
        var safeTitle = SanitizeFileName(item.Title);

        // Basis: "Super_Mario_Bros_Cover"
        var baseFileName = $"{safeTitle}_{fileType}";
        
        // Standard-Name: "Super_Mario_Bros_Cover.jpg"
        var newFileName = $"{baseFileName}{extension}";
        var destinationPath = Path.Combine(absoluteDirectory, newFileName);

        // VERSIONIERUNG: Prüfen ob Datei existiert und hochzählen
        int counter = 1;
        while (File.Exists(destinationPath))
        {
            // Wir prüfen hier nicht auf Inhalt (Hash), wir behalten einfach alles.
            // Neuer Name: "Super_Mario_Bros_Cover_01.jpg"
            newFileName = $"{baseFileName}_{counter:D2}{extension}";
            destinationPath = Path.Combine(absoluteDirectory, newFileName);
            counter++;
        }

        // 4. Kopieren
        try
        {
            File.Copy(sourceFilePath, destinationPath); // Kein Überschreiben nötig
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Fehler beim Kopieren: {ex.Message}");
            return string.Empty;
        }

        // 5. Relativen Pfad zurückgeben
        return Path.Combine(relativeDirectory, newFileName);
    }

    /// <summary>
    ///     Entfernt ungültige Zeichen und ersetzt Leerzeichen durch Unterstriche.
    /// </summary>
    private string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";

        // Ungültige Zeichen für Dateinamen entfernen
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

        var safeName = Regex.Replace(name, invalidRegStr, "_");

        // Leerzeichen durch Unterstrich ersetzen (optional, aber unter Linux oft netter)
        safeName = safeName.Replace(" ", "_");

        return safeName;
    }

    /// <summary>
    ///     Sucht nach einem existierenden Asset für das Item (z.B. manuell reinkopiert).
    /// </summary>
    public string? FindExistingAsset(MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        var paths = new List<string> { RootFolderName };
        paths.AddRange(nodePath);
        paths.Add(fileType.ToString());

        var relativeDirectory = Path.Combine(paths.ToArray());
        var absoluteDirectory = Path.Combine(baseDir, relativeDirectory);

        if (!Directory.Exists(absoluteDirectory)) return null;

        // Wir suchen nach Dateien, die mit "Titel_Typ" anfangen.
        // z.B. "Super_Mario_Cover.jpg" oder "Super_Mario_Cover.png"
        var safeTitle = SanitizeFileName(item.Title);
        var searchPattern = $"{safeTitle}_{fileType}*"; // Sternchen am Ende für _01 etc.

        // Directory.GetFiles gibt volle Pfade zurück
        var foundFiles = Directory.GetFiles(absoluteDirectory, searchPattern);

        if (foundFiles.Length > 0)
        {
            // Wir nehmen den ersten Treffer
            var fullPath = foundFiles[0];
            var fileName = Path.GetFileName(fullPath);

            // Relativen Pfad zurückgeben
            return Path.Combine(relativeDirectory, fileName);
        }

        return null;
    }
}