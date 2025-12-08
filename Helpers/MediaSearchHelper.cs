using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to search for media assets (images, audio) related to a specific media item.
/// It scans related directories (ROM folder, "media" subfolder, etc.) using file system patterns.
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
        return FindFiles(item, ImageExtensions);
    }

    /// <summary>
    /// Finds potential background music for the given item.
    /// </summary>
    public static List<string> FindPotentialAudio(MediaItem item)
    {
        return FindFiles(item, AudioExtensions);
    }

    /// <summary>
    /// Scans directories efficiently using OS globbing patterns instead of iterating all files.
    /// </summary>
    private static List<string> FindFiles(MediaItem item, HashSet<string> validExtensions)
    {
        var results = new List<string>();
        var distinctResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Case-insensitive deduplication
        
        // 1. Collect Search Patterns (e.g., "Super_Mario", "Super Mario World")
        var searchPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            // Pattern A: "Super_Mario" (Sanitized)
            searchPrefixes.Add(item.Title.Replace(" ", "_"));
            // Pattern B: "Super Mario" (Original - risky on some FS, but good for matching)
            searchPrefixes.Add(item.Title);
        }

        if (!string.IsNullOrEmpty(item.FilePath))
        {
            var romName = Path.GetFileNameWithoutExtension(item.FilePath);
            if (!string.IsNullOrWhiteSpace(romName)) 
            {
                searchPrefixes.Add(romName);
            }
        }

        // 2. Collect Folders to Scan
        var foldersToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        if (!string.IsNullOrEmpty(item.FilePath))
        {
            try 
            {
                // Resolve full path (important if FilePath is relative)
                var fullRomPath = Path.GetFullPath(item.FilePath);
                var romDir = Path.GetDirectoryName(fullRomPath);
                
                if (!string.IsNullOrEmpty(romDir) && Directory.Exists(romDir))
                {
                    foldersToScan.Add(romDir);
                    
                    // Common subfolder convention
                    var subfolders = new[] { "media", "images", "art", "music", "sound" };
                    foreach (var sub in subfolders)
                    {
                        var subPath = Path.Combine(romDir, sub);
                        if (Directory.Exists(subPath)) foldersToScan.Add(subPath);
                    }
                }
            }
            catch { /* Ignore invalid paths */ }
        }

        // 3. Execute Scan (Performance Optimized)
        // Optimization: Instead of loading ALL files, we iterate and filter efficiently using OS globbing.
        foreach (var dir in foldersToScan)
        {
            try
            {
                foreach (var prefix in searchPrefixes)
                {
                    // OS Optimized Search: Let the file system filter by prefix
                    // This avoids iterating 30,000 files in C#.
                    var pattern = $"{prefix}*"; 
                    
                    foreach (var file in Directory.EnumerateFiles(dir, pattern))
                    {
                        // Fast Extension Check
                        if (!validExtensions.Contains(Path.GetExtension(file))) continue;
                        
                        // Deduplication Check
                        if (distinctResults.Add(file)) // Returns true if added (deduplication)
                        {
                            results.Add(file);
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* Skip system folders */ }
            catch (Exception) { /* Skip other errors */ }
        }

        return results;
    }
}