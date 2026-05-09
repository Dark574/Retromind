using System.Collections.Generic;
using System.Diagnostics;
using Retromind.Models;

namespace Retromind.Helpers;

public static partial class NodeAssetFolderHelper
{
    private static bool TryBeginNodeTraversal(
        MediaNode node,
        HashSet<MediaNode> visitedNodes,
        int depth,
        string operationName)
    {
        if (depth > MaxNodeTraversalDepth)
        {
            Debug.WriteLine(
                $"[NodeAssetFolderHelper] {operationName} aborted: max depth {MaxNodeTraversalDepth} exceeded at node '{node.Name}' ({node.Id}).");
            return false;
        }

        if (visitedNodes.Add(node))
            return true;

        Debug.WriteLine(
            $"[NodeAssetFolderHelper] {operationName} aborted: cycle/shared reference detected at node '{node.Name}' ({node.Id}).");
        return false;
    }
}
