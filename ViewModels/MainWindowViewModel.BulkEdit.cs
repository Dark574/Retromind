using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task BulkEditSelectedMediaAsync()
    {
        if (CurrentWindow is not { } owner)
            return;

        var selection = SelectedNodeContent switch
        {
            MediaAreaViewModel mediaVm when mediaVm.IsMultiSelectMode => mediaVm.MultiSelection,
            SearchAreaViewModel searchVm when searchVm.IsMultiSelectMode => searchVm.MultiSelection,
            _ => null
        };

        if (selection is not { HasSelection: true })
            return;

        var libraryItems = new List<MediaItem>();
        foreach (var root in RootItems)
            CollectItemsRecursive(root, libraryItems);

        var selectedItems = selection.ResolveFrom(libraryItems);
        if (selectedItems.Count == 0)
        {
            selection.Clear();
            return;
        }

        var viewModel = new BulkEditMediaViewModel(selectedItems.Count, RootItems);
        var dialog = new BulkEditMediaView { DataContext = viewModel };
        var accepted = await dialog.ShowDialog<bool>(owner);

        if (!accepted || viewModel.ResultPatch is not { } patch)
            return;

        try
        {
            if (ShouldCreateAutomaticMetadataBackup(MetadataBackupReason.BeforeBulkEdit))
                await CreateCurrentMetadataBackupAsync(MetadataBackupReason.BeforeBulkEdit);
        }
        catch (Exception ex)
        {
            var format = T(
                "MetadataBackup.BeforeBulkEditFailedFormat",
                "The selected items were not changed because the safety backup could not be created.\n\n{0}");
            await ShowInfoDialog(owner, string.Format(format, ex.Message));
            return;
        }

        var changed = selectedItems.Aggregate(
            false,
            (anyChanged, item) => patch.ApplyTo(item) || anyChanged);

        // A completed operation starts a fresh batch while keeping selection mode active.
        selection.Clear();

        if (!changed)
            return;

        _libraryTracker.MarkDirty();
        await SaveData();

        if (_currentSearchAreaVm is { } currentSearchVm)
        {
            currentSearchVm.RefreshResults();
            return;
        }

        var parentNode = selectedItems
            .Select(item => FindParentNode(RootItems, item))
            .FirstOrDefault(node => node != null);
        if (parentNode != null)
            await RefreshItemPresentationAsync(parentNode);
    }
}
