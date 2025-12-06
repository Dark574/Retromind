using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

public enum MediaFileType
{
    Cover,
    Logo,
    Wallpaper,
    Music
}

public partial class FileManagementService
{
    private const string RootFolderName = "Medien";

    [GeneratedRegex(@"([<>\:""/\\|?*]*\.+$)|([<>\:""/\\|?*]+)")]
    private static partial Regex InvalidCharsRegex();
    
    /// <summary>
    ///     Imports an asset file (Cover, Wallpaper) into the portable structure.
    ///     If the file already exists, a counter (_01, _02) is appended.
    /// </summary>
    public string ImportAsset(string sourceFilePath, MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        // 1. Base directory of the application
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 2. Build target folder structure: Media/Area/Group/...
        var paths = new List<string> { RootFolderName };
        paths.AddRange(nodePath);
        paths.Add(fileType.ToString()); // Unterordner z.B. "Cover"

        var relativeDirectory = Path.Combine(paths.ToArray());
        var absoluteDirectory = Path.Combine(baseDir, relativeDirectory);

        // Create directory if it doesn't exist
        if (!Directory.Exists(absoluteDirectory)) Directory.CreateDirectory(absoluteDirectory);

        // 3. Generate target filename
        var extension = Path.GetExtension(sourceFilePath);
        string baseFileName;
        
        if (fileType == MediaFileType.Music)
        {
            // Logic for music: keep the Original name
            var originalName = Path.GetFileNameWithoutExtension(sourceFilePath);
            baseFileName = SanitizeFileName(originalName);
        }
        else
        {
            // Logic for graphics: Titel_Type
            var safeTitle = SanitizeFileName(item.Title);
            baseFileName = $"{safeTitle}_{fileType}";
        }
        
        // Standard Name: e.g. "Super_Mario_Bros_Cover.jpg"
        var newFileName = $"{baseFileName}{extension}";
        var destinationPath = Path.Combine(absoluteDirectory, newFileName);

        // VERSIONING: Check if file exists and increment counter
        int counter = 1;
        while (File.Exists(destinationPath))
        {
            // Check Content Hash (MD5) to avoid unnecessary duplicates
            // If the content is identical we deliver the existing file
            if (AreFilesEqual(sourceFilePath, destinationPath))
            {
                return Path.Combine(relativeDirectory, newFileName);
            }
            
            // Name collision but content is different -> Increment
            newFileName = $"{baseFileName}_{counter:D2}{extension}";
            destinationPath = Path.Combine(absoluteDirectory, newFileName);
            counter++;
        }

        // 4. Copy
        try
        {
            File.Copy(sourceFilePath, destinationPath); // No overwrite needed
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Fehler beim Kopieren: {ex.Message}");
            return string.Empty;
        }

        // 5. Return relative path
        return Path.Combine(relativeDirectory, newFileName);
    }

    private bool AreFilesEqual(string source, string dest)
    {
        try
        {
            // Simple size check first
            if (new FileInfo(source).Length != new FileInfo(dest).Length) return false;
            // Then MD5
            return FileHelper.CalculateMd5(source) == FileHelper.CalculateMd5(dest);
        }
        catch { return false; }
    }
    
    /// <summary>
    ///     Imports an asset file (Cover, Wallpaper) into the portable structure.
    ///     Removes invalid characters and replaces spaces with underscores.
    /// </summary>
    private string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";
        
        // Use the generated regex
        var safeName = InvalidCharsRegex().Replace(name, "_");

        // Replace spaces with underscores (optional, but often nicer on Linux)
        safeName = safeName.Replace(" ", "_");

        return safeName;
    }

    /// <summary>
    ///     Imports an asset file (Cover, Wallpaper) into the portable structure.
    /// </summary>
    public string? FindExistingAsset(MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        // Nutzen wir die neue Methode, um Code-Duplizierung zu vermeiden
        var assets = GetAvailableAssets(item, nodePath, fileType);
        return assets.FirstOrDefault();
    }

    /// <summary>
    ///     Returns ALL matching assets (e.g. Cover, Cover_01, Cover_02) for Randomization.
    /// </summary>
    public List<string> GetAvailableAssets(MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var paths = new List<string> { RootFolderName };
        paths.AddRange(nodePath);
        paths.Add(fileType.ToString());

        var relativeDirectory = Path.Combine(paths.ToArray());
        var absoluteDirectory = Path.Combine(baseDir, relativeDirectory);

        if (!Directory.Exists(absoluteDirectory)) return new List<string>();

        string searchPattern;
        
        if (fileType == MediaFileType.Music)
        {
            // Bei Musik ist es schwieriger, da der Name variieren kann.
            // Wir könnten alles im Ordner nehmen oder versuchen intelligent zu matchen.
            // Da Musikdateien oft eigene Namen haben, nehmen wir hier ALLE Audiofiles im Ordner,
            // oder wir müssten wissen, wie die Datei heißt.
            // VEREINFACHUNG FÜR ZUFALL: Wir nehmen alle Audio-Dateien im Musik-Ordner dieser Gruppe.
            searchPattern = "*.*"; 
        }
        else
        {
            var safeTitle = SanitizeFileName(item.Title);
            searchPattern = $"{safeTitle}_{fileType}*"; 
        }

        var foundFiles = Directory.GetFiles(absoluteDirectory, searchPattern)
            .Where(f => IsValidExtension(f, fileType))
            .OrderBy(f => f) // Deterministic order
            .ToList();

        return foundFiles.Select(f => Path.Combine(relativeDirectory, Path.GetFileName(f))).ToList();
    }
    
    private bool IsValidExtension(string path, MediaFileType type)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (type == MediaFileType.Music) return ext is ".mp3" or ".ogg" or ".wav" or ".flac" or ".sid";
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";
    }
    
    /// <summary>
    ///     Deletes a file if it is located within the application's base directory.
    /// </summary>
    public bool DeleteAsset(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                
            // Security Check: Only delete files inside our app directory
            if (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) 
                return false;

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting file: {ex.Message}");
        }
        return false;
    }
}