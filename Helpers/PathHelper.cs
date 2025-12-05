using System.Collections.Generic;
using System.Collections.ObjectModel;
using Retromind.Models;

namespace Retromind.Helpers;

public static class PathHelper
{
    /// <summary>
    ///     Sucht den Pfad von Root bis zum targetNode.
    ///     Gibt eine Liste der Ordnernamen zurück, z.B. ["Spiele", "Action"].
    /// </summary>
    public static List<string> GetNodePath(MediaNode targetNode, ObservableCollection<MediaNode> roots)
    {
        var pathStack = new List<string>();
        foreach (var root in roots)
            if (FindPathRecursive(root, targetNode, pathStack))
                return pathStack;

        // Fallback, falls nicht gefunden (sollte nicht passieren)
        return new List<string> { targetNode.Name };
    }

    private static bool FindPathRecursive(MediaNode current, MediaNode target, List<string> pathStack)
    {
        pathStack.Add(current.Name);

        if (current == target) return true;

        foreach (var child in current.Children)
            if (FindPathRecursive(child, target, pathStack))
                return true;

        // Nicht in diesem Ast gefunden, Pfad wieder bereinigen
        pathStack.RemoveAt(pathStack.Count - 1);
        return false;
    }
}