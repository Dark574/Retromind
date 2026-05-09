using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        int depth)
    {
        if (!TryBeginNodeTraversal(node, visitedNodes, depth, nameof(MoveAssetFoldersRecursive)))
            return false;

        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        var newSegments = new List<string>(newBaseSegments.Count + relativeSegments.Count);
        newSegments.AddRange(newBaseSegments);
        newSegments.AddRange(relativeSegments);

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
                depth + 1);
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
        if (!Directory.Exists(oldFolder))
            return true;

        var newFolder = PathHelper.ResolveNodeFolder(newSegments, AppPaths.LibraryRoot);

        foreach (var type in AssetFolderTypes)
        {
            var oldTypeFolder = Path.Combine(oldFolder, type.ToString());
            if (!Directory.Exists(oldTypeFolder))
                continue;

            var newTypeFolder = Path.Combine(newFolder, type.ToString());
            if (string.Equals(oldTypeFolder, newTypeFolder, StringComparison.OrdinalIgnoreCase))
                continue;

            var reservation = GetTargetFolderReservation(newTypeFolder, reservationsByFolder);

            foreach (var file in Directory.EnumerateFiles(oldTypeFolder))
            {
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
                        Debug.WriteLine($"[NodeAssetFolderHelper] Failed to allocate renamed target for '{file}'.");
                        return false;
                    }
                }

                var targetPath = Path.Combine(newTypeFolder, targetFileName);
                var sourceRelativePath = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, file));

                if (!string.Equals(targetFileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    renamedRelativePath = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, targetPath));
                    if (plannedRenamedFiles.TryGetValue(sourceRelativePath, out var existing) &&
                        !string.Equals(existing, renamedRelativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine(
                            $"[NodeAssetFolderHelper] Planned rename collision for '{sourceRelativePath}': '{existing}' -> '{renamedRelativePath}'. Overwriting mapping.");
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

    private static bool TryExecutePlannedMoves(
        List<PlannedFileMove> plannedMoves,
        IReadOnlyDictionary<string, string> plannedRenamedFiles,
        Dictionary<string, string> renamedFiles)
    {
        var stagingRoot = Path.Combine(
            AppPaths.LibraryRoot,
            ".retromind_asset_move_staging",
            Guid.NewGuid().ToString("N"));

        var stagedMoves = new List<PlannedFileMove>(plannedMoves.Count);
        var committedMoves = new List<PlannedFileMove>(plannedMoves.Count);

        try
        {
            Directory.CreateDirectory(stagingRoot);

            for (var i = 0; i < plannedMoves.Count; i++)
            {
                var move = plannedMoves[i];
                if (!File.Exists(move.SourcePath))
                    throw new FileNotFoundException("Source file does not exist for planned asset move.", move.SourcePath);

                var extension = Path.GetExtension(move.SourcePath);
                var stagePath = Path.Combine(stagingRoot, $"{i:D6}{extension}");

                File.Move(move.SourcePath, stagePath);
                move.StagingPath = stagePath;
                stagedMoves.Add(move);
            }

            foreach (var move in plannedMoves)
            {
                if (string.IsNullOrWhiteSpace(move.StagingPath))
                    throw new InvalidOperationException($"Staging path missing for planned move '{move.SourcePath}'.");

                var targetDirectory = Path.GetDirectoryName(move.TargetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory) && !Directory.Exists(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);

                if (File.Exists(move.TargetPath))
                    throw new IOException($"Target collision during commit: '{move.TargetPath}'.");

                File.Move(move.StagingPath, move.TargetPath);
                committedMoves.Add(move);
            }

            foreach (var kvp in plannedRenamedFiles)
            {
                if (renamedFiles.TryGetValue(kvp.Key, out var existing) &&
                    !string.Equals(existing, kvp.Value, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine(
                        $"[NodeAssetFolderHelper] Renamed path collision for '{kvp.Key}': '{existing}' -> '{kvp.Value}'. Overwriting mapping.");
                }

                renamedFiles[kvp.Key] = kvp.Value;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NodeAssetFolderHelper] Atomic move failed, rolling back: {ex}");
            RollbackPlannedMoves(stagedMoves, committedMoves);
            return false;
        }
        finally
        {
            DeleteDirectoryIfEmpty(stagingRoot);

            var stagingParent = Path.GetDirectoryName(stagingRoot);
            if (!string.IsNullOrWhiteSpace(stagingParent))
                DeleteDirectoryIfEmpty(stagingParent);
        }
    }

    private static void RollbackPlannedMoves(
        IReadOnlyList<PlannedFileMove> stagedMoves,
        IReadOnlyList<PlannedFileMove> committedMoves)
    {
        for (var i = committedMoves.Count - 1; i >= 0; i--)
        {
            var move = committedMoves[i];
            try
            {
                if (!File.Exists(move.TargetPath))
                    continue;

                var sourceDirectory = Path.GetDirectoryName(move.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && !Directory.Exists(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                if (File.Exists(move.SourcePath))
                {
                    Debug.WriteLine(
                        $"[NodeAssetFolderHelper] Rollback source already exists for '{move.SourcePath}'. Keeping committed target in place.");
                    continue;
                }

                File.Move(move.TargetPath, move.SourcePath);
            }
            catch (Exception rollbackEx)
            {
                Debug.WriteLine($"[NodeAssetFolderHelper] Rollback failed (commit phase): {rollbackEx}");
            }
        }

        for (var i = stagedMoves.Count - 1; i >= 0; i--)
        {
            var move = stagedMoves[i];
            try
            {
                if (string.IsNullOrWhiteSpace(move.StagingPath) || !File.Exists(move.StagingPath))
                    continue;

                var sourceDirectory = Path.GetDirectoryName(move.SourcePath);
                if (!string.IsNullOrWhiteSpace(sourceDirectory) && !Directory.Exists(sourceDirectory))
                    Directory.CreateDirectory(sourceDirectory);

                if (File.Exists(move.SourcePath))
                {
                    Debug.WriteLine(
                        $"[NodeAssetFolderHelper] Rollback source already exists for staged move '{move.SourcePath}'.");
                    continue;
                }

                File.Move(move.StagingPath, move.SourcePath);
            }
            catch (Exception rollbackEx)
            {
                Debug.WriteLine($"[NodeAssetFolderHelper] Rollback failed (staging phase): {rollbackEx}");
            }
        }
    }

    private static void CleanupEmptyOldFoldersRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        List<string> relativeSegments)
    {
        var visitedNodes = new HashSet<MediaNode>();
        if (!TryCleanupEmptyOldFoldersRecursive(node, oldBaseSegments, relativeSegments, visitedNodes, depth: 0))
        {
            Debug.WriteLine("[NodeAssetFolderHelper] Empty-folder cleanup aborted due to invalid node traversal.");
        }
    }

    private static bool TryCleanupEmptyOldFoldersRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        List<string> relativeSegments,
        HashSet<MediaNode> visitedNodes,
        int depth)
    {
        if (!TryBeginNodeTraversal(node, visitedNodes, depth, nameof(CleanupEmptyOldFoldersRecursive)))
            return false;

        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        foreach (var child in node.Children)
        {
            relativeSegments.Add(child.Name);
            var childCleanup = TryCleanupEmptyOldFoldersRecursive(
                child,
                oldBaseSegments,
                relativeSegments,
                visitedNodes,
                depth + 1);
            relativeSegments.RemoveAt(relativeSegments.Count - 1);

            if (!childCleanup)
                return false;
        }

        var oldFolder = PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot);
        foreach (var type in AssetFolderTypes)
            DeleteDirectoryIfEmpty(Path.Combine(oldFolder, type.ToString()));

        DeleteDirectoryIfEmpty(oldFolder);
        return true;
    }
}
