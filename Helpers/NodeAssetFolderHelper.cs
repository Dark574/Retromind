using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

public static partial class NodeAssetFolderHelper
{
    private const int MaxNodeTraversalDepth = 256;

    private sealed class PlannedFileMove
    {
        public required string SourcePath { get; init; }
        public required string TargetPath { get; init; }
        public string? StagingPath { get; set; }
        public bool StagingCompleted { get; set; }
        public bool CommitAttempted { get; set; }
        public bool CommitCompleted { get; set; }
    }

    private sealed class TargetFolderReservation
    {
        public HashSet<string> ReservedNames { get; } = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, int> NextNumberByPrefix { get; } =
            new Dictionary<string, int>(StringComparer.Ordinal);
    }

    private static readonly AssetType[] AssetFolderTypes = Enum.GetValues(typeof(AssetType))
        .Cast<AssetType>()
        .Where(type => type != AssetType.Unknown)
        .ToArray();

    private static readonly HashSet<string> AssetTypeTokens = new HashSet<string>(
        AssetFolderTypes.Select(type => type.ToString()),
        StringComparer.OrdinalIgnoreCase);

    public static NodeAssetOperationResult UpdateAssetPathsRecursive(
        MediaNode node, string oldPrefix, string newPrefix, IReadOnlyDictionary<string, string>? renamedFiles)
    {
        var result = new NodeAssetOperationResult { SourcePrefix = oldPrefix, TargetPrefix = newPrefix };
        if (!TrySnapshotNodes(new[] { node }, result, out var nodes)) return result;
        PreparedPathUpdates? paths = null;
        try
        {
            paths = PreparePathUpdates(nodes, oldPrefix, newPrefix, renamedFiles);
            paths.Apply();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Fail("NodeAssets.PathUpdateFailed", ex);
            paths?.Rollback(result);
        }
        return result;
    }

    public static NodeAssetOperationResult MoveAndUpdateAssetPaths(
        MediaNode node, List<string> oldSegments, List<string> newSegments,
        Action applyCategoryChange, Action rollbackCategoryChange) =>
        MoveAndUpdateAssetPaths(node, oldSegments, newSegments, applyCategoryChange, rollbackCategoryChange,
            new NodeAssetFileOperations());

    internal static NodeAssetOperationResult MoveAndUpdateAssetPaths(
        MediaNode node, List<string> oldSegments, List<string> newSegments,
        Action applyCategoryChange, Action rollbackCategoryChange, NodeAssetFileOperations io) =>
        RunAssetMove(node, oldSegments, newSegments, new Dictionary<string, string>(StringComparer.Ordinal),
            applyCategoryChange, rollbackCategoryChange, io, updatePaths: true);

    public static bool HasAnyAssetFolders(string nodeFolder)
    {
        if (string.IsNullOrWhiteSpace(nodeFolder))
            return false;

        if (!Directory.Exists(nodeFolder))
            return false;

        foreach (var type in AssetFolderTypes)
        {
            var folder = Path.Combine(nodeFolder, type.ToString());
            if (Directory.Exists(folder))
                return true;
        }

        return false;
    }

    public static NodeAssetOperationResult MoveAssetFoldersRecursive(
        MediaNode node, List<string> oldBaseSegments, List<string> newBaseSegments,
        Dictionary<string, string> renamedFiles) =>
        RunAssetMove(node, oldBaseSegments, newBaseSegments, renamedFiles, () => { }, () => { },
            new NodeAssetFileOperations(), updatePaths: false);

    private static NodeAssetOperationResult RunAssetMove(
        MediaNode node, List<string> oldSegments, List<string> newSegments,
        Dictionary<string, string> renamedFiles, Action applyCategoryChange, Action rollbackCategoryChange,
        NodeAssetFileOperations io, bool updatePaths)
    {
        var result = new NodeAssetOperationResult();
        if (!TrySnapshotNodes(new[] { node }, result, out var nodes)) return result;
        var plannedMoves = new List<PlannedFileMove>();
        var plannedRenames = new Dictionary<string, string>(StringComparer.Ordinal);
        var oldFolders = new List<string>();
        PreparedPathUpdates? paths = null;
        var failureCode = "NodeAssets.MoveFailed";
        try
        {
            result.SourcePrefix = RelativeLocation(PathHelper.ResolveNodeFolder(oldSegments, AppPaths.LibraryRoot));
            result.TargetPrefix = RelativeLocation(PathHelper.ResolveNodeFolder(newSegments, AppPaths.LibraryRoot));
            if (!TryPlanAssetFolderMovesRecursive(node, oldSegments, newSegments, new List<string>(), plannedMoves,
                    plannedRenames, new Dictionary<string, TargetFolderReservation>(StringComparer.Ordinal),
                    new HashSet<MediaNode>(ReferenceEqualityComparer.Instance), 0, oldFolders))
            {
                result.Fail("NodeAssets.MoveFailed");
                return result;
            }
            if (updatePaths)
            {
                failureCode = "NodeAssets.PathUpdateFailed";
                paths = PreparePathUpdates(nodes, result.SourcePrefix, result.TargetPrefix, plannedRenames);
            }
        }
        catch (Exception ex)
        {
            result.Fail(failureCode, ex);
            return result;
        }

        TryExecutePlannedMoves(plannedMoves, plannedRenames, renamedFiles, result, io, paths,
            applyCategoryChange, rollbackCategoryChange);
        if (result.Success)
            foreach (var oldFolder in oldFolders.AsEnumerable().Reverse())
            {
                foreach (var type in AssetFolderTypes)
                    CleanupEmptyDirectory(Path.Combine(oldFolder, type.ToString()), result, io);
                CleanupEmptyDirectory(oldFolder, result, io);
            }
        return result;
    }

    internal static string RelativeLocation(string path) =>
        AppPaths.TryResolveDataPathInsideRoot(path, out var full)
            ? NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, full)) : "<outside-data-root>";

    private static void CleanupEmptyDirectory(string path, NodeAssetOperationResult result, NodeAssetFileOperations io)
    {
        try
        {
            if (!AppPaths.TryResolveDataPathForMutation(path, out var safe))
                throw new IOException("Unsafe cleanup location.");
            if (Directory.Exists(safe) && !Directory.EnumerateFileSystemEntries(safe).Any())
                io.DeleteDirectory(safe);
        }
        catch { result.CleanupFailures.Add(RelativeLocation(path)); }
    }

    public static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (!AppPaths.TryResolveDataPathForMutation(path, out var safePath) ||
                !Directory.Exists(safePath))
                return;

            if (Directory.EnumerateFileSystemEntries(safePath).Any())
                return;

            Directory.Delete(safePath);
        }
        catch
        {
            // best effort cleanup
        }
    }

    public static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/').Trim();

        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        if (normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = normalized.TrimStart('/');

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".")
                continue;

            if (part == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }
}
