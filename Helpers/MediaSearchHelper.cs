using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public static class MediaSearchHelper
{
    // Gemeinsame Logik für Ordner-Suche
    private static List<string> FindFiles(MediaItem item, string[] extensions, IEnumerable<string?> knownPaths)
    {
        var distinctPaths = new HashSet<string>();
        var results = new List<string>();
        var searchPatterns = new List<string>();

        // 1. Muster: Titel
        if (!string.IsNullOrWhiteSpace(item.Title))
            searchPatterns.Add(item.Title.Replace(" ", "_"));

        // 2. Muster: ROM Name
        if (!string.IsNullOrEmpty(item.FilePath))
        {
            var romName = Path.GetFileNameWithoutExtension(item.FilePath);
            if (!string.IsNullOrEmpty(romName)) searchPatterns.Add(romName);
        }

        // 3. Muster: Bekannte Pfade
        foreach (var path in knownPaths) AddPatternFromPath(path, searchPatterns);

        searchPatterns = searchPatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 4. Ordner sammeln
        var foldersToScan = new HashSet<string>();
        
        if (!string.IsNullOrEmpty(item.FilePath))
        {
            var romDir = Path.GetDirectoryName(Path.GetFullPath(item.FilePath));
            if (!string.IsNullOrEmpty(romDir))
            {
                foldersToScan.Add(romDir);
                var mediaDir = Path.Combine(romDir, "media");
                if (Directory.Exists(mediaDir)) foldersToScan.Add(mediaDir);
            }
        }

        foreach (var path in knownPaths) AddFolderFromPath(path, foldersToScan);

        // 5. Scannen
        foreach (var dir in foldersToScan)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;

            try
            {
                var files = Directory.GetFiles(dir);
                foreach (var file in files)
                {
                    if (!extensions.Contains(Path.GetExtension(file).ToLower())) continue;
                    if (distinctPaths.Contains(file.ToLower())) continue;

                    var fileName = Path.GetFileName(file);
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
                        distinctPaths.Add(file.ToLower());
                    }
                }
            }
            catch { }
        }
        return results;
    }

    public static List<string> FindPotentialImages(MediaItem item)
    {
        var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
        var known = new[] { item.CoverPath, item.WallpaperPath, item.LogoPath };
        return FindFiles(item, extensions, known);
    }

    // NEU: Suche nach Musik
    public static List<string> FindPotentialAudio(MediaItem item)
    {
        var extensions = new[] { ".mp3", ".ogg", ".wav", ".flac", ".wma", ".m4a" };
        var known = new[] { item.MusicPath };
        return FindFiles(item, extensions, known);
    }

    private static void AddPatternFromPath(string? path, List<string> patterns)
    {
        if (string.IsNullOrEmpty(path)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrEmpty(name)) patterns.Add(name);
    }

    private static void AddFolderFromPath(string? path, HashSet<string> folders)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var fullPath = Path.IsPathRooted(path) 
                ? path 
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath));
            if (!string.IsNullOrEmpty(dir)) folders.Add(dir);
        }
        catch { }
    }
}