using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class SettingsViewModel
{
    private bool CanAddRunnerVersion()
        => !string.IsNullOrWhiteSpace(RunnerVersionPathInput);

    private void AddRunnerVersion()
    {
        var path = RunnerVersionPathInput?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalizedPath = PreferPortableLaunchPaths
            ? PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(path) ?? path
            : path;

        var name = string.IsNullOrWhiteSpace(RunnerVersionNameInput)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : RunnerVersionNameInput.Trim();

        var row = new RunnerVersionRow
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Kind = _runnerVersionService.DetectRunnerKind(path),
            SourceType = RunnerVersionSourceType.ExternalPath,
            Path = normalizedPath
        };

        RunnerVersions.Add(row);
        SelectedRunnerVersion = row;
        SortRunnerVersions();
        RunnerVersionNameInput = string.Empty;
        RunnerVersionPathInput = string.Empty;

        RecomputeRunnerUsageCounts();
        RebuildSelectedEmulatorRunnerVersionOptions();
        RebuildRunnerReplacementOptions();
    }

    private bool CanRemoveRunnerVersion()
    {
        if (IsRemovingRunnerVersion || IsReplacingRunnerVersionAssignments || SelectedRunnerVersion == null)
            return false;

        if (SelectedRunnerVersion.UsedByGames <= 0)
            return true;

        return !string.IsNullOrWhiteSpace(SelectedRunnerReplacement?.Id);
    }

    private bool CanReplaceRunnerVersionAssignments()
    {
        if (IsRemovingRunnerVersion || IsReplacingRunnerVersionAssignments)
            return false;

        var source = SelectedRunnerVersion;
        var replacement = SelectedRunnerReplacement;
        if (source == null || source.UsedByGames <= 0 || string.IsNullOrWhiteSpace(replacement?.Id))
            return false;

        return replacement.Kind == source.Kind &&
               !string.Equals(source.Id, replacement.Id, StringComparison.Ordinal);
    }

    private async Task ReplaceRunnerVersionAssignmentsAsync()
    {
        var source = SelectedRunnerVersion;
        var replacementOption = SelectedRunnerReplacement;
        if (source == null || replacementOption == null || !CanReplaceRunnerVersionAssignments())
            return;

        var replacement = RunnerVersions.FirstOrDefault(row =>
            string.Equals(row.Id, replacementOption.Id, StringComparison.Ordinal));
        if (replacement == null || replacement.Kind != source.Kind)
            return;

        var confirmation = RequestRunnerVersionReplacementConfirmation;
        if (confirmation == null || !await confirmation(source, replacement))
            return;

        IsReplacingRunnerVersionAssignments = true;
        RunnerVersionStatusText = string.Empty;

        var affectedGames = source.UsedByGames;

        try
        {
            RemapRunnerAssignmentsInWorkingState(source.Id, replacement.Id);

            // Apply only this explicit operation to the live settings. Other edits
            // in the detached settings working copy remain pending until Save.
            RemapEmulatorRunnerDefaults(_targetSettings.Emulators, source.Id, replacement.Id);

            RecomputeRunnerUsageCounts();
            RebuildSelectedEmulatorRunnerVersionOptions();
            RebuildRunnerReplacementOptions();

            var persistence = RequestRunnerVersionAssignmentPersistence;
            var persisted = persistence == null || await persistence();
            RunnerVersionStatusText = persisted
                ? string.Format(
                    T(
                        "Settings_RunnerVersionReplaceSuccessFormat",
                        "Affected games: {0}. Updated runner assignments from {1} to {2}. The previous runner remains installed."),
                    affectedGames,
                    source.Name,
                    replacement.Name)
                : T(
                    "Settings_RunnerVersionReplaceSaveFailed",
                    "Assignments were changed, but Retromind could not save them. Unsaved changes remain in memory.");
        }
        catch (Exception ex)
        {
            RunnerVersionStatusText = string.Format(
                T("Settings_RunnerVersionReplaceFailedFormat", "Could not replace runner assignments: {0}"),
                ex.Message);
        }
        finally
        {
            IsReplacingRunnerVersionAssignments = false;
        }
    }

    private async Task RemoveRunnerVersionAsync()
    {
        if (SelectedRunnerVersion == null)
            return;

        var removed = SelectedRunnerVersion;
        var removedId = removed.Id;
        var replacementId = SelectedRunnerReplacement?.Id;

        if (removed.UsedByGames > 0 && string.IsNullOrWhiteSpace(replacementId))
            return;

        var confirmation = RequestRunnerVersionRemovalConfirmation;
        if (confirmation == null || !await confirmation(removed))
            return;

        IsRemovingRunnerVersion = true;
        RunnerVersionStatusText = string.Empty;

        try
        {
            if (removed.SourceType == RunnerVersionSourceType.ManagedDownload)
            {
                if (!await _runnerVersionService.DeleteManagedRunnerAsync(removed.Path))
                {
                    RunnerVersionStatusText = T(
                        "Settings_RunnerVersionRemoveInvalidPath",
                        "The managed runner path is invalid. Nothing was removed.");
                    return;
                }
            }

            RemapRunnerAssignmentsInWorkingState(removedId, replacementId);

            RunnerVersions.Remove(removed);
            SelectedRunnerVersion = RunnerVersions.FirstOrDefault();

            RecomputeRunnerUsageCounts();
            RebuildSelectedEmulatorRunnerVersionOptions();
            RebuildRunnerReplacementOptions();

            await PersistRunnerRemovalAsync(removed, replacementId);
            RunnerVersionStatusText = removed.SourceType == RunnerVersionSourceType.ManagedDownload
                ? T("Settings_RunnerVersionRemoveManagedSuccess", "Runner files and configuration removed.")
                : T("Settings_RunnerVersionRemoveExternalSuccess", "Runner configuration removed. External files were kept.");
        }
        catch (Exception ex)
        {
            RunnerVersionStatusText = string.Format(
                T("Settings_RunnerVersionRemoveFailedFormat", "Could not remove runner: {0}"),
                ex.Message);
        }
        finally
        {
            IsRemovingRunnerVersion = false;
        }
    }

    private async Task BrowseRunnerVersionPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = RunnerVersionBrowseTitle,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return;

        RunnerVersionPathInput = result[0].Path.LocalPath;
    }

    private bool CanDownloadSelectedGeRelease()
        => !IsGeReleaseBusy && SelectedGeProtonRelease != null;

    private async Task RefreshGeReleasesAsync()
    {
        if (IsGeReleaseBusy)
            return;

        IsGeReleaseBusy = true;
        GeReleaseStatusText = T("Settings_GeProtonStatusLoading", "Loading release list...");

        try
        {
            var releases = await _runnerVersionService.GetGeProtonReleasesAsync();

            GeProtonReleases.Clear();
            foreach (var release in releases)
                GeProtonReleases.Add(release);

            SelectedGeProtonRelease = GeProtonReleases.FirstOrDefault();

            GeReleaseStatusText = releases.Count > 0
                ? string.Format(T("Settings_GeProtonStatusLoadedFormat", "Loaded {0} release(s)."), releases.Count)
                : T("Settings_GeProtonStatusNoReleases", "No downloadable GE-Proton release found.");
        }
        catch (Exception ex)
        {
            GeReleaseStatusText = string.Format(
                T("Settings_GeProtonStatusLoadFailedFormat", "Failed to load release list: {0}"),
                ex.Message);
        }
        finally
        {
            IsGeReleaseBusy = false;
        }
    }

    private async Task DownloadSelectedGeReleaseAsync()
    {
        var selected = SelectedGeProtonRelease;
        if (!CanDownloadSelectedGeRelease() || selected == null)
            return;

        IsGeReleaseBusy = true;
        GeReleaseStatusText = string.Format(
            T("Settings_GeProtonStatusDownloadingFormat", "Downloading {0} ..."),
            selected.TagName);

        try
        {
            var relativePath = await _runnerVersionService.DownloadAndInstallGeProtonAsync(selected);

            var existing = RunnerVersions.FirstOrDefault(r =>
                string.Equals(r.Path, relativePath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.SourceType = RunnerVersionSourceType.ManagedDownload;
                existing.Kind = RunnerVersionKind.Proton;
                existing.ReleaseTag = selected.TagName;
                SelectedRunnerVersion = existing;
            }
            else
            {
                var folderName = Path.GetFileName(relativePath);
                var row = new RunnerVersionRow
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = folderName,
                    Kind = RunnerVersionKind.Proton,
                    SourceType = RunnerVersionSourceType.ManagedDownload,
                    Path = relativePath,
                    ReleaseTag = selected.TagName
                };

                RunnerVersions.Add(row);
                SelectedRunnerVersion = row;
            }

            SortRunnerVersions();
            RecomputeRunnerUsageCounts();
            RebuildSelectedEmulatorRunnerVersionOptions();
            RebuildRunnerReplacementOptions();

            await PersistDownloadedRunnerRegistrationAsync(relativePath);

            GeReleaseStatusText = string.Format(
                T("Settings_GeProtonStatusInstalledFormat", "Installed: {0}"),
                relativePath);
        }
        catch (Exception ex)
        {
            GeReleaseStatusText = string.Format(
                T("Settings_GeProtonStatusInstallFailedFormat", "Installation failed: {0}"),
                ex.Message);
        }
        finally
        {
            IsGeReleaseBusy = false;
        }
    }

    /// <summary>
    /// A managed runner download is a completed external action, so retain its
    /// registration even when the surrounding settings dialog is later closed
    /// without saving unrelated edits.
    /// </summary>
    private async Task PersistDownloadedRunnerRegistrationAsync(string relativePath)
    {
        var runner = RunnerVersions.FirstOrDefault(r =>
            string.Equals(r.Path, relativePath, StringComparison.OrdinalIgnoreCase));
        if (runner == null)
            throw new InvalidOperationException("The downloaded runner could not be registered.");

        var runnerConfig = runner.ToModel();
        UpsertRunnerVersion(_targetSettings.RunnerVersions, runnerConfig);

        // Save a disk snapshot so unsaved changes elsewhere in the settings
        // dialog do not become persistent merely because a runner was downloaded.
        var persistedSettings = await _settingsService.LoadAsync().ConfigureAwait(false);
        UpsertRunnerVersion(persistedSettings.RunnerVersions, runnerConfig);
        await _settingsService.SaveAsync(persistedSettings).ConfigureAwait(false);
    }

    private async Task PersistRunnerRemovalAsync(RunnerVersionRow removed, string? replacementId)
    {
        RemoveRunnerVersion(_targetSettings.RunnerVersions, removed);
        RemapEmulatorRunnerDefaults(_targetSettings.Emulators, removed.Id, replacementId);

        // Save a disk snapshot so this completed removal is retained without
        // persisting unrelated edits that are still open in the dialog.
        var persistedSettings = await _settingsService.LoadAsync().ConfigureAwait(false);
        RemoveRunnerVersion(persistedSettings.RunnerVersions, removed);
        RemapEmulatorRunnerDefaults(persistedSettings.Emulators, removed.Id, replacementId);
        await _settingsService.SaveAsync(persistedSettings).ConfigureAwait(false);
    }

    private static void RemapEmulatorRunnerDefaults(
        IEnumerable<EmulatorConfig> emulators,
        string removedId,
        string? replacementId)
    {
        foreach (var emulator in emulators)
        {
            if (string.Equals(emulator.DefaultRunnerVersionId, removedId, StringComparison.Ordinal))
                emulator.DefaultRunnerVersionId = replacementId;
        }
    }

    private void RemapRunnerAssignmentsInWorkingState(string sourceId, string? replacementId)
    {
        RemapEmulatorRunnerDefaults(Emulators, sourceId, replacementId);

        var libraryChanged = false;
        foreach (var root in _rootNodes)
        {
            if (RemapRunnerVersionRecursive(root, sourceId, replacementId))
                libraryChanged = true;
        }

        if (libraryChanged)
            LibraryModified = true;
    }

    private static void UpsertRunnerVersion(List<RunnerVersionConfig> runners, RunnerVersionConfig runner)
    {
        var existingIndex = runners.FindIndex(existing =>
            string.Equals(existing.Id, runner.Id, StringComparison.Ordinal) ||
            string.Equals(existing.Path, runner.Path, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            runners[existingIndex] = runner;
        else
            runners.Add(runner);
    }

    private static void RemoveRunnerVersion(List<RunnerVersionConfig> runners, RunnerVersionRow removed)
    {
        runners.RemoveAll(runner =>
            string.Equals(runner.Id, removed.Id, StringComparison.Ordinal) ||
            string.Equals(runner.Path, removed.Path, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildSelectedEmulatorRunnerVersionOptions()
    {
        SelectedEmulatorRunnerVersionOptions.Clear();
        SelectedEmulatorRunnerVersionOptions.Add(new RunnerVersionSelectionOption(
            id: null,
            name: Strings.NodeSettings_ModeNone));

        var selected = SelectedEmulator;
        var intent = InferEffectiveRunnerIntent(selected);

        foreach (var row in OrderRunnerRowsForIntent(intent))
        {
            var suffix = row.Kind == RunnerVersionKind.Wine ? "Wine" : "Proton";
            SelectedEmulatorRunnerVersionOptions.Add(new RunnerVersionSelectionOption(
                id: row.Id,
                name: $"{row.Name} ({suffix})",
                kind: row.Kind));
        }

        SyncSelectedEmulatorRunnerVersionSelection();
    }

    private void SyncSelectedEmulatorRunnerVersionSelection()
    {
        var defaultId = SelectedEmulator?.DefaultRunnerVersionId;
        SelectedEmulatorRunnerVersionId = SelectedEmulatorRunnerVersionOptions.Any(o =>
            string.Equals(o.Id, defaultId, StringComparison.Ordinal))
            ? defaultId
            : null;
    }

    private void RebuildRunnerReplacementOptions()
    {
        RunnerReplacementOptions.Clear();

        if (SelectedRunnerVersion == null)
        {
            SelectedRunnerReplacement = null;
            return;
        }

        foreach (var row in RunnerVersions
                     .Where(r => r.Kind == SelectedRunnerVersion.Kind &&
                                 !string.Equals(r.Id, SelectedRunnerVersion.Id, StringComparison.Ordinal))
                     .OrderBy(r => r.Name, MediaSortHelper.NaturalStringComparer))
        {
            var suffix = row.Kind == RunnerVersionKind.Wine ? "Wine" : "Proton";
            RunnerReplacementOptions.Add(new RunnerVersionSelectionOption(
                id: row.Id,
                name: $"{row.Name} ({suffix})",
                kind: row.Kind));
        }

        var preferredKind = SelectedRunnerVersion.Kind;
        SelectedRunnerReplacement = RunnerReplacementOptions.FirstOrDefault(o => o.Kind == preferredKind)
            ?? RunnerReplacementOptions.FirstOrDefault();
    }

    private IEnumerable<RunnerVersionRow> OrderRunnerRowsForIntent(EmulatorConfig.RunnerIntent intent)
    {
        RunnerVersionKind? preferredKind = intent switch
        {
            EmulatorConfig.RunnerIntent.UmuProton => RunnerVersionKind.Proton,
            EmulatorConfig.RunnerIntent.Wine => RunnerVersionKind.Wine,
            _ => null
        };

        return RunnerVersions
            .OrderBy(r => preferredKind.HasValue && r.Kind == preferredKind.Value ? 0 : 1)
            .ThenBy(r => r.Name, MediaSortHelper.NaturalStringComparer);
    }

    private void SortRunnerVersions()
    {
        var orderedRows = RunnerVersions
            .OrderBy(row => row.Name, MediaSortHelper.NaturalStringComparer)
            .ThenBy(row => row.Path, MediaSortHelper.NaturalStringComparer)
            .ToList();

        for (var targetIndex = 0; targetIndex < orderedRows.Count; targetIndex++)
        {
            var currentIndex = RunnerVersions.IndexOf(orderedRows[targetIndex]);
            if (currentIndex != targetIndex)
                RunnerVersions.Move(currentIndex, targetIndex);
        }
    }

    private static EmulatorConfig.RunnerIntent InferEffectiveRunnerIntent(EmulatorConfig? emulator)
    {
        if (emulator == null)
            return EmulatorConfig.RunnerIntent.Auto;

        if (emulator.RunnerType != EmulatorConfig.RunnerIntent.Auto)
            return emulator.RunnerType;

        var executable = emulator.Path ?? string.Empty;
        if (executable.Contains("umu", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("proton", StringComparison.OrdinalIgnoreCase) ||
            emulator.EnvironmentOverrides.Keys.Any(k => string.Equals(k, "PROTONPATH", StringComparison.OrdinalIgnoreCase)))
        {
            return EmulatorConfig.RunnerIntent.UmuProton;
        }

        if (executable.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
            emulator.EnvironmentOverrides.Keys.Any(k => string.Equals(k, "WINE", StringComparison.OrdinalIgnoreCase)))
        {
            return EmulatorConfig.RunnerIntent.Wine;
        }

        return EmulatorConfig.RunnerIntent.Generic;
    }

    private void RecomputeRunnerUsageCounts()
    {
        _runnerUsageById.Clear();

        if (_rootNodes.Count > 0)
        {
            foreach (var root in _rootNodes)
                CountRunnerUsageRecursive(root, inheritedDefaultEmulatorId: null);
        }

        foreach (var row in RunnerVersions)
        {
            row.UsedByGames = _runnerUsageById.TryGetValue(row.Id, out var count)
                ? count
                : 0;
        }

        OnPropertyChanged(nameof(IsRunnerReplacementVisible));
        OnPropertyChanged(nameof(RunnerReplacementHint));
        RemoveRunnerVersionCommand.NotifyCanExecuteChanged();
        ReplaceRunnerVersionAssignmentsCommand.NotifyCanExecuteChanged();
    }

    private void CountRunnerUsageRecursive(MediaNode node, string? inheritedDefaultEmulatorId)
    {
        var effectiveDefaultEmulatorId = !string.IsNullOrWhiteSpace(node.DefaultEmulatorId)
            ? node.DefaultEmulatorId
            : inheritedDefaultEmulatorId;

        foreach (var item in node.Items)
        {
            var runnerId = ResolveEffectiveRunnerVersionId(item, effectiveDefaultEmulatorId);
            if (string.IsNullOrWhiteSpace(runnerId))
                continue;

            _runnerUsageById[runnerId] = _runnerUsageById.TryGetValue(runnerId, out var count)
                ? count + 1
                : 1;
        }

        foreach (var child in node.Children)
            CountRunnerUsageRecursive(child, effectiveDefaultEmulatorId);
    }

    private string? ResolveEffectiveRunnerVersionId(MediaItem item, string? inheritedDefaultEmulatorId)
    {
        if (!string.IsNullOrWhiteSpace(item.RunnerVersionId))
            return item.RunnerVersionId;

        if (item.MediaType != MediaType.Emulator)
            return null;

        EmulatorConfig? emulator = null;
        if (!string.IsNullOrWhiteSpace(item.EmulatorId))
        {
            emulator = Emulators.FirstOrDefault(e => string.Equals(e.Id, item.EmulatorId, StringComparison.Ordinal));
        }
        else if (string.IsNullOrWhiteSpace(item.LauncherPath) && !string.IsNullOrWhiteSpace(inheritedDefaultEmulatorId))
        {
            emulator = Emulators.FirstOrDefault(e => string.Equals(e.Id, inheritedDefaultEmulatorId, StringComparison.Ordinal));
        }

        return emulator?.DefaultRunnerVersionId;
    }

    private static bool RemapRunnerVersionRecursive(MediaNode node, string removedId, string? replacementId)
    {
        var changed = false;

        foreach (var item in node.Items)
        {
            if (string.Equals(item.RunnerVersionId, removedId, StringComparison.Ordinal))
            {
                item.RunnerVersionId = replacementId;
                changed = true;
            }
        }

        foreach (var child in node.Children)
        {
            if (RemapRunnerVersionRecursive(child, removedId, replacementId))
                changed = true;
        }

        return changed;
    }
}
