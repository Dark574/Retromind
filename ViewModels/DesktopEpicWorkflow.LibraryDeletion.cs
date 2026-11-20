using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;

namespace Retromind.ViewModels;

internal sealed partial class DesktopEpicWorkflow
{
    internal async Task<(bool CanDelete, string? PreservedDirectory)> PrepareLibraryItemDeletionAsync(
        MediaItem item, Window owner)
    {
        string? preservedEpicInstallDirectory;
        var preserveSharedEpicPrefix = IsPrefixUsedByAnotherItem(item.PrefixPath, item);
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
            return (false, null);
        }

        var confirmMessage = string.Format(
                CultureInfo.CurrentCulture,
                preserveSharedEpicPrefix
                    ? T(
                        "Epic.Delete.ManagedSharedPrefixConfirmFormat",
                        "Delete \"{0}\" from the library?\n\nThe Retromind-managed Epic Games installation will be unregistered from Legendary and its managed game files will be deleted. Unrecognized files such as mods or saves will remain in the game folder. Its Wine/Proton prefix is used by another item and will be preserved.\n\nThis action cannot be undone.")
                    : T(
                        "Epic.Delete.ManagedConfirmFormat",
                        "Delete \"{0}\" from the library?\n\nThe Retromind-managed Epic Games installation will be unregistered from Legendary, its managed game files and its Wine/Proton prefix will be deleted. Unrecognized files such as mods or saves will remain in the game folder.\n\nThis action cannot be undone."),
                item.Title);
        if (!await ShowConfirmDialog(owner, confirmMessage)) return (false, null);

        if (!await EnsureEpicSignInAsync(owner))
            return (false, null);

        try
        {
            EpicUninstallResult uninstallResult;
            bool stateSaved;
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                (uninstallResult, stateSaved) = await UninstallEpicAndPersistAsync(
                    _epicInstallService, item,
                    deletePrefix: !preserveSharedEpicPrefix, PersistEpicUninstallStateAsync);
                preservedEpicInstallDirectory = uninstallResult.PreservedInstallDirectory;
            }
            // SaveData already owns persistence-error notification and keeps the model dirty.
            if (!stateSaved)
                return (false, null);
            if (uninstallResult.CleanupFailed || uninstallResult.CleanupCancelled)
            {
                await ShowInfoDialog(owner, T("Epic.Delete.CleanupIncomplete",
                    "The game was uninstalled. The library item was kept because follow-up cleanup did not complete. Its remaining game-folder and prefix paths are retained for manual cleanup."));
                return (false, null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Delete-time uninstall failed: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Epic.Delete.UninstallFailedFormat",
                        "The Epic Games installation could not be removed, so the library item was kept:\n\n{0}"),
                    BuildShortErrorDetail(ex)));
            return (false, null);
        }
        return (true, preservedEpicInstallDirectory);
    }

    internal async Task ShowPreservedInstallDirectoryAsync(Window owner, string? preservedEpicInstallDirectory)
    {
        if (!string.IsNullOrWhiteSpace(preservedEpicInstallDirectory))
        {
            await ShowInfoDialog(
                owner,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Epic.Uninstall.RemainingFilesPreservedFormat",
                        "The game folder still contains files or could not be removed safely. It was preserved in:\n{0}"),
                    preservedEpicInstallDirectory));
        }
    }
}
