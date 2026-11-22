using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Views;

namespace Retromind.ViewModels;

internal sealed partial class DesktopDownloadWorkflow
{
    private Window? _downloadWindow;

    internal void ShowDownloads(MediaItem? selected)
    {
        if (_downloadWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        if (workspace.GetWindow() is not { } owner)
            return;

        var titles = AllItems().GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Title, StringComparer.Ordinal);
        var dialog = new DownloadManagerView();
        var viewModel = new DownloadManagerViewModel(manager,
            id => titles.GetValueOrDefault(id) ?? AllItems().FirstOrDefault(item => item.Id == id)?.Title,
            selected, () => selected == null || workspace.FindParentNode(selected) == null ? null : StoreFor(selected),
            IsExcluded, SetExcluded, message => DesktopDialogs.ShowConfirmAsync(dialog, message));
        dialog.DataContext = viewModel;
        _downloadWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _downloadWindow = null;
        };
        dialog.Show(owner);
    }

    private static DownloadStore? StoreFor(MediaItem? item)
    {
        if (GogMediaItemStateHelper.TryGetGameId(item) != null)
            return DownloadStore.Gog;
        if (EpicMediaItemStateHelper.CanCheckUpdates(item))
            return DownloadStore.Epic;
        return null;
    }

    internal void SetExcluded(MediaItem item, bool excluded)
    {
        if (workspace.FindParentNode(item) == null)
            return;

        if (excluded)
            item.CustomFields[CustomFieldKeyHelper.StoreAutomaticUpdatesExcluded] = "true";
        else
            item.CustomFields.Remove(CustomFieldKeyHelper.StoreAutomaticUpdatesExcluded);
        workspace.MarkLibraryDirtyAndSaveSoon();
        manager.SetAutomaticUpdateExcluded(item.Id, excluded);
        if (!excluded && item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreAvailableRevision, out var revision) &&
            !string.IsNullOrWhiteSpace(revision))
        {
            EnqueueKnownUpdate(item, revision);
        }
    }
}
