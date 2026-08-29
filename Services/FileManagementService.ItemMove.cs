using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

internal sealed record ItemAssetMoveResult(
    bool Success,
    int MovedFileCount,
    int CopiedFileCount,
    string? ErrorMessage = null);

public partial class FileManagementService
{
    private sealed class ItemAssetTransfer
    {
        public required string SourcePath { get; init; }
        public required string TargetPath { get; init; }
        public required bool CopySource { get; init; }
        public required List<MediaAsset> Assets { get; init; }
        public string? StagingPath { get; set; }
    }

    internal ItemAssetMoveResult MoveItemAssets(
        MediaItem item,
        IReadOnlyList<string> sourceNodePath,
        IReadOnlyList<string> targetNodePath,
        IEnumerable<MediaItem> allItems,
        IEnumerable<MediaAsset>? additionalAssetReferences = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourceNodePath);
        ArgumentNullException.ThrowIfNull(targetNodePath);
        ArgumentNullException.ThrowIfNull(allItems);

        var sourceNodeFolder = ResolveNodeFolder(sourceNodePath.ToList());
        var targetNodeFolder = ResolveNodeFolder(targetNodePath.ToList());
        if (string.Equals(sourceNodeFolder, targetNodeFolder, StringComparison.Ordinal))
            return new ItemAssetMoveResult(true, 0, 0);

        var referenceCounts = CountAssetPathReferences(allItems, additionalAssetReferences);
        var groups = BuildItemAssetGroups(item);
        var transfers = new List<ItemAssetTransfer>();
        var reservedTargets = new HashSet<string>(StringComparer.Ordinal);
        var originalAssetPaths = new Dictionary<MediaAsset, string>();
        foreach (var asset in item.Assets)
            originalAssetPaths.TryAdd(asset, asset.RelativePath);

        foreach (var group in groups)
        {
            if (!File.Exists(group.SourcePath))
                continue;

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(group.SourcePath);
            }
            catch
            {
                continue;
            }

            // Moving or copying a symbolic link could silently operate on a target outside
            // Retromind's portable data root. Keep the existing reference unchanged instead.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var targetFolder = Path.Combine(targetNodeFolder, group.Type.ToString());
            var targetPath = ReserveTargetPath(
                targetFolder,
                Path.GetFileName(group.SourcePath),
                reservedTargets);

            if (string.Equals(group.SourcePath, targetPath, StringComparison.Ordinal))
                continue;

            referenceCounts.TryGetValue(group.SourcePath, out var referenceCount);
            var sourceBelongsToCurrentNode = IsPathInsideDirectory(group.SourcePath, sourceNodeFolder);
            var copySource = !sourceBelongsToCurrentNode || referenceCount > group.Assets.Count;

            transfers.Add(new ItemAssetTransfer
            {
                SourcePath = group.SourcePath,
                TargetPath = targetPath,
                CopySource = copySource,
                Assets = group.Assets
            });
        }

        if (transfers.Count == 0)
            return new ItemAssetMoveResult(true, 0, 0);

        var stagingRoot = Path.Combine(
            libraryRootPath,
            ".retromind_item_move_staging",
            Guid.NewGuid().ToString("N"));
        var staged = new List<ItemAssetTransfer>(transfers.Count);
        var committed = new List<ItemAssetTransfer>(transfers.Count);

        try
        {
            Directory.CreateDirectory(stagingRoot);

            for (var index = 0; index < transfers.Count; index++)
            {
                var transfer = transfers[index];
                var extension = Path.GetExtension(transfer.SourcePath);
                var stagingPath = Path.Combine(stagingRoot, $"{index:D6}{extension}");

                if (transfer.CopySource)
                    File.Copy(transfer.SourcePath, stagingPath, overwrite: false);
                else
                    File.Move(transfer.SourcePath, stagingPath);

                transfer.StagingPath = stagingPath;
                staged.Add(transfer);
            }

            foreach (var transfer in transfers)
            {
                if (string.IsNullOrWhiteSpace(transfer.StagingPath))
                    throw new InvalidOperationException("Staging path missing for an item asset move.");

                var targetDirectory = Path.GetDirectoryName(transfer.TargetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                if (File.Exists(transfer.TargetPath))
                    throw new IOException($"Target asset already exists: '{transfer.TargetPath}'.");

                File.Move(transfer.StagingPath, transfer.TargetPath);
                committed.Add(transfer);
            }

            foreach (var transfer in transfers)
            {
                var targetRelativePath = Path.GetRelativePath(AppPaths.DataRoot, transfer.TargetPath);
                foreach (var asset in transfer.Assets)
                    asset.RelativePath = targetRelativePath;

                if (!transfer.CopySource)
                    TryInvalidateImageCache(transfer.SourcePath);
            }

            item.ResetActiveAssets();
            item.NotifyAssetPathsChanged();
            CleanupOldItemAssetFolders(sourceNodeFolder);
            RaiseLibraryChanged();

            return new ItemAssetMoveResult(
                true,
                transfers.Count(transfer => !transfer.CopySource),
                transfers.Count(transfer => transfer.CopySource));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ItemMove] Asset move failed, rolling back: {ex}");
            RollbackItemAssetTransfers(staged, committed);
            RestoreOriginalAssetPaths(item, originalAssetPaths);
            return new ItemAssetMoveResult(false, 0, 0, ex.Message);
        }
        finally
        {
            NodeAssetFolderHelper.DeleteDirectoryIfEmpty(stagingRoot);
            var stagingParent = Path.GetDirectoryName(stagingRoot);
            if (!string.IsNullOrWhiteSpace(stagingParent))
                NodeAssetFolderHelper.DeleteDirectoryIfEmpty(stagingParent);
        }
    }

    private static Dictionary<string, int> CountAssetPathReferences(
        IEnumerable<MediaItem> items,
        IEnumerable<MediaAsset>? additionalAssetReferences)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in items)
        {
            foreach (var asset in candidate.Assets)
                CountAssetReference(asset, counts);
        }

        if (additionalAssetReferences != null)
        {
            foreach (var asset in additionalAssetReferences)
                CountAssetReference(asset, counts);
        }

        return counts;
    }

    private static void CountAssetReference(MediaAsset asset, IDictionary<string, int> counts)
    {
        if (!TryResolveAssetPath(asset, out var fullPath))
            return;

        counts.TryGetValue(fullPath, out var count);
        counts[fullPath] = count + 1;
    }

    private static List<(string SourcePath, AssetType Type, List<MediaAsset> Assets)> BuildItemAssetGroups(
        MediaItem item)
    {
        var groups = new Dictionary<(string Path, AssetType Type), List<MediaAsset>>();

        foreach (var asset in item.Assets)
        {
            if (asset.Type == AssetType.Unknown || !TryResolveAssetPath(asset, out var fullPath))
                continue;

            var key = (fullPath, asset.Type);
            if (!groups.TryGetValue(key, out var groupedAssets))
            {
                groupedAssets = new List<MediaAsset>();
                groups[key] = groupedAssets;
            }

            groupedAssets.Add(asset);
        }

        return groups
            .Select(pair => (pair.Key.Path, pair.Key.Type, pair.Value))
            .ToList();
    }

    private static bool TryResolveAssetPath(MediaAsset asset, out string fullPath)
    {
        fullPath = string.Empty;
        if (asset == null || string.IsNullOrWhiteSpace(asset.RelativePath))
            return false;

        if (!AppPaths.TryResolveDataPathInsideRoot(asset.RelativePath, out var resolved))
            return false;

        fullPath = Path.GetFullPath(resolved);
        return true;
    }

    private static string ReserveTargetPath(
        string targetFolder,
        string fileName,
        HashSet<string> reservedTargets)
    {
        var candidate = Path.Combine(targetFolder, fileName);
        if (!File.Exists(candidate) && reservedTargets.Add(candidate))
            return candidate;

        var match = AssetRegex.Match(fileName);
        var extension = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);

        if (match.Success)
        {
            var prefix = match.Groups[1].Value;
            var type = match.Groups[2].Value;
            for (var number = 1; ; number++)
            {
                candidate = Path.Combine(targetFolder, $"{prefix}_{type}_{number:D2}{extension}");
                if (!File.Exists(candidate) && reservedTargets.Add(candidate))
                    return candidate;
            }
        }

        for (var number = 1; ; number++)
        {
            candidate = Path.Combine(targetFolder, $"{baseName}_Moved_{number:D2}{extension}");
            if (!File.Exists(candidate) && reservedTargets.Add(candidate))
                return candidate;
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(directory),
            Path.GetFullPath(path));

        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RollbackItemAssetTransfers(
        IReadOnlyList<ItemAssetTransfer> staged,
        IReadOnlyList<ItemAssetTransfer> committed)
    {
        for (var index = committed.Count - 1; index >= 0; index--)
        {
            var transfer = committed[index];
            try
            {
                if (!File.Exists(transfer.TargetPath))
                    continue;

                if (transfer.CopySource)
                {
                    File.Delete(transfer.TargetPath);
                    continue;
                }

                var sourceDirectory = Path.GetDirectoryName(transfer.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                if (!File.Exists(transfer.SourcePath))
                    File.Move(transfer.TargetPath, transfer.SourcePath);
            }
            catch (Exception rollbackException)
            {
                Debug.WriteLine($"[ItemMove] Failed to roll back committed asset: {rollbackException}");
            }
        }

        for (var index = staged.Count - 1; index >= 0; index--)
        {
            var transfer = staged[index];
            try
            {
                if (string.IsNullOrWhiteSpace(transfer.StagingPath) || !File.Exists(transfer.StagingPath))
                    continue;

                if (transfer.CopySource)
                {
                    File.Delete(transfer.StagingPath);
                    continue;
                }

                var sourceDirectory = Path.GetDirectoryName(transfer.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                if (!File.Exists(transfer.SourcePath))
                    File.Move(transfer.StagingPath, transfer.SourcePath);
            }
            catch (Exception rollbackException)
            {
                Debug.WriteLine($"[ItemMove] Failed to roll back staged asset: {rollbackException}");
            }
        }
    }

    private static void CleanupOldItemAssetFolders(string sourceNodeFolder)
    {
        foreach (var assetType in Enum.GetValues<AssetType>())
        {
            if (assetType != AssetType.Unknown)
                NodeAssetFolderHelper.DeleteDirectoryIfEmpty(Path.Combine(sourceNodeFolder, assetType.ToString()));
        }

        NodeAssetFolderHelper.DeleteDirectoryIfEmpty(sourceNodeFolder);
    }

    private static void RestoreOriginalAssetPaths(
        MediaItem item,
        IReadOnlyDictionary<MediaAsset, string> originalAssetPaths)
    {
        try
        {
            foreach (var pair in originalAssetPaths)
                pair.Key.RelativePath = pair.Value;

            item.ResetActiveAssets();
            item.NotifyAssetPathsChanged();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ItemMove] Failed to restore an asset path after rollback: {ex}");
        }
    }

    private static void TryInvalidateImageCache(string path)
    {
        try
        {
            AsyncImageHelper.InvalidateCache(path);
        }
        catch (Exception ex)
        {
            // Cache invalidation is only an optimization and must never invalidate
            // an otherwise successful, already committed filesystem transaction.
            Debug.WriteLine($"[ItemMove] Could not invalidate image cache for '{path}': {ex.Message}");
        }
    }
}
