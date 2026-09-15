using System.Collections.ObjectModel;
using Retromind.Models;
using Retromind.Services.GameSystems;

namespace Retromind.Tests.Services.GameSystems;

public sealed class GameSystemResolverTests
{
    [Fact]
    public void ResolveForItem_ItemOverrideWinsOverNearestNode()
    {
        var item = new MediaItem("Game") { GameSystemId = "nintendo.game-boy-advance" };
        var parent = new MediaNode("SNES", NodeType.Group)
        {
            GameSystemId = "nintendo.snes",
            Items = new ObservableCollection<MediaItem> { item }
        };
        var roots = new ObservableCollection<MediaNode> { parent };

        var result = GameSystemResolver.ResolveForItem(item, parent, roots);

        Assert.Equal("nintendo.game-boy-advance", result);
    }

    [Fact]
    public void ResolveForItem_UsesNearestConfiguredNode()
    {
        var item = new MediaItem("Game");
        var child = new MediaNode("Favorites", NodeType.Group)
        {
            Items = new ObservableCollection<MediaItem> { item }
        };
        var platform = new MediaNode("PlayStation", NodeType.Group)
        {
            GameSystemId = "sony.playstation",
            Children = new ObservableCollection<MediaNode> { child }
        };
        var root = new MediaNode("Games", NodeType.Area)
        {
            GameSystemId = "arcade",
            Children = new ObservableCollection<MediaNode> { platform }
        };
        var roots = new ObservableCollection<MediaNode> { root };

        var result = GameSystemResolver.ResolveForItem(item, child, roots);

        Assert.Equal("sony.playstation", result);
    }

    [Fact]
    public void ResolveForNode_PreservesUnknownFutureIdentifier()
    {
        var node = new MediaNode("Future", NodeType.Group)
        {
            GameSystemId = "vendor.future-system"
        };
        var roots = new ObservableCollection<MediaNode> { node };

        var result = GameSystemResolver.ResolveForNode(node, roots);

        Assert.Equal("vendor.future-system", result);
    }

    [Fact]
    public void CatalogUsesUniqueStableIdentifiers()
    {
        var ids = GameSystemCatalog.All.Select(system => system.Id).ToList();

        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.Equal(id.ToLowerInvariant(), id));
    }
}
