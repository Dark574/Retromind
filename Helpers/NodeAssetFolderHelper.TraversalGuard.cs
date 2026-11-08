using System.Collections.Generic;
using System;
using Retromind.Models;

namespace Retromind.Helpers;

public static partial class NodeAssetFolderHelper
{
    public static NodeAssetOperationResult ValidateTraversal(IEnumerable<MediaNode> roots, int initialDepth = 0)
    {
        var result = new NodeAssetOperationResult();
        TrySnapshotNodes(roots, result, out _, initialDepth);
        return result;
    }

    private static bool TrySnapshotNodes(IEnumerable<MediaNode> roots, NodeAssetOperationResult result,
        out List<MediaNode> nodes, int initialDepth = 0)
    {
        nodes = new List<MediaNode>();
        var snapshot = nodes;
        var visited = new HashSet<MediaNode>(ReferenceEqualityComparer.Instance);
        bool Visit(MediaNode node, int depth)
        {
            if (node == null || !TryBeginNodeTraversal(node, visited, depth, "Validate"))
            {
                result.FailureReason = node == null ? "MissingNode"
                    : depth > MaxNodeTraversalDepth ? "MaximumDepthExceeded" : "CycleOrSharedReference";
                return false;
            }
            snapshot.Add(node);
            foreach (var child in node.Children)
                if (!Visit(child, depth + 1)) return false;
            return true;
        }
        try
        {
            foreach (var root in roots)
                if (!Visit(root, initialDepth))
                {
                    result.Fail("NodeAssets.TraversalFailed");
                    return false;
                }
            result.Success = true;
            return true;
        }
        catch (Exception ex)
        {
            result.Fail("NodeAssets.TraversalFailed", ex);
            return false;
        }
    }

    private static bool TryBeginNodeTraversal(
        MediaNode node,
        HashSet<MediaNode> visitedNodes,
        int depth,
        string operationName)
    {
        return depth <= MaxNodeTraversalDepth && visitedNodes.Add(node);
    }
}
