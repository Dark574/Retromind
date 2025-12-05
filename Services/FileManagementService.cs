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
        var safeTitle = SanitizeFileName(item.Title);

        // Base: e.g. "Super_Mario_Bros_Cover"
        var baseFileName = $"{safeTitle}_{fileType}";
        
        // Standard Name: e.g. "Super_Mario_Bros_Cover.jpg"
        var newFileName = $"{baseFileName}{extension}";
        var destinationPath = Path.Combine(absoluteDirectory, newFileName);

        // VERSIONING: Check if file exists and increment counter
        int counter = 1;
        while (File.Exists(destinationPath))
        {
            // We don't check content hash here, we just keep everything.
            // New Name: e.g. "Super_Mario_Bros_Cover_01.jpg"
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

    /// <summary>
    ///     Removes invalid characters and replaces spaces with underscores.
    /// </summary>
    private string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Unknown";

        // Remove invalid characters for filenames
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

        var safeName = Regex.Replace(name, invalidRegStr, "_");

        // Replace spaces with underscores (optional, but often nicer on Linux)
        safeName = safeName.Replace(" ", "_");

        return safeName;
    }

    /// <summary>
    ///     Searches for an existing asset for the item (e.g. manually copied in).
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

        // We search for files starting with "Title_Type".
        // e.g. "Super_Mario_Cover.jpg" or "Super_Mario_Cover.png"
        var safeTitle = SanitizeFileName(item.Title);
        var searchPattern = $"{safeTitle}_{fileType}*"; // Wildcard at end for _01 etc.

        // Directory.GetFiles returns full paths
        var foundFiles = Directory.GetFiles(absoluteDirectory, searchPattern);

        if (foundFiles.Length > 0)
        {
            // Take the first match
            var fullPath = foundFiles[0];
            var fileName = Path.GetFileName(fullPath);

            // Return relative path
            return Path.Combine(relativeDirectory, fileName);
        }

        return null;
    }
}