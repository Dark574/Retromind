using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Views;

namespace Retromind.ViewModels;

internal abstract class DesktopStoreWorkflow(DesktopStoreWorkspace workspace)
{
    protected DesktopStoreWorkspace Workspace { get; } = workspace;
    protected ObservableCollection<MediaNode> RootItems => Workspace.GetRootItems();
    protected Window? CurrentWindow => Workspace.GetWindow();
    protected IStorageProvider? StorageProvider => Workspace.GetStorageProvider();
    protected bool IsMultiSelectModeActive => Workspace.GetMultiSelectionActive();
    protected bool IsLaunchInProgress
    {
        get => Workspace.GetOperationInProgress();
        set => Workspace.SetOperationInProgress(value);
    }

    protected Task<bool> SaveData() => Workspace.SaveLibraryAsync();
    protected void NotifyPlayAvailabilityChanged() => Workspace.RefreshActions();
    protected Task RefreshItemPresentationAsync(MediaNode node) => Workspace.RefreshItemAsync(node);

    internal static bool ShouldOfferInstallForItem(MediaItem? item) =>
        GogMediaItemStateHelper.ShouldOfferInstall(item) || EpicMediaItemStateHelper.ShouldOfferInstall(item);

    protected static string T(string key, string fallback) => ViewModelBase.T(key, fallback);
    protected static string OperationSavePendingMessage() => DesktopPersistenceCoordinator.OperationSavePendingMessage();
    protected static IDisposable BeginBusyCursor(Window owner) => new BusyCursorScope(owner);
    protected static Task<bool> ShowConfirmDialog(Window owner, string message) => DesktopDialogs.ShowConfirmAsync(owner, message);
    protected static Task ShowInfoDialog(Window owner, string message, bool showCopyButton = true) =>
        DesktopDialogs.ShowInfoAsync(owner, message, showCopyButton);

    protected Dictionary<string, int> BuildStoreUsageCountByGameId(string providerId)
    {
        var usageByGameId = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var root in RootItems)
            CollectStoreUsageRecursive(root, providerId, usageByGameId);

        return usageByGameId;
    }

    private static void CollectStoreUsageRecursive(
        MediaNode node,
        string providerId,
        IDictionary<string, int> usageByGameId)
    {
        foreach (var item in node.Items)
        {
            if (!StoreGameIdentityHelper.TryGetIdentity(item, out var itemProviderId, out var storeGameId) ||
                !string.Equals(itemProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (usageByGameId.TryGetValue(storeGameId, out var existingCount))
                usageByGameId[storeGameId] = existingCount + 1;
            else
                usageByGameId[storeGameId] = 1;
        }

        foreach (var child in node.Children)
            CollectStoreUsageRecursive(child, providerId, usageByGameId);
    }

    protected async Task<bool> CanChangeStoreLinkAsync(
        MediaItem item,
        string targetProviderId,
        string targetDisplayName,
        Window owner)
    {
        var blockReason = StoreLinkChangeService.GetBlockReason(item, targetProviderId);
        if (blockReason != StoreLinkChangeBlockReason.ManagedInstallation)
            return blockReason == StoreLinkChangeBlockReason.None;

        var currentProviderId = StoreProviderBadgeHelper.GetProviderId(item);
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

    protected Dictionary<string, int> BuildStoreUsageCountExcludingItem(
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

    protected bool IsInstallDirectoryUsedByAnotherItem(string installRoot, MediaItem linkedItem)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        foreach (var root in RootItems)
        {
            if (IsInstallDirectoryUsedByAnotherItemRecursive(root, fullRoot, linkedItem))
                return true;
        }

        return false;
    }

    protected bool IsPrefixUsedByAnotherItem(string? prefixPath, MediaItem linkedItem)
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
                StoreInstallPathHelper.TryResolveStoredPath(storedInstallPath, out var candidateInstallPath))
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

    protected static string BuildShortErrorDetail(Exception ex) =>
        BuildShortErrorDetail(ex.Message);

    protected static string BuildShortErrorDetail(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Unknown error";

        var compact = message.Trim().Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return compact.Length <= 260 ? compact : compact[..260] + "...";
    }

    protected static void AppendProcessLog(ProcessLogViewModel logVm, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        UiThreadHelper.Post(() => logVm.AppendLine(line));
    }
}
