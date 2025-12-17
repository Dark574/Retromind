using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Central service for file operations.
/// Enforces naming convention (Name_Type_Number) and manages physical files.
/// </summary>
public class FileManagementService
{
    // Regex for: Name_Type_Number.Extension
    // Group 1: Name (e.g., "Super_Mario")
    // Group 2: Type (e.g., "Wallpaper")
    // Group 3: Number (e.g., "01")
    private static readonly Regex AssetRegex = new Regex(@"^(.+)_(Wallpaper|Cover|Logo|Video|Marquee|Music)_(\d+)\..*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string libraryRootPath; // Readonly for immutability

    public FileManagementService(string libraryRootPath)
    {
        this.libraryRootPath = libraryRootPath;
    }

    public event Action? LibraryChanged;

    private void RaiseLibraryChanged()
    {
        try
        {
            LibraryChanged?.Invoke();
        }
        catch
        {
            // Event-Handler sollen File-Operationen niemals crashen lassen.
        }
    }
    /// <summary>
    /// Imports a file, renames it according to convention, and copies it to the correct folder.
    /// Adds it directly to the asset list of the item or node.
    /// </summary>
    /// <param name="sourceFilePath">Source file path.</param>
    /// <param name="entity">MediaItem or MediaNode to add asset to.</param>
    /// <param name="nodePathStack">Path stack for folder.</param>
    /// <param name="type">Asset type.</param>
    /// <returns>The new MediaAsset or null on failure.</returns>
    public async Task<MediaAsset?> ImportAssetAsync(string sourceFilePath, object entity, List<string> nodePathStack, AssetType type)
    {
        if (!File.Exists(sourceFilePath)) return null;

        string entityName = entity is MediaItem item ? item.Title : (entity as MediaNode)?.Name ?? "Unknown";
        string nodeFolder = Path.Combine(libraryRootPath, Path.Combine(nodePathStack.ToArray()));

        string extension = Path.GetExtension(sourceFilePath);
        string fullDestPath = GetNextAssetFileName(nodeFolder, entityName, type, extension);
        
        try
        {
            string? dir = Path.GetDirectoryName(fullDestPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await Task.Run(() => File.Copy(sourceFilePath, fullDestPath, overwrite: true)); // Async offload for IO

            var newAsset = new MediaAsset
            {
                Type = type,
                RelativePath = Path.GetRelativePath(AppPaths.DataRoot, fullDestPath)
            };

            if (entity is MediaItem mediaItem)
            {
                mediaItem.Assets.Add(newAsset);
            }
            else if (entity is MediaNode mediaNode)
            {
                mediaNode.Assets.Add(newAsset);
            }

            RaiseLibraryChanged();
            return newAsset;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error importing asset: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Deletes an asset physically from disk and removes it from the item's list.
    /// </summary>
    /// <param name="item">MediaItem containing the asset.</param>
    /// <param name="asset">Asset to delete.</param>
    public void DeleteAsset(MediaItem item, MediaAsset asset)
    {
        if (asset == null) return;

        try
        {
            // Remove from list first (transaction-like)
            item.Assets.Remove(asset);

            if (!string.IsNullOrEmpty(asset.RelativePath))
            {
                string fullPath = AppPaths.ResolveDataPath(asset.RelativePath);

                Helpers.AsyncImageHelper.InvalidateCache(fullPath);

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            
            RaiseLibraryChanged();
        }
        catch (Exception ex)
        {
            // Rollback: Re-add to list on failure
            item.Assets.Add(asset);
            Console.WriteLine($"Error deleting asset: {ex.Message}");
        }
    }

    /// <summary>
    /// Scans the filesystem for assets of a given item and returns the current list.
    /// This method does NOT modify any ObservableCollections (safe to run off the UI thread).
    /// </summary>
    public List<MediaAsset> ScanItemAssets(MediaItem item, List<string> nodePathStack)
    {
        var results = new List<MediaAsset>();
        if (item == null) return results;

        string nodeFolder = Path.Combine(libraryRootPath, Path.Combine(nodePathStack.ToArray()));
        if (!Directory.Exists(nodeFolder)) return results;

        var sanitizedTitle = SanitizeForFilename(item.Title);

        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown) continue;

            string typeFolder = Path.Combine(nodeFolder, type.ToString());
            if (!Directory.Exists(typeFolder)) continue;

            var files = Directory.EnumerateFiles(typeFolder)
                .Where(file => AssetRegex.IsMatch(Path.GetFileName(file)) &&
                               Path.GetFileName(file).StartsWith(sanitizedTitle + "_", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                var match = AssetRegex.Match(Path.GetFileName(file));
                if (match.Success &&
                    match.Groups[1].Value.Equals(sanitizedTitle, StringComparison.OrdinalIgnoreCase) &&
                    match.Groups[2].Value.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new MediaAsset
                    {
                        Type = type,
                        RelativePath = Path.GetRelativePath(AppPaths.DataRoot, file)
                    });
                }
            }
        }

        return results;
    }
    
    /// <summary>
    /// Scans the node's directory for assets and updates the item's asset list.
    /// Clears and rebuilds the list to sync with filesystem.
    /// </summary>
    /// <param name="item">MediaItem to refresh.</param>
    /// <param name="nodePathStack">Path stack for folder.</param>
    public void RefreshItemAssets(MediaItem item, List<string> nodePathStack)
    {
        string nodeFolder = Path.Combine(libraryRootPath, Path.Combine(nodePathStack.ToArray()));
        if (!Directory.Exists(nodeFolder)) return;

        item.Assets.Clear();
        var sanitizedTitle = SanitizeForFilename(item.Title);

        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown) continue;

            string typeFolder = Path.Combine(nodeFolder, type.ToString());
            if (!Directory.Exists(typeFolder)) continue;

            // Enumerate with filter for performance (avoid loading all files)
            var files = Directory.EnumerateFiles(typeFolder)
                .Where(file => AssetRegex.IsMatch(Path.GetFileName(file)) && Path.GetFileName(file).StartsWith(sanitizedTitle + "_", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                var match = AssetRegex.Match(Path.GetFileName(file));
                if (match.Success && match.Groups[1].Value.Equals(sanitizedTitle, StringComparison.OrdinalIgnoreCase) &&
                    match.Groups[2].Value.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    item.Assets.Add(new MediaAsset
                    {
                        Type = type,
                        RelativePath = Path.GetRelativePath(AppPaths.DataRoot, file)
                    });
                }
            }
        }
    }

    // --- Private Helpers ---

    /// <summary>
    /// Determines the next available filename according to convention: Title_Type_XX.ext
    /// </summary>
    private string GetNextAssetFileName(string nodeBaseFolder, string mediaTitle, AssetType type, string extension)
    {
        string typeFolder = Path.Combine(nodeBaseFolder, type.ToString());
    
        if (!Directory.Exists(typeFolder))
            Directory.CreateDirectory(typeFolder);

        string cleanTitle = SanitizeForFilename(mediaTitle); 
        string suffix = type.ToString(); 

        // Collect existing counters for efficiency (instead of loop checks)
        var existingFiles = Directory.EnumerateFiles(typeFolder)
            .Select(Path.GetFileName)
            .Where(name => name?.StartsWith($"{cleanTitle}_{suffix}_", StringComparison.OrdinalIgnoreCase) == true)
            .Select(name => int.TryParse(Regex.Match(name ?? "", @"_(\d+)\.").Groups[1].Value, out int num) ? num : 0);

        int maxCounter = existingFiles.Any() ? existingFiles.Max() : 0;
        int nextCounter = maxCounter + 1;
        string number = nextCounter.ToString("D2");
        string fileName = $"{cleanTitle}_{suffix}_{number}{extension}";

        return Path.Combine(typeFolder, fileName);
    }

    /// <summary>
    /// Sanitizes a string for use in filenames (replaces spaces with underscores, removes invalid chars).
    /// </summary>
    private static string SanitizeForFilename(string input)
    {
        if (string.IsNullOrEmpty(input)) return "Unknown";

        string sanitized = input.Replace(" ", "_");

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c.ToString(), "");
        }

        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");

        return sanitized;
    }
}