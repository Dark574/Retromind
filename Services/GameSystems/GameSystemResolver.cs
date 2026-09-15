using System;
using System.Collections.ObjectModel;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.GameSystems;

/// <summary>
/// Resolves provider-neutral system identity through Item -> nearest Node.
/// </summary>
public static class GameSystemResolver
{
    public static string? ResolveForItem(
        MediaItem item,
        MediaNode? parentNode,
        ObservableCollection<MediaNode> rootNodes)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(rootNodes);

        var itemSystemId = GameSystemCatalog.NormalizeId(item.GameSystemId);
        return itemSystemId ?? ResolveForNode(parentNode, rootNodes);
    }

    public static string? ResolveForNode(
        MediaNode? node,
        ObservableCollection<MediaNode> rootNodes)
    {
        ArgumentNullException.ThrowIfNull(rootNodes);

        if (node == null)
            return null;

        var chain = PathHelper.GetNodeChain(node, rootNodes, matchById: true);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var systemId = GameSystemCatalog.NormalizeId(chain[i].GameSystemId);
            if (systemId != null)
                return systemId;
        }

        return null;
    }
}
