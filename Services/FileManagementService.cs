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
    // Group 2: Type (e.g., "Wallpaper", "Manual")
    // Group 3: Number (e.g., "01")
    private static readonly Regex AssetRegex = new Regex(
        @"^(.+)_(Wallpaper|Cover|Logo|Video|Marquee|Music|Banner|Bezel|ControlPanel|Manual)_(\d+)\..*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            // Event handlers should never cause file operations to crash
        }
    }
    /// <summary>
    /// Imports a file, renames it according to convention, and copies it to the correct folder.
    /// NOTE: This method is UI-agnostic and does NOT modify any ObservableCollections.
    /// The caller (ViewModel) must add the returned asset to the item's/node's Assets collection on the UI thread.
    /// </summary>
    public async Task<MediaAsset?> ImportAssetAsync(string sourceFilePath, object entity, List<string> nodePathStack, AssetType type)
    {
        if (!File.Exists(sourceFilePath)) return null;

        string entityName = entity is MediaItem item ? item.Title : (entity as MediaNode)?.Name ?? "Unknown";
        string nodeFolder = ResolveNodeFolder(nodePathStack);

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
    /// Deletes an asset physically from disk.
    /// NOTE: This method is UI-agnostic and does NOT remove the asset from any collections.
    /// The caller (ViewModel) is responsible for removing/re-adding in case of failure.
    /// </summary>
    public void DeleteAssetFile(MediaAsset asset)
    {
        if (asset == null) return;

        if (string.IsNullOrWhiteSpace(asset.RelativePath))
            return;

        string fullPath = AppPaths.ResolveDataPath(asset.RelativePath);

        Helpers.AsyncImageHelper.InvalidateCache(fullPath);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        RaiseLibraryChanged();
    }

    public bool RenameItemAssets(MediaItem item, string oldTitle, string newTitle, List<string> nodePathStack)
    {
        if (item == null) return false;

        if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            return false;

        var oldSafeTitle = SanitizeForFilename(oldTitle);
        var newSafeTitle = SanitizeForFilename(newTitle);
        if (string.Equals(oldSafeTitle, newSafeTitle, StringComparison.Ordinal))
            return false;

        var nodeFolder = ResolveNodeFolder(nodePathStack);
        if (!Directory.Exists(nodeFolder))
            return false;

        bool anyRenamed = false;
        var assetsSnapshot = item.Assets.ToList();

        foreach (var asset in assetsSnapshot)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.RelativePath))
                continue;

            var fullPath = AppPaths.ResolveDataPath(asset.RelativePath);
            if (!File.Exists(fullPath))
                continue;

            var extension = Path.GetExtension(fullPath);
            if (string.IsNullOrEmpty(extension))
                extension = ".bin";

            var fileName = Path.GetFileName(fullPath);
            var match = AssetRegex.Match(fileName);

            string targetFileName;
            if (match.Success)
            {
                var number = match.Groups[3].Value;
                targetFileName = $"{newSafeTitle}_{asset.Type}_{number}{extension}";
            }
            else
            {
                targetFileName = Path.GetFileName(GetNextAssetFileName(nodeFolder, newTitle, asset.Type, extension));
            }

            var targetFolder = Path.Combine(nodeFolder, asset.Type.ToString());
            var targetFullPath = Path.Combine(targetFolder, targetFileName);

            if (string.Equals(fullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(targetFullPath))
            {
                targetFullPath = GetNextAssetFileName(nodeFolder, newTitle, asset.Type, extension);
            }

            try
            {
                var targetDir = Path.GetDirectoryName(targetFullPath);
                if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                AsyncImageHelper.InvalidateCache(fullPath);
                File.Move(fullPath, targetFullPath);

                asset.RelativePath = Path.GetRelativePath(AppPaths.DataRoot, targetFullPath);
                anyRenamed = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error renaming asset: {ex.Message}");
            }
        }

        if (anyRenamed)
            RaiseLibraryChanged();

        return anyRenamed;
    }
    
    /// <summary>
    /// Scans the filesystem for assets of a given item and returns the current list.
    /// This method does NOT modify any ObservableCollections (safe to run off the UI thread).
    /// </summary>
    public List<MediaAsset> ScanItemAssets(MediaItem item, List<string> nodePathStack)
    {
        var results = new List<MediaAsset>();
        if (item == null) return results;

        string nodeFolder = ResolveNodeFolder(nodePathStack);
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
    

    // --- Private Helpers ---
    private string ResolveNodeFolder(List<string> nodePathStack)
    {
        var rawPath = Path.Combine(libraryRootPath, Path.Combine(nodePathStack.ToArray()));

        var sanitizedStack = nodePathStack
            .Select(PathHelper.SanitizePathSegment)
            .ToArray();
        var sanitizedPath = Path.Combine(libraryRootPath, Path.Combine(sanitizedStack));

        if (string.Equals(rawPath, sanitizedPath, StringComparison.Ordinal))
            return rawPath;

        // Prefer existing raw paths for backward compatibility.
        if (Directory.Exists(rawPath))
            return rawPath;

        return sanitizedPath;
    }

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
