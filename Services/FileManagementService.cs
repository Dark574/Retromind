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
    Music,
    Video,
    Marquee
}

public partial class FileManagementService
{
    private const string RootFolderName = "Medien";
    private const string MetadataFolderName = "_Metadata";

    [GeneratedRegex(@"([<>\:""/\\|?*]*\.+$)|([<>\:""/\\|?*]+)")]
    private static partial Regex InvalidCharsRegex();
    
    /// <summary>
    ///     Imports an asset file (Cover, Wallpaper, Music) into the portable structure.
    ///     If the file already exists, a counter (_01, _02) is appended.
    ///     Music files are now renamed to match the title (Title_Music_XX) to enable per-game shuffling.
    /// </summary>
    public string ImportAsset(string sourceFilePath, MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        // 1. Base directory of the application
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 2. Build target folder structure: Media/Area/Group/...
        var paths = new List<string> { RootFolderName };
        paths.AddRange(nodePath);
        paths.Add(fileType.ToString()); // Subfolder e.g. "Cover", "Music"

        var relativeDirectory = Path.Combine(paths.ToArray());
        var absoluteDirectory = Path.Combine(baseDir, relativeDirectory);

        // Create directory if it doesn't exist
        if (!Directory.Exists(absoluteDirectory)) Directory.CreateDirectory(absoluteDirectory);

        // 3. Generate target filename
        var extension = Path.GetExtension(sourceFilePath);
        var safeTitle = SanitizeFileName(item.Title);
        string baseFileName;
        
        // UNIFIED NAMING LOGIC:
        // Before, Music kept its original name, causing conflicts and shuffle issues.
        // Now, Music follows the same pattern: "Super_Mario_Bros_Music.mp3"
        // This allows us to strictly associate multiple tracks with one game.
        baseFileName = $"{safeTitle}_{fileType}";
        
        // Standard Name: e.g. "Super_Mario_Bros_Music.mp3"
        var newFileName = $"{baseFileName}{extension}";
        var destinationPath = Path.Combine(absoluteDirectory, newFileName);

        // VERSIONING: Check if file exists and increment counter
        int counter = 1;
        
        // Special case for Music: If we already have a track (Music.mp3), and import another,
        // we want the second one to be Music_01.mp3 immediately, or just stick to numbering always?
        // Your logic for covers does: Cover.jpg -> Cover_01.jpg if duplicate.
        // For music, it's nice to have "Music_01", "Music_02" explicitly if multiple exist.
        
        while (File.Exists(destinationPath))
        {
            // Check Content Hash (MD5) to avoid unnecessary duplicates
            if (AreFilesEqual(sourceFilePath, destinationPath))
            {
                return Path.Combine(relativeDirectory, newFileName);
            }
            
            // Name collision -> Increment
            newFileName = $"{baseFileName}_{counter:D2}{extension}";
            destinationPath = Path.Combine(absoluteDirectory, newFileName);
            counter++;
        }

        // 4. Copy
        try
        {
            File.Copy(sourceFilePath, destinationPath); 
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Error copying file: {ex.Message}");
            return string.Empty;
        }

        // 5. Return relative path
        return Path.Combine(relativeDirectory, newFileName);
    }

    /// <summary>
    /// Imports an asset for a MediaNode (Category/Platform).
    /// Stores it in a "_Metadata" subfolder inside the node's path.
    /// Naming: NodeName_FileType.ext (e.g. "SNES_Wallpaper.jpg")
    /// </summary>
    public string ImportNodeAsset(string sourceFilePath, MediaNode node, List<string> nodePath, MediaFileType fileType)
    {
        // 1. Base directory
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 2. Build target structure: Media/Area/Group/_Metadata
        var paths = new List<string> { RootFolderName };
        paths.AddRange(nodePath);
        paths.Add(MetadataFolderName); 

        var relativeDirectory = Path.Combine(paths.ToArray());
        var absoluteDirectory = Path.Combine(baseDir, relativeDirectory);

        if (!Directory.Exists(absoluteDirectory)) Directory.CreateDirectory(absoluteDirectory);

        // 3. Generate filename
        var extension = Path.GetExtension(sourceFilePath);
        var safeName = SanitizeFileName(node.Name);
        
        // Pattern: "SNES_Logo.png"
        var baseFileName = $"{safeName}_{fileType}";
        var newFileName = $"{baseFileName}{extension}";
        var destinationPath = Path.Combine(absoluteDirectory, newFileName);

        // VERSIONING (same logic as ImportAsset)
        int counter = 1;
        while (File.Exists(destinationPath))
        {
            if (AreFilesEqual(sourceFilePath, destinationPath))
            {
                return Path.Combine(relativeDirectory, newFileName);
            }
            
            newFileName = $"{baseFileName}_{counter:D2}{extension}";
            destinationPath = Path.Combine(absoluteDirectory, newFileName);
            counter++;
        }

        // 4. Copy
        try
        {
            File.Copy(sourceFilePath, destinationPath);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Error copying node asset: {ex.Message}");
            return string.Empty;
        }

        return Path.Combine(relativeDirectory, newFileName);
    }
    
    private bool AreFilesEqual(string source, string dest)
    {
        try
        {
            if (new FileInfo(source).Length != new FileInfo(dest).Length) return false;
            return FileHelper.CalculateMd5(source) == FileHelper.CalculateMd5(dest);
        }
        catch { return false; }
    }
    
    private string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";
        var safeName = InvalidCharsRegex().Replace(name, "_");
        safeName = safeName.Replace(" ", "_");
        return safeName;
    }

    public string? FindExistingAsset(MediaItem item, List<string> nodePath, MediaFileType fileType)
    {
        var assets = GetAvailableAssets(item, nodePath, fileType);
        return assets.FirstOrDefault();
    }

    /// <summary>
    ///     Returns ALL matching assets (e.g. Cover, Cover_01, Cover_02) for Randomization.
    ///     For Music, this now correctly returns only tracks associated with the specific game title.
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

        // Unified Search Pattern
        var safeTitle = SanitizeFileName(item.Title);
        var searchPattern = $"{safeTitle}_{fileType}*"; 

        // Note: This pattern matches "Mario_Music.mp3", "Mario_Music_01.mp3", etc.
        // It prevents finding "Zelda_Music.mp3".
        
        var foundFiles = Directory.GetFiles(absoluteDirectory, searchPattern)
            .Where(f => IsValidExtension(f, fileType))
            .OrderBy(f => f) 
            .ToList();

        return foundFiles.Select(f => Path.Combine(relativeDirectory, Path.GetFileName(f))).ToList();
    }
    
    private bool IsValidExtension(string path, MediaFileType type)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (type == MediaFileType.Music) return ext is ".mp3" or ".ogg" or ".wav" or ".flac" or ".sid";
        if (type == MediaFileType.Video) return ext is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov";
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";
    }
    
    public bool DeleteAsset(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
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