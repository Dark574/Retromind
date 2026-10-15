using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool CanManageEpicDlcs(MediaItem? item) =>
        !IsLaunchInProgress && EpicMediaItemStateHelper.IsRetromindManagedInstallation(item);

    private async Task ShowEpicDlcManagerAsync(MediaItem? item)
    {
        if (!CanManageEpicDlcs(item) || item == null || CurrentWindow is not { } owner)
            return;
        if (!await EnsureEpicSignInAsync(owner))
            return;

        var dialog = new EpicDlcDialogView();
        var viewModel = new EpicDlcDialogViewModel(
            _epicInstallService,
            item,
            (entry, ct) => InstallOrUpdateEpicDlcAsync(item, entry, dialog, ct),
            (entry, ct) => UninstallEpicDlcAsync(item, entry, dialog, ct));
        dialog.DataContext = viewModel;
        await dialog.ShowDialog(owner);
    }

    private async Task<bool> InstallOrUpdateEpicDlcAsync(
        MediaItem item,
        EpicDlcCatalogEntry entry,
        Window owner,
        CancellationToken dialogCancellationToken)
    {
        if (!await EnsureEpicSignInAsync(owner) ||
            !item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) ||
            !StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath))
        {
            return false;
        }

        LegendaryInstallEstimate? estimate = null;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                estimate = await _epicInstallService.GetInstallEstimateAsync(
                    entry.AppName,
                    dialogCancellationToken);
            }
        }
        catch (OperationCanceledException) when (dialogCancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] DLC install-size estimate failed: {ex.Message}");
            if (!await ShowConfirmDialog(
                    owner,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Dlc.SpaceEstimateFailedFormat",
                            "Epic Games did not provide a usable installation size for DLC \"{0}\". Retromind cannot check the required free space in advance. Legendary will still perform its own exact check before downloading.\n\nContinue?"),
                        entry.Title)))
            {
                return false;
            }
        }

        if (estimate != null && !await ConfirmEpicInstallSpaceAsync(owner, installPath, estimate))
            return false;

        var progressTitle = string.Format(
            CultureInfo.CurrentCulture,
            T("Epic.Dlc.ProgressTitleFormat", "Epic DLC - {0}"),
            entry.Title);
        var progressVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressView = new ProcessLogView { DataContext = progressVm };
        await UiThreadHelper.InvokeAsync(() => progressView.Show(owner));
        progressVm.EnableCancel();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            dialogCancellationToken,
            progressVm.Token);
        try
        {
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    entry.IsInstalled
                        ? T("Epic.Dlc.UpdateStartingFormat", "Updating DLC \"{0}\" through Legendary...")
                        : T("Epic.Dlc.InstallStartingFormat", "Installing DLC \"{0}\" through Legendary..."),
                    entry.Title));
            await _epicInstallService.InstallDlcAsync(
                item,
                entry.AppName,
                line => AppendProcessLog(progressVm, line),
                linkedCancellation.Token);
            AppendProcessLog(
                progressVm,
                entry.IsInstalled
                    ? T("Epic.Dlc.UpdateSuccess", "DLC update completed.")
                    : T("Epic.Dlc.InstallSuccess", "DLC installation completed."));
            return true;
        }
        catch (OperationCanceledException)
        {
            progressVm.MarkCancelled(T("Epic.Dlc.OperationCancelled", "DLC operation cancelled."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] DLC installation/update failed: {ex.Message}");
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Dlc.OperationFailedFormat", "The DLC operation failed: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }
        finally
        {
            progressVm.MarkFinished();
            UiThreadHelper.Post(() => progressVm.IsRunning = false);
        }
    }

    private async Task<bool> UninstallEpicDlcAsync(
        MediaItem item,
        EpicDlcCatalogEntry entry,
        Window owner,
        CancellationToken dialogCancellationToken)
    {
        var confirmed = await ShowConfirmDialog(
            owner,
            string.Format(
                CultureInfo.CurrentCulture,
                T(
                    "Epic.Dlc.UninstallConfirmFormat",
                    "Uninstall DLC \"{0}\"?\n\nLegendary removes the files belonging to this DLC from the shared game folder. The main game and its other DLCs remain installed."),
                entry.Title));
        if (!confirmed)
            return false;

        var progressTitle = string.Format(
            CultureInfo.CurrentCulture,
            T("Epic.Dlc.UninstallProgressTitleFormat", "Uninstall Epic DLC - {0}"),
            entry.Title);
        var progressVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressView = new ProcessLogView { DataContext = progressVm };
        await UiThreadHelper.InvokeAsync(() => progressView.Show(owner));
        progressVm.EnableCancel();

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            dialogCancellationToken,
            progressVm.Token);
        try
        {
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Dlc.UninstallStartingFormat", "Uninstalling DLC \"{0}\" through Legendary..."),
                    entry.Title));
            await _epicInstallService.UninstallDlcAsync(
                item,
                entry.AppName,
                line => AppendProcessLog(progressVm, line),
                linkedCancellation.Token);
            AppendProcessLog(progressVm, T("Epic.Dlc.UninstallSuccess", "DLC uninstalled."));
            return true;
        }
        catch (OperationCanceledException)
        {
            progressVm.MarkCancelled(T("Epic.Dlc.OperationCancelled", "DLC operation cancelled."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] DLC uninstall failed: {ex.Message}");
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Dlc.OperationFailedFormat", "The DLC operation failed: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }
        finally
        {
            progressVm.MarkFinished();
            UiThreadHelper.Post(() => progressVm.IsRunning = false);
        }
    }
}
