using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to navigate and resolve paths within the MediaNode tree structure.
/// </summary>
public static class PathHelper
{
    public static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "Unknown";

        var sanitized = input.Replace("/", "")
            .Replace("\\", "")
            .Replace(" ", "_");

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(c.ToString(), "");
        }

        while (sanitized.Contains("__"))
            sanitized = sanitized.Replace("__", "_");

        if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
            return "Unknown";

        return sanitized;
    }

    /// <summary>
    /// Traverses the tree from the roots to find the path to the specified target node.
    /// Returns a list of logical node names (e.g., ["Games", "SNES", "RPG"]).
    /// Note: These names are raw and might contain characters invalid for file systems.
    /// </summary>
    /// <param name="targetNode">The node to find the path for.</param>
    /// <param name="roots">The collection of root nodes to start searching from.</param>
    /// <returns>A list of strings representing the path.</returns>
    public static List<string> GetNodePath(MediaNode targetNode, ObservableCollection<MediaNode> roots)
    {
        var pathStack = new List<string>();
        
        foreach (var root in roots)
        {
            if (FindPathRecursive(root, targetNode, pathStack))
            {
                return pathStack;
            }
        }

        // Fallback: If not found in the tree (e.g. detached node), return just its own name.
        return new List<string> { targetNode.Name };
    }

    private static bool FindPathRecursive(MediaNode current, MediaNode target, List<string> pathStack)
    {
        pathStack.Add(current.Name);

        // Check by Reference (or ID if you prefer stricter checks)
        if (current == target) return true;

        foreach (var child in current.Children)
        {
            if (FindPathRecursive(child, target, pathStack))
            {
                return true;
            }
        }

        // Not found in this branch, backtrack.
        pathStack.RemoveAt(pathStack.Count - 1);
        return false;
    }

    public static List<MediaNode> GetNodeChain(
        MediaNode target,
        ObservableCollection<MediaNode> nodes,
        bool matchById = false)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target) || (matchById && node.Id == target.Id))
                return new List<MediaNode> { node };

            var chain = GetNodeChain(target, node.Children, matchById);
            if (chain.Count > 0)
            {
                chain.Insert(0, node);
                return chain;
            }
        }

        return new List<MediaNode>();
    }

    public static string BuildUniqueNodeNameSuggestion(
        string baseName,
        HashSet<string> existingRaw,
        HashSet<string> existingSanitized)
    {
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (existingRaw.Contains(candidate))
                continue;

            var sanitizedCandidate = SanitizePathSegment(candidate);
            if (existingSanitized.Contains(sanitizedCandidate))
                continue;

            return candidate;
        }

        return string.Empty;
    }

    public static string ResolveNodeFolder(List<string> nodePathSegments, string libraryRootPath)
    {
        if (nodePathSegments is not { Count: > 0 })
            return libraryRootPath;

        var rawPath = Path.Combine(libraryRootPath, Path.Combine(nodePathSegments.ToArray()));

        var sanitizedStack = nodePathSegments
            .Select(SanitizePathSegment)
            .ToArray();
        var sanitizedPath = Path.Combine(libraryRootPath, Path.Combine(sanitizedStack));

        if (string.Equals(rawPath, sanitizedPath, StringComparison.Ordinal))
            return rawPath;

        if (Directory.Exists(rawPath))
            return rawPath;

        return sanitizedPath;
    }
}
