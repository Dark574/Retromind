using System.ComponentModel;
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
public abstract class BigModeNavigationEntry : INotifyPropertyChanged
{
    private protected BigModeNavigationEntry(BigModeNavigationKey key) => Key = key;

    public BigModeNavigationKey Key { get; }
    public BigModeNavigationEntryKind Kind => Key.Kind;
    public abstract string Title { get; }
    public abstract string Description { get; }
    public abstract string? CoverPath { get; }
    public abstract string? LogoPath { get; }
    public string? PrimaryCoverPath => CoverPath;
    public string? PrimaryLogoPath => LogoPath;
    public bool HasNodeLogoFallback => Kind == BigModeNavigationEntryKind.Node &&
        string.IsNullOrWhiteSpace(CoverPath) && !string.IsNullOrWhiteSpace(LogoPath);
    public bool ShowCoverTitle => string.IsNullOrWhiteSpace(CoverPath) && !HasNodeLogoFallback;
    public virtual string? ItemLogoPath => LogoPath;

    protected abstract INotifyPropertyChanged Model { get; }
    protected virtual INotifyPropertyChanged? OwnerModel => null;
    private PropertyChangedEventHandler? _propertyChanged;
    // Only realized rows subscribe to their models. Unused entries do not retain
    // subscriptions across snapshot replacement or expand a large library's work.
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            if (value == null) return;
            if (_propertyChanged == null)
            {
                Model.PropertyChanged += OnModelChanged;
                if (OwnerModel is { } owner) owner.PropertyChanged += OnModelChanged;
            }
            _propertyChanged += value;
        }
        remove
        {
            _propertyChanged -= value;
            if (_propertyChanged == null)
            {
                Model.PropertyChanged -= OnModelChanged;
                if (OwnerModel is { } owner) owner.PropertyChanged -= OnModelChanged;
            }
        }
    }
    private void OnModelChanged(object? sender, PropertyChangedEventArgs args) =>
        _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
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
    protected override INotifyPropertyChanged Model => Node;
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
    protected override INotifyPropertyChanged Model => Item;
    protected override INotifyPropertyChanged? OwnerModel => SourceNode;
    public MediaNode SourceNode { get; }
    public override string? ItemLogoPath => Item.ItemLogoPath;
    public override string Title => Item.Title;
    public override string Description => Item.Description;
    public override string? CoverPath => AssetResolver.ResolveAssetPath(Item, SourceNode, AssetType.Cover);
    public override string? LogoPath => AssetResolver.ResolveAssetPath(Item, SourceNode, AssetType.Logo);
}
