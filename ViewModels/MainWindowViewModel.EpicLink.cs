using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool CanLinkMediaItemToEpic(MediaItem? item)
    {
        var providerId = StoreProviderBadgeHelper.GetProviderId(item);
        return !IsLaunchInProgress &&
               !IsMultiSelectModeActive &&
               item != null &&
               (providerId == null ||
                string.Equals(providerId, StoreProviderIds.Gog, StringComparison.Ordinal) ||
                (string.Equals(providerId, StoreProviderIds.Epic, StringComparison.Ordinal) &&
                 !StoreGameIdentityHelper.HasStoredIdentity(item)));
    }

    private async Task LinkMediaItemToEpicFromContextAsync(MediaItem? item)
    {
        if (!CanLinkMediaItemToEpic(item) || item == null || CurrentWindow is not { } owner)
            return;

        _ = await LinkMediaItemToEpicAsync(item, owner);
    }

    private async Task<bool> LinkMediaItemToEpicAsync(MediaItem item, Window owner)
    {
        var parentNode = FindParentNode(RootItems, item);
        var previousProviderId = StoreProviderBadgeHelper.GetProviderId(item);
        if (!await CanChangeStoreLinkAsync(
                item,
                StoreProviderIds.Epic,
                EpicDisplayName,
                owner))
        {
            return false;
        }

        if (!await EnsureEpicSignInAsync(owner))
            return false;

        IReadOnlyList<StoreGameRecord> ownedGames;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                ownedGames = await _epicStoreLibraryProvider.GetOwnedGamesAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Failed to load owned games for linking: {ex.Message}");
            await ShowInfoDialog(
                owner,
                T("Epic.LibraryLoadFailed", "The Epic Games library could not be loaded."));
            return false;
        }

        if (ownedGames.Count == 0)
        {
            await ShowInfoDialog(
                owner,
                T("Epic.LibraryEmpty", "No Epic Games titles were found."));
            return false;
        }

        var existingIdsInTargetNode = parentNode?.Items
            .Where(candidate => !ReferenceEquals(candidate, item))
            .Select(candidate => StoreGameIdentityHelper.TryGetIdentity(
                candidate,
                out var providerId,
                out var gameId) &&
                string.Equals(providerId, StoreProviderIds.Epic, StringComparison.OrdinalIgnoreCase)
                    ? gameId
                    : null)
            .Where(static gameId => !string.IsNullOrWhiteSpace(gameId))
            .Select(static gameId => gameId!)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        var pickerVm = new GogPickerDialogViewModel(
            ownedGames,
            existingIdsInTargetNode,
            BuildStoreUsageCountExcludingItem(StoreProviderIds.Epic, item),
            singleSelection: true,
            initialSearchText: item.Title,
            providerId: StoreProviderIds.Epic,
            providerDisplayName: EpicDisplayName);
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

        var isLegacyEpicLink = string.Equals(
            previousProviderId,
            StoreProviderIds.Epic,
            StringComparison.Ordinal);
        var confirmationText = previousProviderId != null && !isLegacyEpicLink
            ? string.Format(
                T(
                    "Store.Link.SwitchConfirmFormat",
                    "Change the store association from {0} to {1} title '{2}'?\n\nThe current files and launch settings remain unchanged. The Retromind library item, media and metadata are preserved."),
                StoreProviderBadgeHelper.GetToolTip(previousProviderId) ?? previousProviderId,
                EpicDisplayName,
                selectedGame.Title)
            : string.Format(
                T(
                    "Epic.Link.IdentityOnlyConfirmFormat",
                    "Link this item to Epic Games title '{0}'?\n\nThe current files and launch settings remain unchanged. The Retromind library item, media and metadata are preserved. Existing externally managed files remain external until the game is installed through Retromind."),
                selectedGame.Title);

        if (!await ShowConfirmDialog(owner, confirmationText))
            return false;

        var updatedFields = StoreLinkChangeService.BuildChangedFields(
            item.CustomFields,
            StoreProviderIds.Epic,
            selectedGame.StoreGameId);

        try
        {
            await StoreLinkChangeService.ApplyAndSaveAsync(
                item,
                updatedFields,
                SaveData,
                _libraryTracker.MarkDirty,
                "The linked Epic Games identity could not be saved.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Failed to link media item: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    T(
                        "Epic.Link.FailedFormat",
                        "The item could not be linked to Epic Games: {0}"),
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
                Debug.WriteLine($"[Epic] Linked item presentation refresh failed: {ex.Message}");
            }
        }

        return true;
    }
}
