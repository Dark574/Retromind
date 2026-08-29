using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task MoveMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner)
            return;

        var sourceNode = FindParentNode(RootItems, item);
        if (sourceNode == null)
            return;

        var dialogViewModel = new MoveMediaDialogViewModel(RootItems, item, sourceNode);
        var dialog = new MoveMediaDialogView { DataContext = dialogViewModel };
        dialogViewModel.RequestClose += accepted => dialog.Close(accepted);

        var accepted = await dialog.ShowDialog<bool>(owner);
        if (!accepted)
            return;

        var targetNode = dialogViewModel.GetSelectedTarget();
        if (targetNode == null)
            return;

        await TryMoveMediaAsync(item, targetNode);
    }

    public bool CanMoveMediaItemTo(MediaItem? item, MediaNode? targetNode)
    {
        if (item == null || targetNode == null)
            return false;

        var sourceNode = FindParentNode(RootItems, item);
        return sourceNode != null && MediaItemMovePolicy.Assess(item, sourceNode, targetNode).IsAllowed;
    }

    public async Task<bool> TryMoveMediaAsync(MediaItem? item, MediaNode? targetNode)
    {
        if (item == null || targetNode == null || CurrentWindow is not { } owner)
            return false;

        var sourceNode = FindParentNode(RootItems, item);
        if (sourceNode == null)
            return false;

        var assessment = MediaItemMovePolicy.Assess(item, sourceNode, targetNode);
        if (!assessment.IsAllowed)
            return false;

        var confirmation = string.Format(
            T(
                "MoveMedia.ConfirmFormat",
                "Move '{0}' from '{1}' to '{2}'?\n\nAssociated assets will be moved as well. Settings inherited from the category may change."),
            item.Title,
            sourceNode.Name,
            targetNode.Name);

        if (MediaItemMovePolicy.IsLeavingSynchronizedNode(sourceNode, targetNode))
        {
            confirmation += Environment.NewLine + Environment.NewLine + T(
                "MoveMedia.StoreSyncWarning",
                "Warning: the source is a synchronized store category. A later store sync may add the item there again.");
        }

        if (!await ShowConfirmDialog(owner, confirmation))
            return false;

        // Revalidate after the dialogs in case a collection changed in the meantime.
        if (!sourceNode.Items.Contains(item) ||
            !MediaItemMovePolicy.Assess(item, sourceNode, targetNode).IsAllowed)
        {
            await ShowInfoDialog(
                owner,
                T("MoveMedia.TargetChanged", "The source or target category changed. Please select the target again."));
            return false;
        }

        var allItems = new List<MediaItem>();
        var nodeAssetReferences = new List<MediaAsset>();
        foreach (var rootNode in RootItems)
        {
            CollectItemsRecursive(rootNode, allItems);
            CollectNodeAssetReferencesRecursive(rootNode, nodeAssetReferences);
        }

        var sourcePath = PathHelper.GetNodePath(sourceNode, RootItems);
        var targetPath = PathHelper.GetNodePath(targetNode, RootItems);
        var sourceIndex = sourceNode.Items.IndexOf(item);
        try
        {
            sourceNode.Items.RemoveAt(sourceIndex);
            InsertMediaItemSorted(targetNode.Items, item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ItemMove] Failed to update node collections: {ex}");

            if (targetNode.Items.Contains(item))
                targetNode.Items.Remove(item);
            if (!sourceNode.Items.Contains(item))
                sourceNode.Items.Insert(Math.Clamp(sourceIndex, 0, sourceNode.Items.Count), item);

            await ShowInfoDialog(
                owner,
                T("MoveMedia.CollectionMoveFailed", "The category assignment could not be changed."));
            return false;
        }

        var assetMove = _fileService.MoveItemAssets(
            item,
            sourcePath,
            targetPath,
            allItems,
            nodeAssetReferences);
        if (!assetMove.Success)
        {
            targetNode.Items.Remove(item);
            sourceNode.Items.Insert(Math.Clamp(sourceIndex, 0, sourceNode.Items.Count), item);

            var format = T(
                "MoveMedia.AssetMoveFailedFormat",
                "The item could not be moved because its assets could not be moved safely.\n\n{0}");
            await ShowInfoDialog(owner, string.Format(format, assetMove.ErrorMessage ?? string.Empty));
            return false;
        }

        // Auto-protection is the one inherited node policy that is materialized on
        // an item when it becomes a new child. Launcher, wrapper and environment
        // inheritance remain dynamic and resolve through the new parent node.
        _isApplyingProtectionChanges = true;
        try
        {
            ApplyEffectiveParentalProtection(targetNode, [item]);
            RefreshAncestorAutoProtectStates(sourceNode);
            RefreshAncestorAutoProtectStates(targetNode);
        }
        finally
        {
            _isApplyingProtectionChanges = false;
        }

        RefreshTreeVisibility();

        foreach (var nodeId in _lastSelectedMediaByNodeId
                     .Where(pair => string.Equals(pair.Value, item.Id, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _lastSelectedMediaByNodeId.Remove(nodeId);
        }

        if (string.Equals(_currentSettings.LastSelectedMediaId, item.Id, StringComparison.Ordinal))
            _currentSettings.LastSelectedMediaId = null;

        _audioService.StopMusic();
        _libraryTracker.MarkDirty();
        await SaveData();
        UpdateContent();
        return true;
    }

    private static void CollectNodeAssetReferencesRecursive(MediaNode node, ICollection<MediaAsset> target)
    {
        foreach (var asset in node.Assets)
            target.Add(asset);

        foreach (var child in node.Children)
            CollectNodeAssetReferencesRecursive(child, target);
    }
}
