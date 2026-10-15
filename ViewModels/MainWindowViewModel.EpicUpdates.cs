using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool CanCheckEpicUpdates(MediaItem? item) =>
        !IsLaunchInProgress &&
        EpicMediaItemStateHelper.CanCheckUpdates(item) &&
        !EpicMediaItemStateHelper.HasUpdateAvailable(item);

    private bool CanUpdateEpicMedia(MediaItem? item) =>
        !IsLaunchInProgress && EpicMediaItemStateHelper.HasUpdateAvailable(item);

    private async Task CheckEpicUpdatesNowAsync(MediaItem? item)
    {
        if (!CanCheckEpicUpdates(item) || item == null || CurrentWindow is not { } owner)
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
        if (CurrentWindow is not { } owner)
            return false;

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
            T("Epic.Update.ProgressTitleFormat", "Epic update - {0}"),
            string.IsNullOrWhiteSpace(item.Title) ? "Epic Games" : item.Title);
        var progressVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressView = new ProcessLogView { DataContext = progressVm };
        await UiThreadHelper.InvokeAsync(() => progressView.Show(owner));
        progressVm.EnableCancel();

        try
        {
            AppendProcessLog(
                progressVm,
                T("Epic.Update.Starting", "Checking and applying the Epic Games update with Legendary..."));
            var installed = await _epicInstallService.InstallAsync(
                item,
                directoryAssessment.FullPath,
                line => AppendProcessLog(progressVm, line),
                progressVm.Token);

            if (!ApplyEpicExecutablePath(item, installed))
            {
                AppendProcessLog(
                    progressVm,
                    T(
                        "Epic.Update.ExecutableWarning",
                        "The update completed, but Retromind could not refresh the game executable. The existing launch configuration was retained."));
            }

            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = StoreInstallPathHelper.ToStoredPath(
                installed.InstallPath,
                _currentSettings.PreferPortableLaunchPaths);
            item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = installed.Version;
            item.CustomFields[CustomFieldKeyHelper.StoreUpdateAvailable] = "false";
            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();
            AppendProcessLog(progressVm, T("Epic.Update.Success", "Update completed."));
            return true;
        }
        catch (OperationCanceledException)
        {
            progressVm.MarkCancelled(T("Epic.Update.Cancelled", "Update cancelled by user."));
            AppendProcessLog(
                progressVm,
                T(
                    "Epic.Update.ResumeHint",
                    "Legendary can reuse already downloaded update data when the update is started again."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Update failed: {ex.Message}");
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Update.FailedFormat", "Update failed: {0}"),
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
