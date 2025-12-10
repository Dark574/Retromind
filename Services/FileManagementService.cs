using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Zentraler Service für Datei-Operationen.
/// Setzt die Namenskonvention (Name_Typ_Nummer) durch und verwaltet die physischen Dateien.
/// </summary>
public class FileManagementService
{
    // Regex für: Name_Typ_Nummer.Endung
    // Gruppe 1: Name (z.B. "Super_Mario")
    // Gruppe 2: Typ (z.B. "Wallpaper")
    // Gruppe 3: Nummer (z.B. "01")
    private static readonly Regex AssetRegex = new Regex(@"^(.+)_(Wallpaper|Cover|Logo|Video|Marquee|Music)_(\d+)\..*$", RegexOptions.IgnoreCase);

    private readonly string _libraryRootPath;

    public FileManagementService(string libraryRootPath)
    {
        _libraryRootPath = libraryRootPath;
    }

    /// <summary>
    /// Importiert eine Datei, benennt sie um (Konvention) und kopiert sie in den richtigen Ordner.
    /// Fügt sie auch direkt der Asset-Liste des Items hinzu.
    /// </summary>
    public MediaAsset? ImportAsset(string sourceFilePath, MediaItem item, List<string> nodePathStack, AssetType type)
    {
        if (!File.Exists(sourceFilePath)) return null;

        // 1. Ziel-Basisordner bestimmen
        string nodeFolder = Path.Combine(_libraryRootPath, Path.Combine(nodePathStack.ToArray()));
        
        // 2. Namen generieren (z.B. "Stardew_Valley_Wallpaper_03.jpg")
        string extension = Path.GetExtension(sourceFilePath);
        string fullDestPath = GetNextAssetFileName(nodeFolder, item.Title, type, extension);
        
        try
        {
            // Verzeichnis erstellen falls nicht existent (eigentlich schon in GetNextAssetFileName erledigt)
            string? dir = Path.GetDirectoryName(fullDestPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.Copy(sourceFilePath, fullDestPath, overwrite: true); // Overwrite true ist sicherer, falls Logikfehler, aber Name sollte unique sein

            // 4. Asset Objekt erstellen
            var newAsset = new MediaAsset
            {
                Type = type,
                RelativePath = Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, fullDestPath)
            };
            
            // Zur Liste hinzufügen
            item.Assets.Add(newAsset);
            return newAsset;
        }
        catch (Exception ex)
        {
            // Fehlerbehandlung / Logging (in einer echten App via Logger)
            Console.WriteLine($"Error importing asset: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Spezielle Import-Methode für MediaNodes (Kategorien), da diese auch Assets haben.
    /// </summary>
    public MediaAsset? ImportNodeAsset(string sourceFilePath, MediaNode node, List<string> nodePathStack, AssetType type)
    {
        if (!File.Exists(sourceFilePath)) return null;

        // Ordner: Library -> Games -> SNES
        string nodeFolder = Path.Combine(_libraryRootPath, Path.Combine(nodePathStack.ToArray()));
        
        // Dateiname basierend auf Node-Name (z.B. "SNES_Logo_01.png")
        string extension = Path.GetExtension(sourceFilePath);
        string fullDestPath = GetNextAssetFileName(nodeFolder, node.Name, type, extension);

        try
        {
            string? dir = Path.GetDirectoryName(fullDestPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            File.Copy(sourceFilePath, fullDestPath, overwrite: true);

            var newAsset = new MediaAsset
            {
                Type = type,
                RelativePath = Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, fullDestPath)
            };
            
            node.Assets.Add(newAsset);
            return newAsset;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error importing node asset: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Löscht ein Asset physisch von der Festplatte und entfernt es aus der Liste des Items.
    /// </summary>
    public void DeleteAsset(MediaItem item, MediaAsset asset)
    {
        if (asset == null) return;

        try
        {
            // Liste bereinigen
            item.Assets.Remove(asset);

            // Datei löschen
            if (!string.IsNullOrEmpty(asset.RelativePath))
            {
                string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, asset.RelativePath);
                
                // Cache leeren!
                Retromind.Helpers.AsyncImageHelper.InvalidateCache(fullPath); 

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting asset: {ex.Message}");
        }
    }

    /// <summary>
    /// Scannt das Verzeichnis einer Node nach Assets für ein Item und aktualisiert dessen Asset-Liste.
    /// Dies ist wichtig beim Programmstart oder Refresh, um manuell kopierte Dateien zu finden.
    /// </summary>
    public void RefreshItemAssets(MediaItem item, List<string> nodePathStack)
    {
        string nodeFolder = Path.Combine(_libraryRootPath, Path.Combine(nodePathStack.ToArray()));
        if (!Directory.Exists(nodeFolder)) return;

        // Strategie: Wir löschen die Liste und bauen sie neu auf, um absolut synchron zum Filesystem zu sein.
        item.Assets.Clear();

        var sanitizedTitle = SanitizeForFilename(item.Title);

        // Durch alle Asset-Typen (Ordner) iterieren
        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown) continue;

            string typeFolder = Path.Combine(nodeFolder, type.ToString());
            if (!Directory.Exists(typeFolder)) continue;

            var files = Directory.GetFiles(typeFolder);

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var match = AssetRegex.Match(fileName);

                if (match.Success)
                {
                    string fileTitle = match.Groups[1].Value; // "Super_Mario"
                    string fileTypeStr = match.Groups[2].Value; // "Wallpaper"
                    
                    // Prüfen ob Typ und Name exakt übereinstimmen (Case Insensitive für Filesystem-Sicherheit)
                    if (fileTitle.Equals(sanitizedTitle, StringComparison.OrdinalIgnoreCase) &&
                        fileTypeStr.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        item.Assets.Add(new MediaAsset
                        {
                            Type = type,
                            RelativePath = Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, file)
                        });
                    }
                }
            }
        }
    }

    // --- Private Helpers ---

    /// <summary>
    /// Ermittelt den nächsten freien Dateinamen gemäß Konvention: Titel_Typ_XX.ext
    /// </summary>
    private string GetNextAssetFileName(string nodeBaseFolder, string mediaTitle, AssetType type, string extension)
    {
        // Ziel: .../SNES/Wallpaper/
        string typeFolder = Path.Combine(nodeBaseFolder, type.ToString());
    
        if (!Directory.Exists(typeFolder))
            Directory.CreateDirectory(typeFolder);

        string cleanTitle = SanitizeForFilename(mediaTitle); 
        string suffix = type.ToString(); 

        int counter = 1;
        string fullPath;

        // Schleife sucht die nächste freie Nummer (01, 02, 03...)
        do
        {
            string number = counter.ToString("D2"); // Führende Null: 01
            string fileName = $"{cleanTitle}_{suffix}_{number}{extension}";
            fullPath = Path.Combine(typeFolder, fileName);
            counter++;
        } 
        while (File.Exists(fullPath));

        return fullPath;
    }

    private string SanitizeForFilename(string input)
    {
        if (string.IsNullOrEmpty(input)) return "Unknown";

        // Leerzeichen zu Underscore
        string sanitized = input.Replace(" ", "_");
    
        // Illegale Zeichen entfernen
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c.ToString(), "");
        }
    
        // Doppelte Underscores vermeiden (Kosmetik)
        while(sanitized.Contains("__")) 
            sanitized = sanitized.Replace("__", "_");

        return sanitized;
    }
}