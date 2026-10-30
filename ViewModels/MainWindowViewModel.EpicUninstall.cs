using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;
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
                    "Uninstall \"{0}\"?\n\nThe managed game files will be deleted and the installation will be unregistered from Legendary. Unrecognized files such as mods or saves will remain in the game folder. Its Wine/Proton prefix is also used by another item and will be preserved. The Retromind library item, media and metadata remain available.\n\nThis action cannot be undone.")
                : T(
                    "Epic.Uninstall.ConfirmFormat",
                    "Uninstall \"{0}\"?\n\nThe managed game files and its Wine/Proton prefix will be deleted, and the installation will be unregistered from Legendary. Unrecognized files such as mods or saves will remain in the game folder. The Retromind library item, media and metadata remain available.\n\nThis action cannot be undone."),
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
        var nonemptyDirectoryReported = 0;
        try
        {
            AppendProcessLog(
                progressVm,
                T(
                    "Epic.Uninstall.Starting",
                    "Uninstalling the Epic Games title and removing its Legendary registration..."));
            var (result, stateSaved) = await UninstallEpicAndPersistAsync(
                _epicInstallService, item, deletePrefix: !preserveSharedPrefix,
                PersistEpicUninstallStateAsync,
                line =>
                {
                    if (IsLegendaryNonemptyDirectoryMessage(line))
                        Interlocked.Exchange(ref nonemptyDirectoryReported, 1);
                    AppendProcessLog(progressVm, line);
                },
                progressVm.Token);

            if (!stateSaved)
            {
                AppendProcessLog(progressVm, T("Epic.Uninstall.StateSaveFailed",
                    "The game was uninstalled, but its updated library state could not be saved. The changes remain pending."));
                return false;
            }

            var directoryNotice = GetEpicUninstallDirectoryCleanupNotice(
                result, Volatile.Read(ref nonemptyDirectoryReported) != 0);
            if (directoryNotice != null)
                AppendProcessLog(progressVm, directoryNotice);

            AppendProcessLog(
                progressVm,
                result.CleanupCancelled
                    ? T("Epic.Uninstall.CleanupCancelled", "The game was uninstalled. Follow-up cleanup was cancelled; remaining paths were preserved.")
                    : result.CleanupFailed
                    ? T("Epic.Uninstall.CleanupFailed", "The game was uninstalled, but follow-up cleanup did not complete. Remaining paths were preserved.")
                    : result.PrefixPreserved
                    ? T(
                        "Epic.Uninstall.SuccessPrefixPreserved",
                        "Game uninstalled successfully. The Wine/Proton prefix was preserved.")
                    : T("Epic.Uninstall.Success", "Game uninstalled successfully."));
            if (!string.IsNullOrWhiteSpace(result.PreservedInstallDirectory))
            {
                AppendProcessLog(
                    progressVm,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Uninstall.RemainingFilesPreservedFormat",
                            "The game folder still contains files or could not be removed safely. It was preserved in:\n{0}"),
                        result.PreservedInstallDirectory));
            }
            if (result.PrefixPreserved && !string.IsNullOrWhiteSpace(item.PrefixPath))
                AppendProcessLog(progressVm, string.Format(CultureInfo.CurrentCulture,
                    T("Epic.Uninstall.PrefixPathPreservedFormat", "Preserved Wine/Proton prefix: {0}"), item.PrefixPath));

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

    internal static bool IsLegendaryNonemptyDirectoryMessage(string line) =>
        line.Contains("[LFS Utils] ERROR: Removing game directory failed", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("Directory not empty", StringComparison.OrdinalIgnoreCase);

    internal static string? GetEpicUninstallDirectoryCleanupNotice(
        EpicUninstallResult result, bool nonemptyDirectoryReported)
    {
        // Explain this backend message only after the actual final outcome is known. Keep raw CLI output
        // and the existing warnings for retained files, failed cleanup, cancellation and failed persistence.
        if (!nonemptyDirectoryReported || result.CleanupFailed || result.CleanupCancelled ||
            result.PreservedInstallDirectory != null)
            return null;

        return T("Epic.Uninstall.DirectoryCleanupCompleted",
            "Legendary initially could not remove the nonempty game folder. After follow-up cleanup, Retromind has confirmed that the folder is now completely removed.");
    }

    // Shared by standalone uninstall and item deletion. Once the service returns, the uninstall is confirmed;
    // persist it even when cleanup was cancelled. The cleanup token must not cancel this required save.
    internal static async Task<(EpicUninstallResult Result, bool StateSaved)> UninstallEpicAndPersistAsync(
        EpicInstallService service, MediaItem item, bool deletePrefix, Func<Task<bool>> persistState,
        Action<string>? outputReceived = null, CancellationToken ct = default)
    {
        var result = await service.UninstallAsync(item, deletePrefix, outputReceived, ct);
        var saved = await persistState();
        return (result, saved);
    }

    private async Task<bool> PersistEpicUninstallStateAsync()
    {
        _libraryTracker.MarkDirty();
        var saved = await SaveData();
        NotifyPlayAvailabilityChanged();
        return saved;
    }
}
