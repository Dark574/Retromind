using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

internal static class DesktopMediaProjectionBuilder
{
    private sealed record AssetSnapshot(AssetType Type, string RelativePath);

    internal readonly record struct AssetSelection(
        MediaItem Item, string? Cover, string? Wallpaper, string? Video, string? Music);

    internal sealed record Projection(List<MediaItem> Items, List<AssetSelection> RandomizationPlan);

    internal static bool IsMusicRandomized(MediaNode node, ObservableCollection<MediaNode> roots) =>
        ResolveRandomization(node, roots).Music;

    private static (bool Covers, bool Music) ResolveRandomization(
        MediaNode node, ObservableCollection<MediaNode> roots)
    {
        var chain = PathHelper.GetNodeChain(node, roots, matchById: true);
        chain.Reverse();
        return (
            chain.FirstOrDefault(entry => entry.RandomizeCovers.HasValue)?.RandomizeCovers ?? false,
            chain.FirstOrDefault(entry => entry.RandomizeMusic.HasValue)?.RandomizeMusic ?? false);
    }

    internal static async Task<Projection> BuildAsync(
        MediaNode node, ObservableCollection<MediaNode> roots, bool filterProtected, CancellationToken token)
    {
        // 1. Collect items (recursive) using UI-thread snapshots for safety.
        var itemList = await CollectItemsAsync(node, filterProtected, token);

        token.ThrowIfCancellationRequested();

        // Globally sort by SortTitle (fallback: Title) so aggregated views
        // keep a user-defined series order across subcategories.
        itemList.Sort(MediaSortHelper.DisplayOrderComparer);

        // 2. Randomization logic (covers/wallpapers/videos/music)
        bool randomizeMusic = false;
        bool randomizeCovers = false;

        // Snapshot assets on the UI thread so background work never enumerates UI-bound collections.
        var assetSnapshots = new List<(MediaItem Item, List<AssetSnapshot> Assets)>(itemList.Count);
        await UiThreadHelper.InvokeAsync(() =>
        {
            (randomizeCovers, randomizeMusic) = ResolveRandomization(node, roots);

            if (!randomizeCovers && !randomizeMusic)
                return;

            foreach (var item in itemList)
            {
                var assets = item.Assets
                    .Select(a => new AssetSnapshot(a.Type, a.RelativePath))
                    .ToList();

                assetSnapshots.Add((item, assets));
            }
        });

        var randomizationPlan = new List<AssetSelection>(itemList.Count);

        if (!randomizeCovers && !randomizeMusic)
        {
            // Ensure any previous randomization overrides are cleared.
            for (int i = 0; i < itemList.Count; i++)
                randomizationPlan.Add(new AssetSelection(itemList[i], null, null, null, null));

            return new Projection(itemList, randomizationPlan);
        }

        foreach (var snapshot in assetSnapshots)
        {
            token.ThrowIfCancellationRequested();

            var item = snapshot.Item;
            var assets = snapshot.Assets;

            List<AssetSnapshot>? covers = null;
            List<AssetSnapshot>? wallpapers = null;
            List<AssetSnapshot>? videos = null;
            List<AssetSnapshot>? music = null;

            foreach (var asset in assets)
            {
                if (randomizeCovers)
                {
                    if (asset.Type == AssetType.Cover)
                        (covers ??= new()).Add(asset);
                    else if (asset.Type == AssetType.Wallpaper)
                        (wallpapers ??= new()).Add(asset);
                    else if (asset.Type == AssetType.Video)
                        (videos ??= new()).Add(asset);
                }

                if (randomizeMusic && asset.Type == AssetType.Music)
                    (music ??= new()).Add(asset);
            }

            string? coverWinner = null;
            string? wallpaperWinner = null;
            string? videoWinner = null;
            string? musicWinner = null;

            if (randomizeCovers)
            {
                if (covers is { Count: > 1 })
                    coverWinner = RandomHelper.PickRandom(covers)?.RelativePath;

                if (wallpapers is { Count: > 1 })
                    wallpaperWinner = RandomHelper.PickRandom(wallpapers)?.RelativePath;

                if (videos is { Count: > 1 })
                    videoWinner = RandomHelper.PickRandom(videos)?.RelativePath;
            }

            if (randomizeMusic && music is { Count: > 1 })
                musicWinner = RandomHelper.PickRandom(music)?.RelativePath;

            randomizationPlan.Add(new AssetSelection(item, coverWinner, wallpaperWinner, videoWinner, musicWinner));
        }

        return new Projection(itemList, randomizationPlan);
    }

    internal static async Task<List<MediaItem>> CollectItemsAsync(
        MediaNode node, bool filterProtected, CancellationToken token)
    {
        var items = new List<MediaItem>();
        await CollectItemsRecursiveSnapshotAsync(node, items, token, filterProtected);
        return items;
    }

    private static async Task CollectItemsRecursiveSnapshotAsync(
        MediaNode node,
        List<MediaItem> targetList,
        CancellationToken token,
        bool filterProtected)
    {
        token.ThrowIfCancellationRequested();

        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            items = node.Items.ToList();
            children = node.Children.ToList();
        });

        if (filterProtected)
            targetList.AddRange(items.Where(item => !item.IsProtected));
        else
            targetList.AddRange(items);

        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            await CollectItemsRecursiveSnapshotAsync(child, targetList, token, filterProtected);
        }
    }

    /// <summary>
    /// Applies the precomputed randomization winners to the given items.
    /// Must be called on the UI thread because it mutates model objects
    /// that are bound directly into the view.
    /// </summary>
    internal static void ApplyRandomizationPlan(IReadOnlyList<AssetSelection> randomizationPlan)
    {
        foreach (var plan in randomizationPlan)
        {
            plan.Item.ResetActiveAssets();

            if (!string.IsNullOrEmpty(plan.Cover))
                plan.Item.SetActiveAsset(AssetType.Cover, plan.Cover);

            if (!string.IsNullOrEmpty(plan.Wallpaper))
                plan.Item.SetActiveAsset(AssetType.Wallpaper, plan.Wallpaper);

            if (!string.IsNullOrEmpty(plan.Video))
                plan.Item.SetActiveAsset(AssetType.Video, plan.Video);

            if (!string.IsNullOrEmpty(plan.Music))
                plan.Item.SetActiveAsset(AssetType.Music, plan.Music);
        }
    }
}
