using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

// Capture and apply on the UI thread. The worker reads only the captured paths and prefixes.
internal sealed class StartupAssetScanSnapshot
{
    internal const int ApplyBatchSize = 500;

    private readonly List<MediaNode> _chain;

    private StartupAssetScanSnapshot(List<MediaNode> chain, CancellationToken cancellationToken)
    {
        _chain = chain;
        NodePath = chain.Select(node => node.Name).ToList();
        var node = chain[^1];
        Items = new ItemSnapshot[node.Items.Count];
        for (var i = 0; i < Items.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items[i] = new ItemSnapshot(node.Items[i]);
        }
        Children = node.Children.ToArray();
    }

    internal List<string> NodePath { get; }
    internal ItemSnapshot[] Items { get; }
    internal MediaNode[] Children { get; }

    internal static StartupAssetScanSnapshot? Capture(ObservableCollection<MediaNode> roots, MediaNode node,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chain = PathHelper.GetNodeChain(node, roots);
        return chain.Count == 0 ? null : new StartupAssetScanSnapshot(chain, cancellationToken);
    }

    internal bool ApplyBatch(ObservableCollection<MediaNode> roots,
        List<(ItemSnapshot Item, List<MediaAsset> Assets)> scanned, int start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var siblings = roots;
        for (var i = 0; i < _chain.Count; i++)
        {
            var node = _chain[i];
            if (node.Name != NodePath[i] || !siblings.Contains(node))
                return false;
            siblings = node.Children;
        }

        var items = _chain[^1].Items;
        var changed = false;
        var end = start + Math.Min(ApplyBatchSize, scanned.Count - start);
        for (var i = start; i < end; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            var (snapshot, assets) = scanned[i];
            // An insertion, move or reorder can invalidate the rest of this snapshot.
            if (i >= items.Count || !ReferenceEquals(items[i], snapshot.Item) || !snapshot.IsCurrent())
                continue;
            var existing = snapshot.Item.Assets;
            if (AssetsMatch(existing, assets))
                continue;

            existing.Clear();
            foreach (var asset in assets)
                existing.Add(asset);
            changed = true;
        }
        return changed;
    }

    private static bool AssetsMatch(IList<MediaAsset> existing, List<MediaAsset> scanned)
    {
        if (existing.Count != scanned.Count)
            return false;

        var paths = new HashSet<(AssetType Type, string Path)>(existing.Count);
        foreach (var asset in existing)
            paths.Add((asset.Type, asset.RelativePath));

        foreach (var asset in scanned)
        {
            if (!paths.Remove((asset.Type, asset.RelativePath)))
                return false;
        }
        return true;
    }

    internal readonly struct ItemSnapshot
    {
        private readonly string _title;
        private readonly string _id;
        private readonly ObservableCollection<MediaAsset> _collection;
        private readonly (MediaAsset Asset, Guid Id, AssetType Type, string Path)[] _assets;

        internal ItemSnapshot(MediaItem item)
        {
            Item = item;
            _title = item.Title;
            _id = item.Id;
            _collection = item.Assets;
            _assets = _collection.Select(asset => (asset, asset.Id, asset.Type, asset.RelativePath)).ToArray();
            Prefix = FileManagementService.BuildItemAssetPrefix(_title, _id);
        }

        internal MediaItem Item { get; }
        internal string Prefix { get; }

        internal bool IsCurrent()
        {
            if (Item.Title != _title || Item.Id != _id || Item.HasTransientAssetPreviews ||
                !ReferenceEquals(Item.Assets, _collection) || _collection.Count != _assets.Length)
                return false;

            for (var i = 0; i < _assets.Length; i++)
            {
                var asset = _collection[i];
                if (_assets[i] != (asset, asset.Id, asset.Type, asset.RelativePath))
                    return false;
            }
            return true;
        }
    }
}
