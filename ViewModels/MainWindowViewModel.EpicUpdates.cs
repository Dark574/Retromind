using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private enum EpicMaintenanceOperation
    {
        Update,
        Repair
    }

    private bool CanCheckEpicUpdates(MediaItem? item) =>
        !IsLaunchInProgress &&
        EpicMediaItemStateHelper.CanCheckUpdates(item) &&
        !EpicMediaItemStateHelper.HasUpdateAvailable(item);

    private bool CanUpdateEpicMedia(MediaItem? item) =>
        !IsLaunchInProgress && EpicMediaItemStateHelper.HasUpdateAvailable(item);

    private bool CanRepairEpicMedia(MediaItem? item) =>
        !IsLaunchInProgress && EpicMediaItemStateHelper.IsRetromindManagedInstallation(item);

    private async Task CheckEpicUpdatesNowAsync(MediaItem? item)
    {
        if (!CanCheckEpicUpdates(item) || item == null || CurrentWindow is not { } owner)
            return;

        await CheckEpicUpdatesNowAsync(item, owner);
    }

    private async Task CheckEpicUpdatesNowAsync(MediaItem item, Window owner)
    {
        if (!CanCheckEpicUpdates(item))
            return;

        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName) || !await EnsureEpicSignInAsync(owner))
            return;

        try
        {
            LegendaryUpdateStatus status;
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                status = await _epicInstallService.GetUpdateStatusAsync(appName);
            }

            item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = status.InstalledVersion;
            item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] =
                status.IsUpdateAvailable ? "true" : "false";
            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();

            await ShowInfoDialog(
                owner,
                status.IsUpdateAvailable
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Update.AvailableFormat",
                            "An Epic Games update is available.\n\nInstalled: {0}\nAvailable: {1}"),
                        status.InstalledVersion,
                        status.AvailableVersion)
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Update.UpToDateFormat",
                            "The Epic Games title is up to date.\n\nInstalled version: {0}"),
                        status.InstalledVersion));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Update check failed: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Update.CheckFailedFormat", "The Epic Games update check failed: {0}"),
                    BuildShortErrorDetail(ex)));
        }
    }

    private async Task UpdateEpicMediaAsync(MediaItem? item)
    {
        if (!CanUpdateEpicMedia(item) || item == null)
            return;

        IsLaunchInProgress = true;
        try
        {
            await UpdateEpicItemAsync(item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Update failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private async Task<bool> UpdateEpicItemAsync(MediaItem item)
    {
        return await MaintainEpicItemAsync(item, EpicMaintenanceOperation.Update);
    }

    private async Task<bool> RunEpicMaintenanceFromEditorAsync(
        MediaItem item,
        Window owner,
        bool repair)
    {
        var operation = repair
            ? EpicMaintenanceOperation.Repair
            : EpicMaintenanceOperation.Update;
        var canRun = repair
            ? CanRepairEpicMedia(item)
            : CanUpdateEpicMedia(item);
        if (!canRun)
            return false;

        IsLaunchInProgress = true;
        try
        {
            return await MaintainEpicItemAsync(item, operation, owner);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] {(repair ? "Repair" : "Update")} from media editor failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private async Task RepairEpicMediaAsync(MediaItem? item)
    {
        if (!CanRepairEpicMedia(item) || item == null)
            return;

        IsLaunchInProgress = true;
        try
        {
            await MaintainEpicItemAsync(item, EpicMaintenanceOperation.Repair);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Repair failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private async Task<bool> MaintainEpicItemAsync(
        MediaItem item,
        EpicMaintenanceOperation operation,
        Window? ownerOverride = null)
    {
        var owner = ownerOverride ?? CurrentWindow;
        if (owner == null)
            return false;

        var isRepair = operation == EpicMaintenanceOperation.Repair;

        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName) ||
            !item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) ||
            !StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath))
        {
            return false;
        }

        var directoryAssessment = _epicInstallService.AssessDirectory(installPath, item);
        if (directoryAssessment.Status != EpicInstallDirectoryStatus.OwnedDirectory)
        {
            await ShowInfoDialog(owner, BuildEpicDirectoryError(directoryAssessment));
            return false;
        }

        if (!await EnsureEpicSignInAsync(owner))
            return false;

        var progressTitle = string.Format(
            CultureInfo.CurrentCulture,
            isRepair
                ? T("Epic.Repair.ProgressTitleFormat", "Repair Epic installation - {0}")
                : T("Epic.Update.ProgressTitleFormat", "Epic update - {0}"),
            string.IsNullOrWhiteSpace(item.Title) ? "Epic Games" : item.Title);
        var progressVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressView = new ProcessLogView { DataContext = progressVm };
        await UiThreadHelper.InvokeAsync(() => progressView.Show(owner));
        progressVm.EnableCancel();

        try
        {
            AppendProcessLog(
                progressVm,
                isRepair
                    ? T("Epic.Repair.Starting", "Checking the Epic Games installation and restoring missing or damaged files with Legendary...")
                    : T("Epic.Update.Starting", "Checking and applying the Epic Games update with Legendary..."));
            var installed = await _epicInstallService.InstallAsync(
                item,
                directoryAssessment.FullPath,
                line => AppendProcessLog(progressVm, line),
                progressVm.Token,
                repair: isRepair);

            if (!ApplyEpicExecutablePath(item, installed))
            {
                AppendProcessLog(
                    progressVm,
                    isRepair
                        ? T(
                            "Epic.Repair.ExecutableWarning",
                            "The repair completed, but Retromind could not refresh the game executable. The existing launch configuration was retained.")
                        : T(
                            "Epic.Update.ExecutableWarning",
                            "The update completed, but Retromind could not refresh the game executable. The existing launch configuration was retained."));
            }

            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = StoreInstallPathHelper.ToStoredPath(
                installed.InstallPath,
                _currentSettings.PreferPortableLaunchPaths);
            item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = installed.Version;
            if (!isRepair)
                item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "false";
            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();
            AppendProcessLog(
                progressVm,
                isRepair
                    ? T("Epic.Repair.Success", "Repair completed.")
                    : T("Epic.Update.Success", "Update completed."));
            return true;
        }
        catch (OperationCanceledException)
        {
            progressVm.MarkCancelled(
                isRepair
                    ? T("Epic.Repair.Cancelled", "Repair cancelled by user.")
                    : T("Epic.Update.Cancelled", "Update cancelled by user."));
            AppendProcessLog(
                progressVm,
                isRepair
                    ? T(
                        "Epic.Repair.ResumeHint",
                        "Legendary can reuse already downloaded repair data when the repair is started again.")
                    : T(
                        "Epic.Update.ResumeHint",
                        "Legendary can reuse already downloaded update data when the update is started again."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] {(isRepair ? "Repair" : "Update")} failed: {ex.Message}");
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    isRepair
                        ? T("Epic.Repair.FailedFormat", "Repair failed: {0}")
                        : T("Epic.Update.FailedFormat", "Update failed: {0}"),
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
