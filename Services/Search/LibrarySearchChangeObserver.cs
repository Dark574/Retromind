using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Retromind.Models;

namespace Retromind.Services.Search;

/// <summary>
/// Model-thread subscriptions for a search consumer. Does not own results or cancellation.
/// Structural subscriptions are reconciled before the next capture, after the input debounce.
/// Non-observable in-place dictionary/file edits still require the editor's explicit refresh.
/// </summary>
internal sealed class LibrarySearchChangeObserver : IDisposable
{
    private readonly IEnumerable<MediaNode> _roots;
    private readonly Action<bool> _changed;
    private readonly INotifyCollectionChanged? _rootChanges;
    private readonly Dictionary<MediaNode, (ObservableCollection<MediaNode> Children, ObservableCollection<MediaItem> Items)> _nodes = new();
    private readonly Dictionary<MediaItem, (ObservableCollection<string> Tags, ObservableCollection<MediaAsset> Assets)> _items = new();
    private readonly HashSet<MediaAsset> _assets = new();
    // Reuse delegates across large libraries instead of allocating one per subscription/removal.
    private readonly PropertyChangedEventHandler _nodeChanged;
    private readonly PropertyChangedEventHandler _itemChanged;
    private readonly PropertyChangedEventHandler _assetChanged;
    private readonly NotifyCollectionChangedEventHandler _structureChanged;
    private readonly NotifyCollectionChangedEventHandler _valuesChanged;
    private readonly NotifyCollectionChangedEventHandler _assetsChanged;
    private bool _needsRebind = true;
    private bool _disposed;

    private static readonly HashSet<string> SearchProperties = new(StringComparer.Ordinal)
    {
        nameof(MediaItem.Id), nameof(MediaItem.Title), nameof(MediaItem.SortTitle), nameof(MediaItem.Description),
        nameof(MediaItem.Developer), nameof(MediaItem.Publisher), nameof(MediaItem.Platform), nameof(MediaItem.Source),
        nameof(MediaItem.Genre), nameof(MediaItem.Series), nameof(MediaItem.ReleaseType), nameof(MediaItem.PlayMode),
        nameof(MediaItem.MaxPlayers), nameof(MediaItem.ReleaseDate), nameof(MediaItem.Rating), nameof(MediaItem.Status),
        nameof(MediaItem.IsFavorite), nameof(MediaItem.IsProtected), nameof(MediaItem.MediaType),
        nameof(MediaItem.HasPlayedForSearch), nameof(MediaItem.StoreProviderId), nameof(MediaItem.GogUpdateSearchState),
        nameof(MediaItem.CustomFields), nameof(MediaItem.Tags), nameof(MediaItem.Assets)
    };

    internal LibrarySearchChangeObserver(IEnumerable<MediaNode> roots, Action<bool> changed)
    {
        _nodeChanged = OnNodeChanged;
        _itemChanged = OnItemChanged;
        _assetChanged = OnAssetChanged;
        _structureChanged = OnStructureChanged;
        _valuesChanged = OnValuesChanged;
        _assetsChanged = OnAssetsChanged;
        _roots = roots;
        _changed = changed;
        _rootChanges = roots as INotifyCollectionChanged;
        if (_rootChanges != null) _rootChanges.CollectionChanged += _structureChanged;
        RefreshSubscriptions();
    }

    internal void RefreshSubscriptions()
    {
        if (_disposed || !_needsRebind) return;
        DetachModels();
        var stack = new Stack<MediaNode>(_roots);
        while (stack.TryPop(out var node))
        {
            if (!_nodes.TryAdd(node, (node.Children, node.Items))) continue;
            node.PropertyChanged += _nodeChanged;
            node.Children.CollectionChanged += _structureChanged;
            node.Items.CollectionChanged += _structureChanged;
            foreach (var child in node.Children) stack.Push(child);
            foreach (var item in node.Items)
            {
                if (!_items.TryAdd(item, (item.Tags, item.Assets))) continue;
                item.PropertyChanged += _itemChanged;
                item.Tags.CollectionChanged += _valuesChanged;
                item.Assets.CollectionChanged += _assetsChanged;
                foreach (var asset in item.Assets)
                    if (_assets.Add(asset)) asset.PropertyChanged += _assetChanged;
            }
        }
        _needsRebind = false;
    }

    private void OnStructureChanged(object? sender, NotifyCollectionChangedEventArgs e) => Invalidate(true);
    private void OnValuesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Invalidate(false);
    private void OnAssetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _needsRebind = true;
        Invalidate(false);
    }
    private void OnAssetChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(MediaAsset.RelativePath)) Invalidate(false);
    }
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaNode.Name)) { Invalidate(false); return; }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MediaNode.Id) or nameof(MediaNode.Children) or nameof(MediaNode.Items))
            Invalidate(true);
    }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) && !SearchProperties.Contains(e.PropertyName)) return;
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(MediaItem.Tags) or nameof(MediaItem.Assets)) _needsRebind = true;
        Invalidate(false);
    }
    private void Invalidate(bool structure)
    {
        if (_disposed) return;
        _needsRebind |= structure;
        _changed(structure);
    }
    private void DetachModels()
    {
        foreach (var (node, collections) in _nodes)
        {
            node.PropertyChanged -= _nodeChanged;
            collections.Children.CollectionChanged -= _structureChanged;
            collections.Items.CollectionChanged -= _structureChanged;
        }
        foreach (var (item, collections) in _items)
        {
            item.PropertyChanged -= _itemChanged;
            collections.Tags.CollectionChanged -= _valuesChanged;
            collections.Assets.CollectionChanged -= _assetsChanged;
        }
        foreach (var asset in _assets) asset.PropertyChanged -= _assetChanged;
        _nodes.Clear(); _items.Clear(); _assets.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_rootChanges != null) _rootChanges.CollectionChanged -= _structureChanged;
        DetachModels();
    }
}
