using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool CanUninstallEpicMedia(MediaItem? item) =>
        !IsLaunchInProgress && EpicMediaItemStateHelper.CanUninstall(item);

    private async Task UninstallEpicMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner)
            return;

        _ = await RunEpicUninstallAsync(item, owner);
    }

    private async Task<bool> RunEpicUninstallAsync(MediaItem item, Window owner)
    {
        if (!CanUninstallEpicMedia(item))
            return false;

        if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) &&
            StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath) &&
            IsInstallDirectoryUsedByAnotherItem(installPath, item))
        {
            await ShowInfoDialog(
                owner,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Epic.Uninstall.SharedInstallDirectoryFormat",
                        "The game folder is also used by another library item and will not be deleted:\n{0}"),
                    installPath));
            return false;
        }

        var preserveSharedPrefix = IsPrefixUsedByAnotherItem(item.PrefixPath, item);
        var confirmMessage = string.Format(
            CultureInfo.CurrentCulture,
            preserveSharedPrefix
                ? T(
                    "Epic.Uninstall.ConfirmSharedPrefixFormat",
                    "Uninstall \"{0}\"?\n\nThe game files will be deleted and the installation will be unregistered from Legendary. Its Wine/Proton prefix is also used by another item and will be preserved. The Retromind library item, media and metadata remain available.\n\nThis action cannot be undone.")
                : T(
                    "Epic.Uninstall.ConfirmFormat",
                    "Uninstall \"{0}\"?\n\nThe game files and its Wine/Proton prefix will be deleted, and the installation will be unregistered from Legendary. The Retromind library item, media and metadata remain available.\n\nThis action cannot be undone."),
            item.Title);
        if (!await ShowConfirmDialog(owner, confirmMessage))
            return false;

        if (!await EnsureEpicSignInAsync(owner))
            return false;

        var progressTitle = string.Format(
            CultureInfo.CurrentCulture,
            T("Epic.Uninstall.ProgressTitleFormat", "Uninstall Epic game - {0}"),
            string.IsNullOrWhiteSpace(item.Title) ? "Epic Games" : item.Title);
        var progressVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressView = new ProcessLogView { DataContext = progressVm };
        await UiThreadHelper.InvokeAsync(() => progressView.Show(owner));
        progressVm.EnableCancel();

        IsLaunchInProgress = true;
        try
        {
            AppendProcessLog(
                progressVm,
                T(
                    "Epic.Uninstall.Starting",
                    "Uninstalling the Epic Games title and removing its Legendary registration..."));
            var result = await _epicInstallService.UninstallAsync(
                item,
                deletePrefix: !preserveSharedPrefix,
                line => AppendProcessLog(progressVm, line),
                progressVm.Token);

            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();

            AppendProcessLog(
                progressVm,
                result.PrefixPreserved
                    ? T(
                        "Epic.Uninstall.SuccessPrefixPreserved",
                        "Game uninstalled successfully. The Wine/Proton prefix was preserved.")
                    : T("Epic.Uninstall.Success", "Game uninstalled successfully."));
            return true;
        }
        catch (OperationCanceledException)
        {
            progressVm.MarkCancelled(
                T("Epic.Uninstall.Cancelled", "Uninstallation cancelled."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Uninstall failed: {ex.Message}");
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Uninstall.FailedFormat", "Uninstallation failed: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }
        finally
        {
            progressVm.MarkFinished();
            UiThreadHelper.Post(() => progressVm.IsRunning = false);
            IsLaunchInProgress = false;
        }
    }
}
