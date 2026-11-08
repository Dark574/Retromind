using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Retromind.Models;

namespace Retromind.Helpers;

public static partial class NodeAssetFolderHelper
{
    private static bool TryPlanAssetFolderMovesRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        IReadOnlyList<string> newBaseSegments,
        List<string> relativeSegments,
        List<PlannedFileMove> plannedMoves,
        Dictionary<string, string> plannedRenamedFiles,
        Dictionary<string, TargetFolderReservation> reservationsByFolder,
        HashSet<MediaNode> visitedNodes,
        int depth,
        List<string> oldFolders)
    {
        if (!TryBeginNodeTraversal(node, visitedNodes, depth, nameof(MoveAssetFoldersRecursive)))
            return false;

        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        var newSegments = new List<string>(newBaseSegments.Count + relativeSegments.Count);
        newSegments.AddRange(newBaseSegments);
        newSegments.AddRange(relativeSegments);
        oldFolders.Add(PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot));

        if (!TryPlanAssetFolderMovesForNode(
                oldSegments,
                newSegments,
                plannedMoves,
                plannedRenamedFiles,
                reservationsByFolder))
        {
            return false;
        }

        foreach (var child in node.Children)
        {
            relativeSegments.Add(child.Name);
            var childPlanned = TryPlanAssetFolderMovesRecursive(
                child,
                oldBaseSegments,
                newBaseSegments,
                relativeSegments,
                plannedMoves,
                plannedRenamedFiles,
                reservationsByFolder,
                visitedNodes,
                depth + 1,
                oldFolders);
            relativeSegments.RemoveAt(relativeSegments.Count - 1);

            if (!childPlanned)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryPlanAssetFolderMovesForNode(
        List<string> oldSegments,
        List<string> newSegments,
        List<PlannedFileMove> plannedMoves,
        Dictionary<string, string> plannedRenamedFiles,
        Dictionary<string, TargetFolderReservation> reservationsByFolder)
    {
        var oldFolder = PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot);
        if (!AppPaths.TryResolveDataPathForMutation(oldFolder, out oldFolder))
            return false;

        if (!Directory.Exists(oldFolder))
            return true;

        var newFolder = PathHelper.ResolveNodeFolder(newSegments, AppPaths.LibraryRoot);
        if (!AppPaths.TryResolveDataPathForMutation(newFolder, out newFolder))
            return false;

        foreach (var type in AssetFolderTypes)
        {
            var oldTypeFolder = Path.Combine(oldFolder, type.ToString());
            if (!AppPaths.TryResolveDataPathForMutation(oldTypeFolder, out oldTypeFolder))
                return false;

            if (!Directory.Exists(oldTypeFolder))
                continue;

            var newTypeFolder = Path.Combine(newFolder, type.ToString());
            if (!AppPaths.TryResolveDataPathForMutation(newTypeFolder, out newTypeFolder))
                return false;

            if (string.Equals(oldTypeFolder, newTypeFolder, StringComparison.Ordinal))
                continue;

            var reservation = GetTargetFolderReservation(newTypeFolder, reservationsByFolder);

            foreach (var file in Directory.EnumerateFiles(oldTypeFolder))
            {
                if (!AppPaths.TryResolveDataPathForMutation(file, out _))
                    return false;
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var targetFileName = fileName;
                string? renamedRelativePath = null;

                if (!ReserveFileName(reservation, targetFileName))
                {
                    targetFileName = GetRenumberedAssetFileName(fileName, reservation);
                    if (string.IsNullOrWhiteSpace(targetFileName))
                    {
                        return false;
                    }
                }

                var targetPath = Path.Combine(newTypeFolder, targetFileName);
                var sourceRelativePath = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, file));

                if (!string.Equals(targetFileName, fileName, StringComparison.Ordinal))
                {
                    renamedRelativePath = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, targetPath));
                    if (plannedRenamedFiles.TryGetValue(sourceRelativePath, out var existing) &&
                        !string.Equals(existing, renamedRelativePath, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    plannedRenamedFiles[sourceRelativePath] = renamedRelativePath;
                }

                plannedMoves.Add(new PlannedFileMove
                {
                    SourcePath = file,
                    TargetPath = targetPath
                });
            }
        }

        return true;
    }

    private static void TryExecutePlannedMoves(
        List<PlannedFileMove> plannedMoves, IReadOnlyDictionary<string, string> plannedRenamedFiles,
        Dictionary<string, string> renamedFiles, NodeAssetOperationResult result, NodeAssetFileOperations io,
        PreparedPathUpdates? paths, Action applyCategoryChange, Action rollbackCategoryChange)
    {
        var stagingRoot = Path.Combine(AppPaths.LibraryRoot, ".retromind_asset_move_staging", Guid.NewGuid().ToString("N"));
        var staged = new List<PlannedFileMove>();
        var failureCode = "NodeAssets.MoveFailed";
        var phase = "Staging";
        var categoryAttempted = false;
        try
        {
            if (!AppPaths.TryResolveDataPathForMutation(stagingRoot, out stagingRoot))
                throw new IOException("Unsafe asset staging location.");
            result.StagingDirectory = RelativeLocation(stagingRoot);
            if (plannedMoves.Count > 0) Directory.CreateDirectory(stagingRoot);
            for (var i = 0; i < plannedMoves.Count; i++)
            {
                var move = plannedMoves[i];
                if (!AppPaths.TryResolveDataPathForMutation(move.SourcePath, out _) || !File.Exists(move.SourcePath))
                    throw new IOException("An asset source is missing or unsafe.");
                var stage = Path.Combine(stagingRoot, $"{i:D6}{Path.GetExtension(move.SourcePath)}");
                if (!AppPaths.TryResolveDataPathForMutation(stage, out stage))
                    throw new IOException("Unsafe asset staging location.");
                move.StagingPath = stage;
                staged.Add(move); // Also track an interrupted cross-filesystem copy or a move that throws after IO.
                io.MoveFile(move.SourcePath, stage);
                move.StagingCompleted = true;
            }
            phase = "CommitFiles";
            foreach (var move in plannedMoves)
            {
                if (!AppPaths.TryResolveDataPathForMutation(move.TargetPath, out _))
                    throw new IOException("Unsafe asset target location.");
                Directory.CreateDirectory(Path.GetDirectoryName(move.TargetPath)!);
                // File.Move itself never overwrites, including races after this check or directory collisions.
                move.CommitAttempted = true;
                io.MoveFile(move.StagingPath!, move.TargetPath);
                move.CommitCompleted = true;
            }

            failureCode = "NodeAssets.PathUpdateFailed";
            phase = "UpdatePaths";
            io.AfterFilesMoved?.Invoke();
            paths?.Apply();
            phase = "CategoryChange";
            categoryAttempted = true;
            applyCategoryChange();
            foreach (var pair in plannedRenamedFiles) renamedFiles[pair.Key] = pair.Value;
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Fail(failureCode, ex);
            result.FailurePhase = phase;
            if (categoryAttempted)
            {
                try { rollbackCategoryChange(); }
                catch { result.MetadataRecoveryRequired = true; result.RollbackComplete = false; }
            }
            paths?.Rollback(result);
            RollbackPlannedMoves(staged, result, io);
            if (!result.RollbackComplete)
                WriteRecoveryManifest(stagingRoot, result, paths);
        }
        finally
        {
            CleanupEmptyDirectory(stagingRoot, result, io);
            CleanupEmptyDirectory(Path.GetDirectoryName(stagingRoot)!, result, io);
        }
    }

    private static void RollbackPlannedMoves(IReadOnlyList<PlannedFileMove> staged,
        NodeAssetOperationResult result, NodeAssetFileOperations io)
    {
        foreach (var move in staged.Reverse())
        {
            if (move.CommitCompleted)
            {
                Restore(move, move.TargetPath);
            }
            else if (move.StagingCompleted)
            {
                // A throwing commit may already have published the file. Its target could also be a
                // competing file: preserve it rather than guessing ownership from path existence.
                var uncertainTarget = move.CommitAttempted && File.Exists(move.TargetPath);
                if (uncertainTarget)
                {
                    result.RollbackComplete = false;
                    Remember(move, move.TargetPath);
                }
                if (!uncertainTarget || File.Exists(move.StagingPath))
                    Restore(move, move.StagingPath!);
            }
            else
            {
                // Even staging can throw after IO (or after a partial cross-filesystem copy).
                // Neither an existing source nor a staging file proves a complete original survived.
                result.RollbackComplete = false;
                Remember(move, move.StagingPath!);
                Remember(move, move.SourcePath);
            }
        }

        void Restore(PlannedFileMove move, string retained)
        {
            try
            {
                if (!File.Exists(retained)) throw new IOException("The recorded asset location is missing.");
                if (!AppPaths.TryResolveDataPathForMutation(retained, out _) ||
                    !AppPaths.TryResolveDataPathForMutation(move.SourcePath, out _))
                    throw new IOException("Unsafe rollback location.");
                // No overwrite: a conflicting source (including a newly created one) must survive unchanged.
                Directory.CreateDirectory(Path.GetDirectoryName(move.SourcePath)!);
                io.MoveFile(retained, move.SourcePath);
            }
            catch
            {
                result.RollbackComplete = false;
                Remember(move, retained);
                // A restoration can likewise throw after moving. Keep the actual surviving location
                // in the manifest, but never treat source existence alone as confirmed restoration.
                if (!File.Exists(retained)) Remember(move, move.SourcePath);
            }
        }

        void Remember(PlannedFileMove move, string retained)
        {
            if (!File.Exists(retained)) return;
            var entry = new NodeAssetRecoveryFile(RelativeLocation(move.SourcePath), RelativeLocation(retained));
            if (!result.RecoveryFiles.Contains(entry)) result.RecoveryFiles.Add(entry);
        }
    }

    private static void WriteRecoveryManifest(string stagingRoot, NodeAssetOperationResult result, PreparedPathUpdates? paths)
    {
        var manifest = Path.Combine(stagingRoot, "recovery.json");
        try
        {
            if (!AppPaths.TryResolveDataPathForMutation(manifest, out manifest))
                throw new IOException("Unsafe recovery manifest location.");
            Directory.CreateDirectory(stagingRoot);
            using var output = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, new
            {
                result.SourcePrefix, result.TargetPrefix, result.MetadataRecoveryRequired, result.RecoveryFiles,
                References = paths?.RecoveryReferences
            });
            output.Flush(flushToDisk: true);
            result.RecoveryManifest = RelativeLocation(manifest);
        }
        catch { result.CleanupFailures.Add(RelativeLocation(manifest)); }
    }
}
