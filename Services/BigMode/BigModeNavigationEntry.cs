using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.BigMode;

public enum BigModeNavigationEntryKind
{
    Node,
    MediaItem
}

public readonly record struct BigModeNavigationKey(BigModeNavigationEntryKind Kind, string Id);

/// <summary>
/// A non-persisted reference to a real library object, independent of its theme.
/// Keys are captured when the library snapshot is built; display data stays live.
/// </summary>
public abstract class BigModeNavigationEntry
{
    private protected BigModeNavigationEntry(BigModeNavigationKey key) => Key = key;

    public BigModeNavigationKey Key { get; }
    public BigModeNavigationEntryKind Kind => Key.Kind;
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string? CoverPath { get; }
    public abstract string? LogoPath { get; }
}

public sealed class BigModeNodeNavigationEntry : BigModeNavigationEntry
{
    internal BigModeNodeNavigationEntry(MediaNode node, string id, MediaNode? parentNode)
        : base(new BigModeNavigationKey(BigModeNavigationEntryKind.Node, id))
    {
        Node = node;
        ParentNode = parentNode;
    }

    public MediaNode Node { get; }
    public MediaNode? ParentNode { get; }
    public override string Title => Node.Name;
    public override string Description => Node.Description;
    public override string? CoverPath => Node.PrimaryCoverAbsolutePath;
    public override string? LogoPath => Node.PrimaryLogoAbsolutePath;
}

public sealed class BigModeMediaNavigationEntry : BigModeNavigationEntry
{
    internal BigModeMediaNavigationEntry(MediaItem item, string id, MediaNode sourceNode)
        : base(new BigModeNavigationKey(BigModeNavigationEntryKind.MediaItem, id))
    {
        Item = item;
        SourceNode = sourceNode;
    }

    public MediaItem Item { get; }
    public MediaNode SourceNode { get; }
    public override string Title => Item.Title;
    public override string Description => Item.Description;
    public override string? CoverPath => AssetResolver.ResolveAssetPath(Item, SourceNode, AssetType.Cover);
    public override string? LogoPath => AssetResolver.ResolveAssetPath(Item, SourceNode, AssetType.Logo);
}
