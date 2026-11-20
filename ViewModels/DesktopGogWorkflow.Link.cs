using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Gog;
using Retromind.Views;

namespace Retromind.ViewModels;

internal sealed partial class DesktopGogWorkflow
{
    internal bool CanLinkMediaItemToGog(MediaItem? item)
    {
        var providerId = StoreProviderBadgeHelper.GetProviderId(item);
        return !IsLaunchInProgress &&
               !IsMultiSelectModeActive &&
               item != null &&
               (providerId == null ||
                string.Equals(providerId, StoreProviderIds.Epic, StringComparison.Ordinal));
    }

    internal async Task LinkMediaItemToGogFromContextAsync(MediaItem? item)
    {
        if (!CanLinkMediaItemToGog(item) || item == null || CurrentWindow is not { } owner)
            return;

        _ = await LinkMediaItemToGogAsync(item, owner);
    }

    internal async Task<bool> LinkMediaItemToGogAsync(MediaItem item, Window owner)
    {
        var parentNode = Workspace.FindParentNode(item);
        var previousProviderId = StoreProviderBadgeHelper.GetProviderId(item);
        if (!await CanChangeStoreLinkAsync(
                item,
                StoreProviderIds.Gog,
                GogDisplayName,
                owner))
        {
            return false;
        }

        if (!await EnsureGogSignInForInstallAsync(owner))
            return false;

        IReadOnlyList<StoreGameRecord> ownedGames;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                ownedGames = await _gogStoreLibraryProvider.GetOwnedGamesAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to load owned games for linking: {ex.Message}");
            await ShowInfoDialog(
                owner,
                T("Gog.LibraryLoadFailed", "GOG library could not be loaded."));
            return false;
        }

        if (ownedGames.Count == 0)
        {
            await ShowInfoDialog(owner, T("Gog.LibraryEmpty", "No GOG games found."));
            return false;
        }

        var existingIdsInTargetNode = parentNode?.Items
            .Where(candidate => !ReferenceEquals(candidate, item))
            .Select(GogMediaItemStateHelper.TryGetGameId)
            .Where(static gameId => !string.IsNullOrWhiteSpace(gameId))
            .Select(static gameId => gameId!)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        var pickerVm = new GogPickerDialogViewModel(
            ownedGames,
            existingIdsInTargetNode,
            BuildGogUsageCountByGameId(),
            singleSelection: true,
            initialSearchText: item.Title);
        var pickerDialog = new GogPickerDialogView { DataContext = pickerVm };

        StoreGameRecord? selectedGame;
        try
        {
            var accepted = false;
            pickerVm.RequestClose += result =>
            {
                accepted = result;
                pickerDialog.Close(result);
            };

            await pickerDialog.ShowDialog<bool>(owner);
            if (!accepted)
                return false;

            selectedGame = pickerVm.GetSelectedGames().SingleOrDefault();
        }
        finally
        {
            pickerVm.Dispose();
        }

        if (selectedGame == null)
            return false;

        string? legacyInstallRoot;
        using (BeginBusyCursor(owner))
        {
            legacyInstallRoot = await Task.Run(() =>
                FindAdoptableLegacyGogInstallRoot(item, selectedGame.StoreGameId));
        }

        if (legacyInstallRoot != null && IsInstallDirectoryUsedByAnotherItem(legacyInstallRoot, item))
        {
            Debug.WriteLine($"[GOG] Legacy install directory is shared by another item and will not be adopted: '{legacyInstallRoot}'.");
            legacyInstallRoot = null;
        }

        if (legacyInstallRoot != null)
        {
            var confirmed = await ShowConfirmDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Link.AdoptConfirmFormat",
                        "Retromind found an existing GOG offline installation for this item:\n{0}\n\nLink it to '{1}' and manage this game folder from now on? A later clean reinstall may permanently delete everything inside this folder; a GOG uninstall may also remove its separate Wine/Proton prefix. Media and item metadata are preserved."),
                    legacyInstallRoot,
                    selectedGame.Title));
            if (!confirmed)
                return false;
        }
        else if (previousProviderId != null)
        {
            var confirmed = await ShowConfirmDialog(
                owner,
                string.Format(
                    T(
                        "Store.Link.SwitchConfirmFormat",
                        "Change the store association from {0} to {1} title '{2}'?\n\nThe current files and launch settings remain unchanged. The Retromind library item, media and metadata are preserved."),
                    StoreProviderBadgeHelper.GetToolTip(previousProviderId) ?? previousProviderId,
                    GogDisplayName,
                    selectedGame.Title));
            if (!confirmed)
                return false;
        }
        else
        {
            var confirmed = await ShowConfirmDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Link.IdentityOnlyConfirmFormat",
                        "Retromind could not safely identify the existing game folder as a GOG offline installation for '{0}'.\n\nLink only the GOG identity? The current files and launch settings remain unchanged, but updates and uninstallation cannot be managed until the game is installed through Retromind."),
                    selectedGame.Title));
            if (!confirmed)
                return false;
        }

        var updatedFields = StoreLinkChangeService.BuildChangedFields(
            item.CustomFields,
            GogProviderId,
            selectedGame.StoreGameId);

        if (legacyInstallRoot != null)
        {
            updatedFields[CustomFieldKeyHelper.StoreInstallPath] = GogInstallPathHelper.ToStoredPath(
                legacyInstallRoot,
                Workspace.Settings.PreferPortableLaunchPaths);
        }

        var markerWritten = false;
        try
        {
            await StoreLinkChangeService.ApplyAndSaveAsync(
                item,
                updatedFields,
                SaveData,
                Workspace.MarkLibraryDirty,
                "The linked GOG identity could not be saved.",
                beforeSave: () =>
                {
                    var inferredPlatform = GogLaunchConfigurationHelper.InferLegacyInstallPlatform(item);
                    if (inferredPlatform.HasValue)
                    {
                        updatedFields[CustomFieldKeyHelper.StoreInstallPlatform] =
                            inferredPlatform.Value == GogInstallPlatform.Windows
                                ? "windows"
                                : "linux";
                    }

                    if (legacyInstallRoot == null)
                        return;

                    GogInstallDirectorySafety.WriteMarker(legacyInstallRoot, item);
                    markerWritten = true;
                });
        }
        catch (Exception ex)
        {
            if (markerWritten && legacyInstallRoot != null)
            {
                try
                {
                    File.Delete(Path.Combine(legacyInstallRoot, GogInstallDirectorySafety.MarkerFileName));
                }
                catch (Exception cleanupEx)
                {
                    Debug.WriteLine($"[GOG] Failed to remove rolled-back legacy marker: {cleanupEx.Message}");
                }
            }

            Debug.WriteLine($"[GOG] Failed to link media item: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    T("Gog.Link.FailedFormat", "The item could not be linked to GOG: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }

        NotifyPlayAvailabilityChanged();
        if (parentNode != null)
        {
            try
            {
                await RefreshItemPresentationAsync(parentNode);
            }
            catch (Exception ex)
            {
                // The association is already durably saved. A presentation refresh
                // failure must not roll back its marker or in-memory identity.
                Debug.WriteLine($"[GOG] Linked item presentation refresh failed: {ex.Message}");
            }
        }

        return true;
    }

    private static string? FindAdoptableLegacyGogInstallRoot(MediaItem item, string storeGameId)
    {
        var launchPath = item.GetPrimaryLaunchPath();
        if (string.IsNullOrWhiteSpace(launchPath))
            return null;

        string? currentDirectory;
        try
        {
            currentDirectory = Path.GetDirectoryName(Path.GetFullPath(launchPath));
        }
        catch
        {
            return null;
        }

        while (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            if (GogInstallDirectorySafety.CanAdoptLegacyInstall(currentDirectory, item, storeGameId))
                return currentDirectory;

            var parent = Directory.GetParent(currentDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || FileSystemPathIdentity.Equals(parent, currentDirectory))
                break;

            currentDirectory = parent;
        }

        return null;
    }

}
