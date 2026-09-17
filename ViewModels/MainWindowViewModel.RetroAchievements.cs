using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Models;
using Retromind.Services.GameSystems;
using Retromind.Services.RetroAchievements;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private bool CanIdentifyNodeWithRetroAchievements(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        var gameSystemId = GameSystemResolver.ResolveForNode(targetNode, RootItems);
        return !_libraryLoadFailed &&
               _currentSettings.RetroAchievements?.Enabled == true &&
               HasConfiguredRetroAchievementsIdentity() &&
               targetNode != null &&
               RetroAchievementsConsoleCatalog.TryGetConsoleId(gameSystemId, out _);
    }

    private async Task IdentifyNodeWithRetroAchievementsAsync(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        if (!CanIdentifyNodeWithRetroAchievements(targetNode) ||
            targetNode == null ||
            CurrentWindow is not { } owner)
        {
            return;
        }

        if (!await EnsureRetroAchievementsAccountAvailableAsync(owner))
            return;

        if (SelectedNode != null &&
            targetNode.Id == SelectedNode.Id &&
            !ReferenceEquals(targetNode, SelectedNode))
        {
            targetNode = SelectedNode;
        }

        var candidates = new List<RetroAchievementsBulkIdentificationCandidate>();
        CollectRetroAchievementsCandidates(targetNode, candidates);
        await RunRetroAchievementsBulkIdentificationAsync(
            owner,
            candidates,
            saveResultsImmediately: true);
    }

    private async Task IdentifyImportedRomsWithRetroAchievementsAsync(
        Window owner,
        MediaNode targetNode,
        IReadOnlyList<MediaItem> importedItems)
    {
        var gameSystemId = GameSystemResolver.ResolveForNode(targetNode, RootItems);
        if (_currentSettings.RetroAchievements?.Enabled != true ||
            !RetroAchievementsConsoleCatalog.TryGetConsoleId(gameSystemId, out _) ||
            importedItems.Count == 0)
        {
            return;
        }

        if (!await EnsureRetroAchievementsAccountAvailableAsync(owner))
            return;

        var candidates = new List<RetroAchievementsBulkIdentificationCandidate>(importedItems.Count);
        foreach (var item in importedItems)
        {
            candidates.Add(CreateRetroAchievementsCandidate(item, targetNode));
        }

        await RunRetroAchievementsBulkIdentificationAsync(
            owner,
            candidates,
            saveResultsImmediately: false);
    }

    private bool HasConfiguredRetroAchievementsIdentity()
    {
        var settings = _currentSettings.RetroAchievements;
        return !string.IsNullOrWhiteSpace(settings?.UserUlid) ||
               !string.IsNullOrWhiteSpace(settings?.Username);
    }

    private async Task<bool> EnsureRetroAchievementsAccountAvailableAsync(Window owner)
    {
        if (HasConfiguredRetroAchievementsIdentity())
        {
            try
            {
                var apiKey = await _retroAchievementsAccountService.GetApiKeyAsync();
                if (!string.IsNullOrWhiteSpace(apiKey))
                    return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[RetroAchievements] Could not check the configured account: {ex.Message}");
            }
        }

        await ShowInfoDialog(
            owner,
            T(
                "RetroAchievements_AccountRequired",
                "Configure a RetroAchievements account and Web API key in the settings before starting identification."));
        return false;
    }

    private void CollectRetroAchievementsCandidates(
        MediaNode node,
        ICollection<RetroAchievementsBulkIdentificationCandidate> candidates)
    {
        foreach (var item in node.Items)
            candidates.Add(CreateRetroAchievementsCandidate(item, node));

        foreach (var child in node.Children)
            CollectRetroAchievementsCandidates(child, candidates);
    }

    private RetroAchievementsBulkIdentificationCandidate CreateRetroAchievementsCandidate(
        MediaItem item,
        MediaNode parentNode) =>
        new(
            item,
            GameSystemResolver.ResolveForItem(item, parentNode, RootItems),
            item.GetPrimaryLaunchPath());

    private async Task<RetroAchievementsBulkIdentificationResult?> RunRetroAchievementsBulkIdentificationAsync(
        Window owner,
        IReadOnlyList<RetroAchievementsBulkIdentificationCandidate> candidates,
        bool saveResultsImmediately)
    {
        var logViewModel = new ProcessLogViewModel(
            T("RetroAchievements_BulkTitle", "RetroAchievements mass identification"));
        var logView = new ProcessLogView { DataContext = logViewModel };
        var dialogTask = logView.ShowDialog(owner);
        logViewModel.EnableCancel();
        logViewModel.AppendLine(string.Format(
            T("RetroAchievements_BulkStartingFormat", "Checking {0:N0} games..."),
            candidates.Count));

        var progress = new Progress<RetroAchievementsBulkIdentificationProgress>(itemProgress =>
            logViewModel.AppendLine(FormatRetroAchievementsBulkProgress(itemProgress)));

        try
        {
            var result = await _retroAchievementsBulkIdentificationService.IdentifyAsync(
                candidates,
                progress,
                logViewModel.Token);

            var changed = ApplyRetroAchievementsBulkIdentities(result);

            if (changed && saveResultsImmediately)
            {
                _libraryTracker.MarkDirty();
                var saved = await SaveData();
                if (!saved)
                {
                    logViewModel.AppendLine(T(
                        "RetroAchievements_BulkSaveFailed",
                        "The identified games were changed in memory, but the library could not be saved."));
                }
            }

            logViewModel.AppendLine(string.Format(
                T(
                    "RetroAchievements_BulkSummaryFormat",
                    "Finished: {0:N0} identified, {1:N0} without a match, {2:N0} skipped, {3:N0} failed."),
                result.IdentifiedCount,
                result.NoMatchCount,
                result.SkippedCount,
                result.FailedCount));

            if (result.IsCancelled)
            {
                logViewModel.MarkCancelled(T(
                    "RetroAchievements_BulkCancelled",
                    "Identification was canceled. Completed matches were retained."));
            }
            else
            {
                logViewModel.MarkFinished();
            }

            await dialogTask;
            return result;
        }
        catch (Exception ex)
        {
            logViewModel.AppendLine(string.Format(
                T("RetroAchievements_BulkUnexpectedFailureFormat", "Identification failed unexpectedly: {0}"),
                ex.Message));
            logViewModel.MarkFinished();
        }

        await dialogTask;
        return null;
    }

    private static bool ApplyRetroAchievementsBulkIdentities(
        RetroAchievementsBulkIdentificationResult result)
    {
        var changed = false;
        foreach (var itemResult in result.Items)
        {
            if (itemResult.Outcome != RetroAchievementsBulkIdentificationOutcome.Identified ||
                itemResult.Identity == null)
            {
                continue;
            }

            itemResult.Item.RetroAchievementsGame = itemResult.Identity;
            changed = true;
        }

        return changed;
    }

    private static string FormatRetroAchievementsBulkProgress(
        RetroAchievementsBulkIdentificationProgress progress)
    {
        var result = progress.ItemResult;
        var outcomeText = result.Outcome switch
        {
            RetroAchievementsBulkIdentificationOutcome.Identified => string.Format(
                T("RetroAchievements_BulkIdentifiedFormat", "Identified as {0}."),
                result.Identity?.Title ?? result.Item.Title),
            RetroAchievementsBulkIdentificationOutcome.NoMatch =>
                T("RetroAchievements_BulkNoMatch", "No matching game found."),
            RetroAchievementsBulkIdentificationOutcome.SkippedAlreadyIdentified =>
                T("RetroAchievements_BulkSkippedExisting", "Skipped: already identified."),
            RetroAchievementsBulkIdentificationOutcome.SkippedMissingSystem =>
                T("RetroAchievements_BulkSkippedSystem", "Skipped: no game system assigned."),
            RetroAchievementsBulkIdentificationOutcome.SkippedMissingFile =>
                T("RetroAchievements_BulkSkippedFile", "Skipped: no launch file assigned."),
            RetroAchievementsBulkIdentificationOutcome.Failed => string.Format(
                T("RetroAchievements_BulkFailedFormat", "Failed: {0}"),
                result.ErrorMessage ?? string.Empty),
            _ => string.Empty
        };

        return string.Format(
            T("RetroAchievements_BulkProgressFormat", "[{0:N0}/{1:N0}] {2}: {3}"),
            progress.CompletedCount,
            progress.TotalCount,
            result.Item.Title,
            outcomeText);
    }
}
