using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

internal sealed partial class DesktopGogWorkflow
{
    internal bool CanReinstallGogMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || item == null)
            return false;

        return GogMediaItemStateHelper.IsInstalled(item) &&
               GogMediaItemStateHelper.IsRetromindManagedInstallation(item);
    }

    internal bool CanUpdateGogMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || item == null)
            return false;

        return ShouldOfferGogUpdateForItem(item);
    }

    internal bool CanUninstallGogMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || item == null)
            return false;

        return GogMediaItemStateHelper.CanUninstall(item);
    }

    internal async Task ReinstallGogMediaAsync(MediaItem? item)
    {
        if (!CanReinstallGogMedia(item) || item == null)
            return;

        if (IsLaunchInProgress)
            return;

        IsLaunchInProgress = true;
        try
        {
            await InstallGogItemAsync(item, operation: GogInstallOperation.Reinstall);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] ReinstallGogMedia failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    internal async Task UpdateGogMediaAsync(MediaItem? item)
    {
        if (!CanUpdateGogMedia(item) || item == null)
            return;

        if (IsLaunchInProgress)
            return;

        IsLaunchInProgress = true;
        try
        {
            await InstallGogItemAsync(item, operation: GogInstallOperation.Update);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] UpdateGogMedia failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    internal async Task<bool> RunGogInstallFromEditorAsync(
        MediaItem item,
        Window owner,
        bool requireAvailableUpdate)
    {
        if (IsLaunchInProgress ||
            GogMediaItemStateHelper.TryGetGameId(item) == null ||
            (requireAvailableUpdate && !GogMediaItemStateHelper.HasUpdateAvailable(item)))
        {
            return false;
        }

        IsLaunchInProgress = true;
        try
        {
            var operation = requireAvailableUpdate
                ? GogInstallOperation.Update
                : GogMediaItemStateHelper.IsInstalled(item)
                    ? GogInstallOperation.Reinstall
                    : GogInstallOperation.Install;
            return await InstallGogItemAsync(
                item,
                owner,
                operation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] GOG install from media editor failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    internal async Task UninstallGogMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner)
            return;

        _ = await RunGogUninstallAsync(item, owner);
    }

    internal async Task<bool> RunGogUninstallAsync(MediaItem item, Window owner)
    {
        if (!CanUninstallGogMedia(item))
            return false;

        if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) &&
            GogInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var installPath) &&
            IsInstallDirectoryUsedByAnotherItem(installPath, item))
        {
            await ShowInfoDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Uninstall.SharedInstallDirectoryFormat",
                        "The game folder is also used by another library item and will not be deleted:\n{0}"),
                    installPath));
            return false;
        }

        var preserveSharedPrefix = IsPrefixUsedByAnotherItem(item.PrefixPath, item);
        var confirmMessage = preserveSharedPrefix
            ? string.Format(
                T(
                    "Gog.Uninstall.ConfirmSharedPrefixFormat",
                    "Uninstall \"{0}\"?\n\nThe game files will be deleted. Its Wine/Proton prefix is also used by another item and will be preserved.\n\nThis action cannot be undone."),
                item.Title)
            : string.Format(
                Strings.Gog_Uninstall_ConfirmMessage,
                item.Title);

        var confirmed = await ShowConfirmDialog(owner, confirmMessage);
        if (!confirmed)
            return false;

        IsLaunchInProgress = true;
        try
        {
            await _gogInstallService.UninstallGogGameAsync(
                item,
                deletePrefix: !preserveSharedPrefix);

            // after successfull deinstall: reload the library
            Workspace.MarkLibraryDirty();
            var saved = await SaveData();
            NotifyPlayAvailabilityChanged();

            if (!saved)
                return true; // Confirms the physical uninstall; the central notice explains the pending save.

            await ShowInfoDialog(
                owner,
                preserveSharedPrefix
                    ? T(
                        "Gog.Uninstall.SuccessSharedPrefix",
                        "Game uninstalled successfully. The shared Wine/Proton prefix was preserved.")
                    : Strings.Gog_Uninstall_Success);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] UninstallGogMedia failed: {ex.Message}");
            await ShowInfoDialog(owner,
                string.Format(Strings.Gog_Uninstall_FailedWithMessage, ex.Message));
            return false;
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }
}
