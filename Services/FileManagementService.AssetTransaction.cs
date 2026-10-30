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
        private readonly List<MediaAsset> _pendingImportCleanup = new();
        private readonly List<(string OriginalPath, string QuarantinePath, AssetType Type)> _quarantinedFiles = new();
        private readonly HashSet<AssetType> _assetTypes = new();
        private readonly string _quarantineDirectory;
        private bool _completed;
        private bool _rollbackStarted;
        private bool _quarantineCreated;

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

        internal string CreateQuarantineDirectory()
        {
            var directory = RequireSafeAssetMutationPath(_quarantineDirectory);
            Directory.CreateDirectory(directory);
            _quarantineCreated = true;
            return directory;
        }

        internal void TrackCreatedAsset(MediaAsset asset)
        {
            _createdAssets.Add(asset);
            _pendingImportCleanup.Add(asset);
            _assetTypes.Add(asset.Type);
        }

        internal void TrackQuarantinedFile(string originalPath, string quarantinePath, AssetType type)
        {
            _quarantinedFiles.Add((originalPath, quarantinePath, type));
            _assetTypes.Add(type);
        }

        public void Commit()
        {
            if (_completed)
                return;
            if (_rollbackStarted)
                throw new InvalidOperationException("An asset transaction cannot be committed after rollback has started.");

            _completed = true;
            _owner.RaiseLibraryChanged();
            TryDeleteQuarantine();
        }

        public void Rollback()
        {
            if (_completed)
                return;

            _rollbackStarted = true;
            var failures = new List<Exception>();

            // Remove imports before restoring originals, so cleanup can never delete a restored original.
            for (var i = _pendingImportCleanup.Count - 1; i >= 0; i--)
            {
                var asset = _pendingImportCleanup[i];
                try
                {
                    var createdPath = RequireSafeAssetMutationPath(asset.RelativePath);
                    if (_quarantinedFiles.Any(file => FileSystemPathIdentity.Equals(file.QuarantinePath, createdPath)))
                        throw new IOException("Import cleanup cannot delete a quarantined original.");
                    TryInvalidateImageCache(createdPath);
                    // File.Delete handles a missing file, but does not hide access errors like File.Exists.
                    File.Delete(createdPath);
                    _pendingImportCleanup.RemoveAt(i);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    _owner.ReportAssetFailure("FileManagement.AssetRollbackFailed", ErrorSeverity.Error,
                        "RemoveImportedAsset", asset.Type, ex, asset.RelativePath);
                }
            }

            for (var i = _quarantinedFiles.Count - 1; i >= 0; i--)
            {
                var (originalPath, quarantinePath, type) = _quarantinedFiles[i];
                try
                {
                    RequireSafeAssetMutationPath(quarantinePath);
                    RequireSafeAssetMutationPath(originalPath);
                    // An import that could not be removed may still occupy the original path. Do not restore
                    // underneath a pending cleanup step that would delete the original on a later retry.
                    if (_pendingImportCleanup.Any(asset => AppPaths.TryResolveDataPathInsideRoot(asset.RelativePath, out var path) &&
                                                          FileSystemPathIdentity.Equals(path, originalPath)))
                        continue; // The failed import cleanup was already reported; keep restoration pending.

                    var originalDirectory = Path.GetDirectoryName(originalPath);
                    if (!string.IsNullOrWhiteSpace(originalDirectory))
                        Directory.CreateDirectory(originalDirectory);

                    File.Move(quarantinePath, originalPath, overwrite: false);
                    _quarantinedFiles.RemoveAt(i);
                    TryInvalidateImageCache(originalPath);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    _owner.ReportAssetFailure("FileManagement.AssetRollbackFailed", ErrorSeverity.Error,
                        "RestoreQuarantinedAsset", type, ex, quarantinePath, originalPath);
                }
            }

            if (failures.Count > 0)
                throw new AggregateException("Asset file rollback did not complete.", failures);

            _completed = true;
            TryDeleteQuarantine();
        }

        public void Dispose()
        {
            if (_completed || _rollbackStarted)
                return;

            try
            {
                Rollback();
            }
            catch
            {
                // Rollback reported each failed step. Retain its pending work and quarantine for an
                // explicit retry; Dispose must neither retry/log the same failure nor commit cleanup.
            }
        }

        private void TryDeleteQuarantine()
        {
            if (!_quarantineCreated)
                return;
            try
            {
                var safeQuarantineDirectory = RequireSafeAssetMutationPath(_quarantineDirectory);

                try
                {
                    _owner._deleteQuarantineDirectory(safeQuarantineDirectory);
                }
                catch (DirectoryNotFoundException)
                {
                    // The directory may already have been removed externally.
                }

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
                // Commit or rollback is complete. Cleanup failure must not change that outcome.
                _owner.ReportAssetFailure("FileManagement.AssetCleanupFailed", ErrorSeverity.Diagnostic,
                    "DeleteQuarantine", _assetTypes.Count == 1 ? _assetTypes.Single() : null, ex, _quarantineDirectory);
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
        var operation = "ImportAsset";
        AssetType? assetType = null;
        string? sourcePath = null;
        string? destinationPath = null;

        try
        {
            foreach (var import in imports)
            {
                assetType = import.Type;
                sourcePath = import.SourceFilePath;
                destinationPath = null;
                if (string.IsNullOrWhiteSpace(import.SourceFilePath) || !File.Exists(import.SourceFilePath))
                    throw new FileNotFoundException("The selected asset source file no longer exists.", import.SourceFilePath);

                destinationPath = GetAssetImportDestinationPath(import.SourceFilePath, entity, nodePathStack.ToList(), import.Type);
                var importedAsset = new MediaAsset
                {
                    Type = import.Type,
                    RelativePath = Path.GetRelativePath(AppPaths.DataRoot, destinationPath)
                };
                await CopyAssetAtomicallyAsync(import.SourceFilePath, destinationPath, import.Type);
                transaction.TrackCreatedAsset(importedAsset);
            }

            var seenDeletionPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in deletions)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.RelativePath))
                    continue;

                operation = "QuarantineDeletion";
                assetType = asset.Type;
                sourcePath = asset.RelativePath;
                destinationPath = null;
                if (!AppPaths.TryResolveDataPathForMutation(asset.RelativePath, out var originalPath))
                    throw new IOException($"Asset path cannot be deleted safely: {asset.RelativePath}");

                if (!seenDeletionPaths.Add(originalPath) || !File.Exists(originalPath))
                    continue;

                var quarantineDirectory = transaction.CreateQuarantineDirectory();
                var quarantinePath = Path.Combine(
                    quarantineDirectory,
                    $"{Guid.NewGuid():N}_{Path.GetFileName(originalPath)}");
                quarantinePath = RequireSafeAssetMutationPath(quarantinePath);
                sourcePath = originalPath;
                destinationPath = quarantinePath;

                TryInvalidateImageCache(originalPath);
                File.Move(originalPath, quarantinePath, overwrite: false);
                transaction.TrackQuarantinedFile(originalPath, quarantinePath, asset.Type);
            }

            return transaction;
        }
        catch (Exception preparationError)
        {
            ReportAssetFailure("FileManagement.AssetImportFailed", ErrorSeverity.Error,
                operation, assetType, preparationError, sourcePath, destinationPath);
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
