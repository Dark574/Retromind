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

        var localLaunchFile = TryGetExistingLocalFilePath(item);
        if (!string.IsNullOrWhiteSpace(localLaunchFile))
        {
            var romName = Path.GetFileNameWithoutExtension(localLaunchFile);
            if (!string.IsNullOrWhiteSpace(romName))
            {
                searchPrefixes.Add(romName);
            }
        }

        // 2. Collect Folders to Scan
        var foldersToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(localLaunchFile))
        {
            try
            {
                var fullRomPath = Path.GetFullPath(localLaunchFile);
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
            catch
            {
                // Ignore invalid paths
            }
        }

        // 3. Execute Scan (Performance Optimized)
        foreach (var dir in foldersToScan)
        {
            try
            {
                foreach (var prefix in searchPrefixes)
                {
                    var pattern = $"{prefix}*";

                    foreach (var file in Directory.EnumerateFiles(dir, pattern))
                    {
                        if (!validExtensions.Contains(Path.GetExtension(file))) continue;

                        if (distinctResults.Add(file))
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
    
    private static string? TryGetExistingLocalFilePath(MediaItem item)
    {
        // Prefer a real, existing local file path (not URLs, not commands like "steam").
        // This keeps MediaSearchHelper safe for Command-type items.
        if (item.Files is { Count: > 0 })
        {
            foreach (var f in item.Files)
            {
                if (f.Kind != MediaFileKind.Absolute)
                    continue;

                if (string.IsNullOrWhiteSpace(f.Path))
                    continue;

                // Only treat rooted paths as local files we can scan around.
                if (!Path.IsPathRooted(f.Path))
                    continue;

                if (File.Exists(f.Path))
                    return f.Path;
            }
        }

        // Fallback: primary path (might still be non-local); we only accept it if it looks like an existing rooted file.
        var primary = item.GetPrimaryLaunchPath();
        if (!string.IsNullOrWhiteSpace(primary) && Path.IsPathRooted(primary) && File.Exists(primary))
            return primary;

        return null;
    }
}