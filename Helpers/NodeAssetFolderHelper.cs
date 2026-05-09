using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    }

    private sealed class TargetFolderReservation
    {
        public HashSet<string> ReservedNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> NextNumberByPrefix { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly AssetType[] AssetFolderTypes = Enum.GetValues(typeof(AssetType))
        .Cast<AssetType>()
        .Where(type => type != AssetType.Unknown)
        .ToArray();

    private static readonly HashSet<string> AssetTypeTokens = new HashSet<string>(
        AssetFolderTypes.Select(type => type.ToString()),
        StringComparer.OrdinalIgnoreCase);

    public static void UpdateAssetPathsRecursive(
        MediaNode node,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        var visitedNodes = new HashSet<MediaNode>();
        if (!TryUpdateAssetPathsRecursive(node, oldPrefix, newPrefix, renamedFiles, visitedNodes, depth: 0))
        {
            Debug.WriteLine("[NodeAssetFolderHelper] Asset path update aborted due to invalid node traversal.");
        }
    }

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

    public static bool MoveAssetFoldersRecursive(
        MediaNode node,
        List<string> oldBaseSegments,
        List<string> newBaseSegments,
        Dictionary<string, string> renamedFiles)
    {
        var relativeSegments = new List<string>();
        var plannedMoves = new List<PlannedFileMove>();
        var plannedRenamedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reservationsByFolder = new Dictionary<string, TargetFolderReservation>(StringComparer.OrdinalIgnoreCase);
        var visitedNodes = new HashSet<MediaNode>();

        if (!TryPlanAssetFolderMovesRecursive(
                node,
                oldBaseSegments,
                newBaseSegments,
                relativeSegments,
                plannedMoves,
                plannedRenamedFiles,
                reservationsByFolder,
                visitedNodes,
                depth: 0))
        {
            return false;
        }

        if (plannedMoves.Count == 0)
        {
            CleanupEmptyOldFoldersRecursive(node, oldBaseSegments, relativeSegments);
            return true;
        }

        if (!TryExecutePlannedMoves(plannedMoves, plannedRenamedFiles, renamedFiles))
            return false;

        CleanupEmptyOldFoldersRecursive(node, oldBaseSegments, relativeSegments);
        return true;
    }

    public static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            if (Directory.EnumerateFileSystemEntries(path).Any())
                return;

            Directory.Delete(path);
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
