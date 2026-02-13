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
/// Enforces naming convention (Title__Id_Type_Number) and manages physical files.
/// </summary>
public class FileManagementService
{
    private const int ItemIdTokenLength = 8;
    private const string ItemIdSeparator = "__";

    // Regex for: Title__Id_Type_Number.Extension
    // Group 1: Prefix (e.g., "Super_Mario__A1B2C3D4")
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

    public static string BuildItemAssetPrefix(MediaItem item)
    {
        return BuildItemAssetPrefix(item?.Title, item?.Id);
    }

    public static string BuildItemAssetPrefix(string? title, string? itemId)
    {
        var cleanTitle = SanitizeForFilename(title ?? string.Empty);
        var token = GetItemIdToken(itemId);
        return $"{cleanTitle}{ItemIdSeparator}{token}";
    }

    private static string GetItemIdToken(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return "00000000";

        var cleaned = itemId.Replace("-", "", StringComparison.Ordinal).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return "00000000";

        if (cleaned.Length <= ItemIdTokenLength)
            return cleaned.ToUpperInvariant();

        return cleaned.Substring(0, ItemIdTokenLength).ToUpperInvariant();
    }
    /// <summary>
    /// Imports a file, renames it according to convention, and copies it to the correct folder.
    /// NOTE: This method is UI-agnostic and does NOT modify any ObservableCollections.
    /// The caller (ViewModel) must add the returned asset to the item's/node's Assets collection on the UI thread.
    /// </summary>
    public async Task<MediaAsset?> ImportAssetAsync(string sourceFilePath, object entity, List<string> nodePathStack, AssetType type)
    {
        if (!File.Exists(sourceFilePath)) return null;

        string assetPrefix = entity switch
        {
            MediaItem item => BuildItemAssetPrefix(item),
            MediaNode node => SanitizeForFilename(node.Name),
            _ => "Unknown"
        };
        string nodeFolder = ResolveNodeFolder(nodePathStack);

        string extension = Path.GetExtension(sourceFilePath);
        string fullDestPath = GetNextAssetFileName(nodeFolder, assetPrefix, type, extension, prefixIsSanitized: true);
        
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

        try
        {
            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting asset: {ex.Message}");
        }

        RaiseLibraryChanged();
    }

    public bool RenameItemAssets(MediaItem item, string oldTitle, string newTitle, List<string> nodePathStack)
    {
        if (item == null) return false;

        if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            return false;

        var oldPrefix = BuildItemAssetPrefix(oldTitle, item.Id);
        var newPrefix = BuildItemAssetPrefix(newTitle, item.Id);
        if (string.Equals(oldPrefix, newPrefix, StringComparison.OrdinalIgnoreCase))
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
                targetFileName = $"{newPrefix}_{asset.Type}_{number}{extension}";
            }
            else
            {
                targetFileName = Path.GetFileName(GetNextAssetFileName(nodeFolder, newPrefix, asset.Type, extension, prefixIsSanitized: true));
            }

            var targetFolder = Path.Combine(nodeFolder, asset.Type.ToString());
            var targetFullPath = Path.Combine(targetFolder, targetFileName);

            if (string.Equals(fullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(targetFullPath))
                targetFullPath = GetNextAssetFileName(nodeFolder, newPrefix, asset.Type, extension, prefixIsSanitized: true);

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

        var assetPrefix = BuildItemAssetPrefix(item);

        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown) continue;

            string typeFolder = Path.Combine(nodeFolder, type.ToString());
            if (!Directory.Exists(typeFolder)) continue;

            var files = Directory.EnumerateFiles(typeFolder)
                .Where(file => AssetRegex.IsMatch(Path.GetFileName(file)));

            foreach (var file in files)
            {
                var match = AssetRegex.Match(Path.GetFileName(file));
                if (match.Success &&
                    match.Groups[1].Value.Equals(assetPrefix, StringComparison.OrdinalIgnoreCase) &&
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

    public bool MigrateLegacyItemAssetsForNode(IReadOnlyList<MediaItem> items, List<string> nodePathStack)
    {
        if (items == null || items.Count == 0)
            return false;

        var nodeFolder = ResolveNodeFolder(nodePathStack);
        if (!Directory.Exists(nodeFolder))
            return false;

        bool anyChanged = false;

        var groups = items
            .GroupBy(item => SanitizeForFilename(item.Title), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToList();

        foreach (var group in groups)
        {
            var legacyPrefix = group.Key;
            var itemList = group.ToList();
            var isDuplicate = itemList.Count > 1;

            foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
            {
                if (type == AssetType.Unknown)
                    continue;

                var typeFolder = Path.Combine(nodeFolder, type.ToString());
                if (!Directory.Exists(typeFolder))
                    continue;

                var legacyFiles = Directory.EnumerateFiles(typeFolder)
                    .Select(path => (Path: path, Name: Path.GetFileName(path)))
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Name) && AssetRegex.IsMatch(entry.Name))
                    .ToList();

                foreach (var entry in legacyFiles)
                {
                    var match = AssetRegex.Match(entry.Name!);
                    if (!match.Success)
                        continue;

                    if (!string.Equals(match.Groups[1].Value, legacyPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var number = match.Groups[3].Value;
                    var extension = Path.GetExtension(entry.Name);

                    if (!isDuplicate)
                    {
                        var item = itemList[0];
                        var newPrefix = BuildItemAssetPrefix(item);
                        var targetPath = BuildTargetAssetPath(nodeFolder, typeFolder, newPrefix, type, number, extension);
                        if (string.Equals(entry.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (TryMoveAssetFile(entry.Path, targetPath))
                            anyChanged = true;

                        continue;
                    }

                    var primaryItem = itemList[0];
                    var primaryPrefix = BuildItemAssetPrefix(primaryItem);
                    var primaryTarget = BuildTargetAssetPath(nodeFolder, typeFolder, primaryPrefix, type, number, extension);

                    if (!TryMoveAssetFile(entry.Path, primaryTarget))
                        continue;

                    anyChanged = true;

                    foreach (var item in itemList.Skip(1))
                    {
                        var itemPrefix = BuildItemAssetPrefix(item);
                        var targetPath = BuildTargetAssetPath(nodeFolder, typeFolder, itemPrefix, type, number, extension);
                        if (File.Exists(targetPath))
                            targetPath = GetNextAssetFileName(nodeFolder, itemPrefix, type, extension, prefixIsSanitized: true);

                        if (TryCopyAssetFile(primaryTarget, targetPath))
                            anyChanged = true;
                    }
                }
            }
        }

        if (anyChanged)
            RaiseLibraryChanged();

        return anyChanged;
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
        {
            var rawFull = Path.GetFullPath(rawPath);
            var rootFull = Path.GetFullPath(libraryRootPath);
            var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (rawFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rawFull, rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return rawPath;
            }
        }

        return sanitizedPath;
    }

    /// <summary>
    /// Determines the next available filename according to convention: Prefix_Type_XX.ext
    /// </summary>
    private string GetNextAssetFileName(
        string nodeBaseFolder,
        string assetPrefix,
        AssetType type,
        string extension,
        bool prefixIsSanitized = false)
    {
        string typeFolder = Path.Combine(nodeBaseFolder, type.ToString());
    
        if (!Directory.Exists(typeFolder))
            Directory.CreateDirectory(typeFolder);

        string cleanTitle = prefixIsSanitized ? assetPrefix : SanitizeForFilename(assetPrefix);
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

    private string BuildTargetAssetPath(
        string nodeBaseFolder,
        string typeFolder,
        string assetPrefix,
        AssetType type,
        string number,
        string extension)
    {
        if (!string.IsNullOrWhiteSpace(number))
        {
            var candidateName = $"{assetPrefix}_{type}_{number}{extension}";
            var candidatePath = Path.Combine(typeFolder, candidateName);
            if (!File.Exists(candidatePath))
                return candidatePath;
        }

        return GetNextAssetFileName(nodeBaseFolder, assetPrefix, type, extension, prefixIsSanitized: true);
    }

    private static bool TryMoveAssetFile(string sourcePath, string targetPath)
    {
        try
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Move(sourcePath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error moving legacy asset: {ex.Message}");
            return false;
        }
    }

    private static bool TryCopyAssetFile(string sourcePath, string targetPath)
    {
        try
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(sourcePath, targetPath, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error copying legacy asset: {ex.Message}");
            return false;
        }
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
