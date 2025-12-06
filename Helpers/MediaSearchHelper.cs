using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to search for media assets (images, audio) related to a specific media item.
/// It scans related directories (ROM folder, "media" subfolder, etc.) for files matching the item's title.
/// </summary>
public static class MediaSearchHelper
{
    // Common image extensions
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" 
    };

    // Common audio extensions (Added .sid based on recent changes)
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) 
    { 
        ".mp3", ".ogg", ".wav", ".flac", ".wma", ".m4a", ".sid" 
    };

    /// <summary>
    /// Finds potential images (Covers, Wallpapers, Logos) for the given item.
    /// </summary>
    public static List<string> FindPotentialImages(MediaItem item)
    {
        var knownPaths = new[] { item.CoverPath, item.WallpaperPath, item.LogoPath };
        return FindFiles(item, ImageExtensions, knownPaths);
    }

    /// <summary>
    /// Finds potential background music for the given item.
    /// </summary>
    public static List<string> FindPotentialAudio(MediaItem item)
    {
        var knownPaths = new[] { item.MusicPath };
        return FindFiles(item, AudioExtensions, knownPaths);
    }

    /// <summary>
    /// Core logic to scan directories for matching files.
    /// </summary>
    private static List<string> FindFiles(MediaItem item, HashSet<string> validExtensions, IEnumerable<string?> knownPaths)
    {
        var results = new List<string>();
        var distinctResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Case-insensitive deduplication
        
        // 1. Collect Search Patterns (e.g., "Super_Mario", "Super Mario World")
        var searchPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            // Pattern A: "Super_Mario" (Sanitized)
            searchPatterns.Add(item.Title.Replace(" ", "_"));
            // Pattern B: "Super Mario" (Original - risky on some FS, but good for matching)
            searchPatterns.Add(item.Title);
        }

        if (!string.IsNullOrEmpty(item.FilePath))
        {
            var romName = Path.GetFileNameWithoutExtension(item.FilePath);
            if (!string.IsNullOrWhiteSpace(romName)) searchPatterns.Add(romName);
        }

        // Add names from already linked assets to find siblings (e.g. if Cover is "Img_01", maybe "Img_02" is Wallpaper)
        foreach (var path in knownPaths) AddPatternFromPath(path, searchPatterns);

        // 2. Collect Folders to Scan
        var foldersToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        if (!string.IsNullOrEmpty(item.FilePath))
        {
            try 
            {
                var romDir = Path.GetDirectoryName(Path.GetFullPath(item.FilePath));
                if (!string.IsNullOrEmpty(romDir) && Directory.Exists(romDir))
                {
                    foldersToScan.Add(romDir);
                    
                    // Common subfolder convention
                    var mediaDir = Path.Combine(romDir, "media");
                    if (Directory.Exists(mediaDir)) foldersToScan.Add(mediaDir);
                }
            }
            catch { /* Ignore invalid paths */ }
        }

        foreach (var path in knownPaths) AddFolderFromPath(path, foldersToScan);

        // 3. Execute Scan
        // Optimization: Instead of loading ALL files, we iterate and filter efficiently.
        foreach (var dir in foldersToScan)
        {
            try
            {
                // EnumerateFiles is lazy (better memory usage than GetFiles)
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    // Fast Extension Check
                    if (!validExtensions.Contains(Path.GetExtension(file))) continue;
                    
                    // Deduplication Check
                    if (distinctResults.Contains(file)) continue;

                    var fileName = Path.GetFileName(file);
                    
                    // Pattern Matching
                    // We use "StartsWith" to allow suffixes like "_Cover", "_01", etc.
                    bool match = false;
                    foreach (var pattern in searchPatterns)
                    {
                        if (fileName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true; 
                            break;
                        }
                    }

                    if (match)
                    {
                        results.Add(file);
                        distinctResults.Add(file);
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* Skip system folders */ }
            catch (Exception) { /* Skip other errors */ }
        }

        return results;
    }

    private static void AddPatternFromPath(string? path, HashSet<string> patterns)
    {
        if (string.IsNullOrEmpty(path)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        // Optimization: Only add if it looks like a meaningful name (e.g. longer than 2 chars)
        if (!string.IsNullOrEmpty(name) && name.Length > 2) patterns.Add(name);
    }

    private static void AddFolderFromPath(string? path, HashSet<string> folders)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // Handle relative paths correctly
            var fullPath = Path.IsPathRooted(path) 
                ? path 
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            
            var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) 
                folders.Add(dir);
        }
        catch { }
    }
}