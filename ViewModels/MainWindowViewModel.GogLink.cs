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
using Retromind.Services.Stores.Gog;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task<bool> LinkMediaItemToGogAsync(MediaItem item, Window owner)
    {
        var parentNode = FindParentNode(RootItems, item);
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

        var previousFields = item.CustomFields;
        var previousGogDlcInstallations = item.GogDlcInstallations;
        var updatedFields = BuildChangedStoreFields(
            previousFields,
            GogProviderId,
            selectedGame.StoreGameId);

        if (legacyInstallRoot != null)
        {
            updatedFields[CustomFieldKeyHelper.StoreInstallPath] = GogInstallPathHelper.ToStoredPath(
                legacyInstallRoot,
                _currentSettings.PreferPortableLaunchPaths);
        }

        item.CustomFields = updatedFields;
        item.GogDlcInstallations = null;
        var inferredPlatform = GogLaunchConfigurationHelper.InferLegacyInstallPlatform(item);
        if (inferredPlatform.HasValue)
        {
            updatedFields[CustomFieldKeyHelper.StoreInstallPlatform] = inferredPlatform.Value == GogInstallPlatform.Windows
                ? "windows"
                : "linux";
        }

        var markerWritten = false;
        try
        {
            if (legacyInstallRoot != null)
            {
                GogInstallDirectorySafety.WriteMarker(legacyInstallRoot, item);
                markerWritten = true;
            }

            _libraryTracker.MarkDirty();
            if (!await SaveData())
                throw new IOException("The linked GOG identity could not be saved.");
        }
        catch (Exception ex)
        {
            item.CustomFields = previousFields;
            item.GogDlcInstallations = previousGogDlcInstallations;
            _libraryTracker.MarkDirty();

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

    private async Task<bool> CanChangeStoreLinkAsync(
        MediaItem item,
        string targetProviderId,
        string targetDisplayName,
        Window owner)
    {
        var currentProviderId = StoreProviderBadgeHelper.GetProviderId(item);
        if (currentProviderId != null &&
            !string.Equals(currentProviderId, StoreProviderIds.Gog, StringComparison.Ordinal) &&
            !string.Equals(currentProviderId, StoreProviderIds.Epic, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(currentProviderId, targetProviderId, StringComparison.Ordinal) &&
            StoreGameIdentityHelper.HasStoredIdentity(item))
        {
            return false;
        }

        // The owned install path is the decisive safety boundary even if an
        // incomplete legacy entry happens to be missing its store game ID.
        var hasManagedInstallation =
            item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var installPath) &&
            !string.IsNullOrWhiteSpace(installPath);
        if (!hasManagedInstallation)
            return true;

        await ShowInfoDialog(
            owner,
            string.Format(
                T(
                    "Store.Link.UninstallFirstFormat",
                    "Uninstall the current {0} installation before switching this item to {1}. The library item, media and metadata remain available after uninstallation."),
                StoreProviderBadgeHelper.GetToolTip(currentProviderId) ?? currentProviderId,
                targetDisplayName));
        return false;
    }

    private static Dictionary<string, string> BuildChangedStoreFields(
        IReadOnlyDictionary<string, string> previousFields,
        string providerId,
        string gameId)
    {
        var updatedFields = previousFields
            .Where(entry => !CustomFieldKeyHelper.IsInternal(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        updatedFields[CustomFieldKeyHelper.StoreProviderId] = providerId;
        updatedFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        return updatedFields;
    }

    private Dictionary<string, int> BuildStoreUsageCountExcludingItem(
        string providerId,
        MediaItem excludedItem)
    {
        var usageByGameId = BuildStoreUsageCountByGameId(providerId);
        if (!StoreGameIdentityHelper.TryGetIdentity(
                excludedItem,
                out var itemProviderId,
                out var gameId) ||
            !string.Equals(itemProviderId, providerId, StringComparison.OrdinalIgnoreCase) ||
            !usageByGameId.TryGetValue(gameId, out var count))
        {
            return usageByGameId;
        }

        if (count > 1)
            usageByGameId[gameId] = count - 1;
        else
            usageByGameId.Remove(gameId);

        return usageByGameId;
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

    private bool IsInstallDirectoryUsedByAnotherItem(string installRoot, MediaItem linkedItem)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        foreach (var root in RootItems)
        {
            if (IsInstallDirectoryUsedByAnotherItemRecursive(root, fullRoot, linkedItem))
                return true;
        }

        return false;
    }

    private bool IsPrefixUsedByAnotherItem(string? prefixPath, MediaItem linkedItem)
    {
        if (string.IsNullOrWhiteSpace(prefixPath))
            return false;

        string fullPrefixPath;
        try
        {
            fullPrefixPath = ResolvePrefixOwnershipRoot(prefixPath);
        }
        catch
        {
            return false;
        }

        foreach (var root in RootItems)
        {
            if (IsPrefixUsedByAnotherItemRecursive(root, fullPrefixPath, linkedItem))
                return true;
        }

        return false;
    }

    private static bool IsPrefixUsedByAnotherItemRecursive(
        MediaNode node,
        string fullPrefixPath,
        MediaItem linkedItem)
    {
        foreach (var candidate in node.Items)
        {
            if (ReferenceEquals(candidate, linkedItem) || string.IsNullOrWhiteSpace(candidate.PrefixPath))
                continue;

            try
            {
                var candidatePrefixPath = ResolvePrefixOwnershipRoot(candidate.PrefixPath);
                if (FileSystemPathIdentity.Equals(candidatePrefixPath, fullPrefixPath))
                    return true;
            }
            catch
            {
                // Invalid prefix paths cannot establish shared ownership.
            }
        }

        return node.Children.Any(child =>
            IsPrefixUsedByAnotherItemRecursive(child, fullPrefixPath, linkedItem));
    }

    private static string ResolvePrefixOwnershipRoot(string prefixPath)
    {
        var resolvedPath = PrefixPathHelper.ResolveAbsolutePrefixPath(prefixPath, AppPaths.LibraryRoot);
        if (!PrefixPathHelper.IsPfxPath(resolvedPath))
            return resolvedPath;

        return Directory.GetParent(resolvedPath)?.FullName ?? resolvedPath;
    }

    private static bool IsInstallDirectoryUsedByAnotherItemRecursive(
        MediaNode node,
        string fullRoot,
        MediaItem linkedItem)
    {
        foreach (var candidate in node.Items)
        {
            if (ReferenceEquals(candidate, linkedItem))
                continue;

            if (candidate.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedInstallPath) &&
                GogInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var candidateInstallPath))
            {
                try
                {
                    if (FileSystemPathIdentity.Equals(
                            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateInstallPath)),
                            fullRoot))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Invalid stored paths cannot establish shared ownership.
                }
            }

            foreach (var file in candidate.Files)
            {
                var filePath = file.Kind == MediaFileKind.LibraryRelative
                    ? AppPaths.ResolveDataPathInsideRootOrEmpty(file.Path)
                    : file.Path;
                if (string.IsNullOrWhiteSpace(filePath))
                    continue;

                try
                {
                    var fullFilePath = Path.GetFullPath(filePath);
                    var relative = Path.GetRelativePath(fullRoot, fullFilePath);
                    if (relative != ".." &&
                        !relative.StartsWith($"..{Path.DirectorySeparatorChar}", FileSystemPathIdentity.Comparison) &&
                        !Path.IsPathRooted(relative))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Invalid launch paths cannot establish shared ownership.
                }
            }
        }

        return node.Children.Any(child =>
            IsInstallDirectoryUsedByAnotherItemRecursive(child, fullRoot, linkedItem));
    }
}
