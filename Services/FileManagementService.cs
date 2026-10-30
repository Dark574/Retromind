using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Central service for file operations.
/// Enforces naming convention (Title__Id_Type_Number).
/// Manual/Music use Display__RM__Title__Id_Type_Number to preserve the original filename.
/// </summary>
public partial class FileManagementService
{
    private const int ItemIdTokenLength = 8;
    private const string ItemIdSeparator = "__";

    // Regex for: Title__Id_Type_Number.Extension
    // Manual/Music may use: Display__RM__Title__Id_Type_Number.Extension
    // Group 1: Prefix (e.g., "Super_Mario__A1B2C3D4")
    // Group 2: Type (e.g., "Wallpaper", "Manual")
    // Group 3: Number (e.g., "01")
    private static readonly Regex AssetRegex = new Regex(
        @"^(.+)_(Wallpaper|Cover|Logo|Video|Marquee|Music|Banner|Bezel|ControlPanel|Manual|Screenshot)_(\d+)\..*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string libraryRootPath; // Readonly for immutability
    private readonly IErrorService _errorService;
    private readonly Func<string, Stream> _openAssetSource;
    private readonly Action<string> _deleteQuarantineDirectory;

    public FileManagementService(string libraryRootPath, IErrorService errorService)
        : this(libraryRootPath, errorService, null)
    {
    }

    internal FileManagementService(string libraryRootPath, IErrorService errorService,
        Func<string, Stream>? openAssetSource, Action<string>? deleteQuarantineDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRootPath);
        this.libraryRootPath = libraryRootPath;
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
        _openAssetSource = openAssetSource ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        _deleteQuarantineDirectory = deleteQuarantineDirectory ?? (path => Directory.Delete(path, recursive: true));
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

    private static string BuildItemAssetPrefixForAsset(MediaItem item, AssetType type, string? displayName)
    {
        var basePrefix = BuildItemAssetPrefix(item);
        if (!AssetNameHelper.RequiresDisplayPrefix(type))
            return basePrefix;

        var cleanDisplay = SanitizeForFilename(displayName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleanDisplay))
            cleanDisplay = "Unknown";

        return AssetNameHelper.BuildDisplayPrefix(cleanDisplay, basePrefix);
    }

    private static string BuildItemAssetPrefixForRename(MediaItem item, AssetType type, string newTitle, string? existingPrefix)
    {
        var basePrefix = BuildItemAssetPrefix(newTitle, item.Id);
        if (!AssetNameHelper.RequiresDisplayPrefix(type))
            return basePrefix;

        var display = AssetNameHelper.ExtractDisplayPrefixOrFallback(existingPrefix ?? string.Empty);
        display = SanitizeForFilename(display);
        if (string.IsNullOrWhiteSpace(display))
            display = "Unknown";

        return AssetNameHelper.BuildDisplayPrefix(display, basePrefix);
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

        string? fullDestPath = null;
        try
        {
            fullDestPath = GetAssetImportDestinationPath(sourceFilePath, entity, nodePathStack, type);
            var newAsset = new MediaAsset
            {
                Type = type,
                RelativePath = Path.GetRelativePath(AppPaths.DataRoot, fullDestPath)
            };

            await CopyAssetAtomicallyAsync(sourceFilePath, fullDestPath, type);
            RaiseLibraryChanged();
            return newAsset;
        }
        catch (Exception ex)
        {
            ReportAssetFailure("FileManagement.AssetImportFailed", ErrorSeverity.Error,
                "ImportAsset", type, ex, sourceFilePath, fullDestPath);
            return null;
        }
    }
    
    public bool RenameItemAssets(MediaItem item, string oldTitle, string newTitle, List<string> nodePathStack)
    {
        if (item == null) return false;

        if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            return false;

        var oldPrefix = BuildItemAssetPrefix(oldTitle, item.Id);
        var newPrefix = BuildItemAssetPrefix(newTitle, item.Id);
        if (FileSystemPathIdentity.Equals(oldPrefix, newPrefix))
            return false;

        string? nodeFolder = null;
        try
        {
            nodeFolder = ResolveNodeFolder(nodePathStack);
            nodeFolder = RequireSafeAssetMutationPath(nodeFolder);
        }
        catch (Exception ex)
        {
            ReportAssetFailure("FileManagement.AssetRenameFailed", ErrorSeverity.Warning,
                "ResolveRenameFolder", null, ex, targetPath: nodeFolder);
            return false;
        }

        if (!Directory.Exists(nodeFolder))
            return false;

        bool anyRenamed = false;
        var assetsSnapshot = item.Assets.ToList();

        foreach (var asset in assetsSnapshot)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.RelativePath))
                continue;

            string? targetFullPath = null;
            var fullPath = asset.RelativePath;
            try
            {
                fullPath = RequireSafeAssetMutationPath(fullPath);

                if (!File.Exists(fullPath))
                    continue;

                var extension = Path.GetExtension(fullPath);
                if (string.IsNullOrEmpty(extension))
                    extension = ".bin";

                var fileName = Path.GetFileName(fullPath);
                var match = AssetRegex.Match(fileName);

                string targetFileName;
                var targetPrefix = BuildItemAssetPrefixForRename(item, asset.Type, newTitle, match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(fileName));
                if (match.Success)
                {
                    var number = match.Groups[3].Value;
                    targetFileName = $"{targetPrefix}_{asset.Type}_{number}{extension}";
                }
                else
                {
                    targetFileName = Path.GetFileName(GetNextAssetFileName(nodeFolder, targetPrefix, asset.Type, extension, prefixIsSanitized: true));
                }

                var targetFolder = Path.Combine(nodeFolder, asset.Type.ToString());
                targetFullPath = Path.Combine(targetFolder, targetFileName);

                targetFullPath = RequireSafeAssetMutationPath(targetFullPath);

                if (FileSystemPathIdentity.Equals(fullPath, targetFullPath))
                    continue;

                if (File.Exists(targetFullPath))
                    targetFullPath = GetNextAssetFileName(nodeFolder, targetPrefix, asset.Type, extension, prefixIsSanitized: true);

                var targetDir = Path.GetDirectoryName(targetFullPath);
                if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                TryInvalidateImageCache(fullPath);
                File.Move(fullPath, targetFullPath);

                asset.RelativePath = Path.GetRelativePath(AppPaths.DataRoot, targetFullPath);
                anyRenamed = true;
            }
            catch (Exception ex)
            {
                ReportAssetFailure("FileManagement.AssetRenameFailed", ErrorSeverity.Warning,
                    "RenameAsset", asset.Type, ex, fullPath, targetFullPath);
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
        if (item == null)
            return new List<MediaAsset>();

        return ScanItemsAssets([item], nodePathStack)[0].Assets;
    }

    /// <summary>
    /// Scans every managed asset folder once and maps matching files to all supplied items.
    /// This method does NOT modify any ObservableCollections (safe to run off the UI thread).
    /// </summary>
    public List<(MediaItem Item, List<MediaAsset> Assets)> ScanItemsAssets(
        IReadOnlyList<MediaItem> items,
        List<string> nodePathStack,
        IProgress<FolderImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = items
            .Select(item => (Item: item, Assets: new List<MediaAsset>()))
            .ToList();
        if (items.Count == 0)
            return results;

        var nodeFolder = ResolveNodeFolder(nodePathStack);
        if (!Directory.Exists(nodeFolder))
        {
            progress?.Report(new FolderImportProgress(FolderImportStage.MatchingAssets));
            return results;
        }

        var itemIndexesByPrefix = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = BuildItemAssetPrefix(items[index]);
            if (!itemIndexesByPrefix.TryGetValue(prefix, out var matchingIndexes))
            {
                matchingIndexes = new List<int>();
                itemIndexesByPrefix[prefix] = matchingIndexes;
            }

            matchingIndexes.Add(index);
        }

        long filesScanned = 0;
        var matchingFileCount = 0;
        progress?.Report(new FolderImportProgress(FolderImportStage.MatchingAssets));
        foreach (var type in Enum.GetValues<AssetType>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type == AssetType.Unknown)
                continue;

            var typeFolder = Path.Combine(nodeFolder, type.ToString());
            if (!Directory.Exists(typeFolder))
                continue;

            foreach (var file in Directory.EnumerateFiles(typeFolder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                filesScanned++;
                if (filesScanned % 256 == 0)
                {
                    progress?.Report(new FolderImportProgress(
                        FolderImportStage.MatchingAssets,
                        filesScanned,
                        matchingFileCount));
                }

                var match = AssetRegex.Match(Path.GetFileName(file));
                if (!match.Success ||
                    !match.Groups[2].Value.Equals(type.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var filePrefix = match.Groups[1].Value;
                if (AssetNameHelper.RequiresDisplayPrefix(type))
                {
                    if (!AssetNameHelper.TrySplitDisplayPrefix(filePrefix, out _, out var suffix))
                        continue;

                    filePrefix = suffix;
                }

                if (!itemIndexesByPrefix.TryGetValue(filePrefix, out var itemIndexes))
                    continue;

                matchingFileCount++;
                var relativePath = Path.GetRelativePath(AppPaths.DataRoot, file);
                foreach (var itemIndex in itemIndexes)
                {
                    results[itemIndex].Assets.Add(new MediaAsset
                    {
                        Type = type,
                        RelativePath = relativePath
                    });
                }

            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new FolderImportProgress(
            FolderImportStage.MatchingAssets,
            filesScanned,
            matchingFileCount));
        return results;
    }

    // --- Private Helpers ---
    private string GetAssetImportDestinationPath(
        string sourceFilePath, object entity, List<string> nodePathStack, AssetType type)
    {
        var assetPrefix = entity switch
        {
            MediaItem item => BuildItemAssetPrefixForAsset(item, type, Path.GetFileNameWithoutExtension(sourceFilePath)),
            MediaNode node => SanitizeForFilename(node.Name),
            _ => "Unknown"
        };
        var nodeFolder = RequireSafeAssetMutationPath(ResolveNodeFolder(nodePathStack));
        return GetNextAssetFileName(nodeFolder, assetPrefix, type, Path.GetExtension(sourceFilePath), prefixIsSanitized: true);
    }

    private async Task CopyAssetAtomicallyAsync(string sourcePath, string destinationPath, AssetType type)
    {
        destinationPath = RequireSafeAssetMutationPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = RequireSafeAssetMutationPath(Path.Combine(directory, $".retromind-asset-{Guid.NewGuid():N}.tmp"));
        var ownsTempFile = false;
        try
        {
            await using (var source = _openAssetSource(sourcePath))
            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                ownsTempFile = true;
                await source.CopyToAsync(destination).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }

            // Revalidate after the asynchronous copy; a collision must never replace an existing asset.
            RequireSafeAssetMutationPath(tempPath);
            RequireSafeAssetMutationPath(destinationPath);
            File.Move(tempPath, destinationPath, overwrite: false);
            ownsTempFile = false;
        }
        finally
        {
            if (ownsTempFile)
            {
                try
                {
                    RequireSafeAssetMutationPath(tempPath);
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    ReportAssetFailure("FileManagement.AssetCleanupFailed", ErrorSeverity.Diagnostic,
                        "DeleteImportTemporaryFile", type, ex, tempPath, destinationPath);
                }
            }
        }
    }

    private void ReportAssetFailure(string code, ErrorSeverity severity, string operation, AssetType? type,
        Exception exception, string? sourcePath = null, string? targetPath = null)
    {
        try
        {
            var context = new Dictionary<string, string>
            {
                ["Operation"] = operation,
                ["AssetType"] = type?.ToString() ?? "Multiple"
            };
            if (sourcePath != null) context["SourcePath"] = sourcePath;
            if (targetPath != null) context["TargetPath"] = targetPath;
            _errorService.Report(new ErrorReport(code, severity, nameof(FileManagementService), code switch
            {
                "FileManagement.AssetImportFailed" => "Asset file changes could not be prepared.",
                "FileManagement.AssetRollbackFailed" => "Asset rollback did not complete. Remaining files were preserved for recovery.",
                "FileManagement.AssetRenameFailed" => "The asset could not be renamed.",
                _ => "Asset cleanup did not complete. The remaining file or directory was preserved."
            }, exception, Context: context));
        }
        catch
        {
            // Diagnostic output must not change import, rollback, or cleanup outcomes.
        }
    }

    private string ResolveNodeFolder(List<string> nodePathStack)
        => PathHelper.ResolveNodeFolder(nodePathStack, libraryRootPath);

    private static string RequireSafeAssetMutationPath(string path)
    {
        if (!AppPaths.TryResolveDataPathForMutation(path, out var safePath))
            throw new IOException($"Asset path contains an unsafe symbolic link or escapes the data root: '{path}'.");

        return safePath;
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
        string typeFolder = RequireSafeAssetMutationPath(Path.Combine(nodeBaseFolder, type.ToString()));
    
        if (!Directory.Exists(typeFolder))
            Directory.CreateDirectory(typeFolder);

        string cleanTitle = prefixIsSanitized ? assetPrefix : SanitizeForFilename(assetPrefix);
        string suffix = type.ToString(); 

        // Collect existing counters for efficiency (instead of loop checks)
        var existingFiles = Directory.EnumerateFiles(typeFolder)
            .Select(Path.GetFileName)
            .Where(name => name?.StartsWith($"{cleanTitle}_{suffix}_", StringComparison.OrdinalIgnoreCase) == true)
            .Select(name => int.TryParse(Regex.Match(name ?? "", @"_(\d+)\.").Groups[1].Value, out int num) ? num : 0);

        int maxCounter = existingFiles.DefaultIfEmpty().Max();
        int nextCounter = maxCounter + 1;
        string number = nextCounter.ToString("D2");
        string fileName = $"{cleanTitle}_{suffix}_{number}{extension}";

        return RequireSafeAssetMutationPath(Path.Combine(typeFolder, fileName));
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
