using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

public partial class FileManagementService
{
    public sealed record AssetImportRequest(string SourceFilePath, AssetType Type);

    /// <summary>
    /// Prepared file changes for an editor save. Until <see cref="Commit"/> is called,
    /// disposing the transaction removes copied imports and restores quarantined deletions.
    /// </summary>
    public sealed class PreparedAssetTransaction : IDisposable
    {
        private readonly FileManagementService _owner;
        private readonly List<MediaAsset> _createdAssets = new();
        private readonly List<(string OriginalPath, string QuarantinePath)> _quarantinedFiles = new();
        private readonly string _quarantineDirectory;
        private bool _completed;

        internal PreparedAssetTransaction(FileManagementService owner)
        {
            _owner = owner;
            _quarantineDirectory = Path.Combine(
                AppPaths.DataRoot,
                ".asset-transactions",
                Guid.NewGuid().ToString("N"));
        }

        public IReadOnlyList<MediaAsset> ImportedAssets => _createdAssets;

        internal string QuarantineDirectory => _quarantineDirectory;

        internal void TrackCreatedAsset(MediaAsset asset)
            => _createdAssets.Add(asset);

        internal void TrackQuarantinedFile(string originalPath, string quarantinePath)
            => _quarantinedFiles.Add((originalPath, quarantinePath));

        public void Commit()
        {
            if (_completed)
                return;

            _completed = true;
            _owner.RaiseLibraryChanged();
            TryDeleteQuarantine();
        }

        public void Rollback()
        {
            if (_completed)
                return;

            var failures = new List<Exception>();

            for (var i = _quarantinedFiles.Count - 1; i >= 0; i--)
            {
                var (originalPath, quarantinePath) = _quarantinedFiles[i];
                try
                {
                    if (!File.Exists(quarantinePath))
                        continue;

                    RequireSafeAssetMutationPath(quarantinePath);
                    RequireSafeAssetMutationPath(originalPath);

                    var originalDirectory = Path.GetDirectoryName(originalPath);
                    if (!string.IsNullOrWhiteSpace(originalDirectory))
                        Directory.CreateDirectory(originalDirectory);

                    File.Move(quarantinePath, originalPath, overwrite: false);
                    TryInvalidateImageCache(originalPath);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            foreach (var asset in _createdAssets)
            {
                try
                {
                    if (!AppPaths.TryResolveDataPathForMutation(asset.RelativePath, out var createdPath))
                        continue;

                    TryInvalidateImageCache(createdPath);
                    if (File.Exists(createdPath))
                        File.Delete(createdPath);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            _completed = true;
            if (failures.Count > 0)
                throw new AggregateException("Asset file rollback did not complete.", failures);

            TryDeleteQuarantine();
        }

        public void Dispose()
        {
            if (_completed)
                return;

            try
            {
                Rollback();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error rolling back asset transaction: {ex}");
            }
        }

        private void TryDeleteQuarantine()
        {
            try
            {
                if (!AppPaths.TryResolveDataPathForMutation(_quarantineDirectory, out var safeQuarantineDirectory))
                    return;

                if (Directory.Exists(safeQuarantineDirectory))
                    Directory.Delete(safeQuarantineDirectory, recursive: true);

                var parent = Path.GetDirectoryName(safeQuarantineDirectory);
                if (!string.IsNullOrWhiteSpace(parent) &&
                    Directory.Exists(parent) &&
                    !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
            catch (Exception ex)
            {
                // The model is already committed. Leaving quarantine behind is safer than
                // turning successful edits into broken references.
                Console.WriteLine($"Error cleaning asset transaction quarantine: {ex.Message}");
            }
        }
    }

    public async Task<PreparedAssetTransaction> PrepareAssetTransactionAsync(
        object entity,
        IReadOnlyList<string> nodePathStack,
        IReadOnlyList<AssetImportRequest> imports,
        IReadOnlyList<MediaAsset> deletions)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(nodePathStack);
        ArgumentNullException.ThrowIfNull(imports);
        ArgumentNullException.ThrowIfNull(deletions);

        var transaction = new PreparedAssetTransaction(this);

        try
        {
            foreach (var import in imports)
            {
                if (string.IsNullOrWhiteSpace(import.SourceFilePath) || !File.Exists(import.SourceFilePath))
                    throw new FileNotFoundException("The selected asset source file no longer exists.", import.SourceFilePath);

                var assetPrefix = entity switch
                {
                    MediaItem item => BuildItemAssetPrefixForAsset(
                        item,
                        import.Type,
                        Path.GetFileNameWithoutExtension(import.SourceFilePath)),
                    MediaNode node => SanitizeForFilename(node.Name),
                    _ => "Unknown"
                };

                var nodeFolder = RequireSafeAssetMutationPath(ResolveNodeFolder(nodePathStack.ToList()));
                var extension = Path.GetExtension(import.SourceFilePath);
                var destinationPath = GetNextAssetFileName(
                    nodeFolder,
                    assetPrefix,
                    import.Type,
                    extension,
                    prefixIsSanitized: true);

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                await Task.Run(() => File.Copy(import.SourceFilePath, destinationPath, overwrite: false));

                var importedAsset = new MediaAsset
                {
                    Type = import.Type,
                    RelativePath = Path.GetRelativePath(AppPaths.DataRoot, destinationPath)
                };
                transaction.TrackCreatedAsset(importedAsset);
            }

            var seenDeletionPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in deletions)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.RelativePath))
                    continue;

                if (!AppPaths.TryResolveDataPathForMutation(asset.RelativePath, out var originalPath))
                    throw new IOException($"Asset path cannot be deleted safely: {asset.RelativePath}");

                if (!seenDeletionPaths.Add(originalPath) || !File.Exists(originalPath))
                    continue;

                var quarantineDirectory = RequireSafeAssetMutationPath(transaction.QuarantineDirectory);
                Directory.CreateDirectory(quarantineDirectory);
                var quarantinePath = Path.Combine(
                    quarantineDirectory,
                    $"{Guid.NewGuid():N}_{Path.GetFileName(originalPath)}");
                quarantinePath = RequireSafeAssetMutationPath(quarantinePath);

                TryInvalidateImageCache(originalPath);
                File.Move(originalPath, quarantinePath, overwrite: false);
                transaction.TrackQuarantinedFile(originalPath, quarantinePath);
            }

            return transaction;
        }
        catch (Exception preparationError)
        {
            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Asset changes failed and could not be rolled back completely.",
                    preparationError,
                    rollbackError);
            }

            throw new IOException("Asset changes could not be prepared.", preparationError);
        }
    }

}
