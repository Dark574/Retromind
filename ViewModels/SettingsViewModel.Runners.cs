using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Text.Json;
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
            Kind = DetectRunnerKindFromPath(path),
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
        if (IsRemovingRunnerVersion || SelectedRunnerVersion == null)
            return false;

        if (SelectedRunnerVersion.UsedByGames <= 0)
            return true;

        return !string.IsNullOrWhiteSpace(SelectedRunnerReplacement?.Id);
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
                if (!TryResolveManagedRunnerDirectory(removed.Path, out var runnerDirectory))
                {
                    RunnerVersionStatusText = T(
                        "Settings_RunnerVersionRemoveInvalidPath",
                        "The managed runner path is invalid. Nothing was removed.");
                    return;
                }

                await Task.Run(() =>
                {
                    if (Directory.Exists(runnerDirectory))
                        Directory.Delete(runnerDirectory, recursive: true);
                });
            }

            // Remap emulator-level defaults
            foreach (var emulator in Emulators)
            {
                if (string.Equals(emulator.DefaultRunnerVersionId, removedId, StringComparison.Ordinal))
                    emulator.DefaultRunnerVersionId = replacementId;
            }

            // Remap item-level overrides
            var libraryChanged = false;
            if (_rootNodes.Count > 0)
            {
                foreach (var root in _rootNodes)
                {
                    if (RemapRunnerVersionRecursive(root, removedId, replacementId))
                        libraryChanged = true;
                }
            }

            if (libraryChanged)
                LibraryModified = true;

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

    private static bool TryResolveManagedRunnerDirectory(string path, out string directory)
    {
        directory = string.Empty;
        if (!AppPaths.TryResolveDataPathInsideRoot(path, out var candidate))
            return false;

        var managedRoot = Path.GetFullPath(Path.Combine(AppPaths.DataRoot, "Emulators", "ProtonVersions"));
        var parentDirectory = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(parentDirectory, managedRoot, StringComparison.Ordinal))
            return false;

        directory = candidate;
        return true;
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
            var releases = await FetchGeProtonReleasesAsync();

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
            var relativePath = await DownloadAndInstallGeReleaseAsync(selected);

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

    private static async Task<List<GeProtonReleaseOption>> FetchGeProtonReleasesAsync()
    {
        var result = new List<GeProtonReleaseOption>();

        for (var page = 1; page <= GeProtonMaxPages; page++)
        {
            var url = $"{GeProtonReleasesApiUrl}?per_page={GeProtonPerPage}&page={page}";
            using var response = await GitHubHttpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            if (json.RootElement.ValueKind != JsonValueKind.Array)
                break;

            var releaseCountOnPage = json.RootElement.GetArrayLength();
            if (releaseCountOnPage == 0)
                break;

            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (!TryGetStringProperty(release, "tag_name", out var tagName))
                    continue;

                if (release.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                    continue;

                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    if (!TryGetStringProperty(asset, "name", out var assetName))
                        continue;

                    if (!assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!TryGetStringProperty(asset, "browser_download_url", out var downloadUrl))
                        continue;

                    if (!assetName.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
                        && !assetName.Contains("arm", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(new GeProtonReleaseOption(tagName, assetName, downloadUrl));
                        break; // One download asset per release is enough.
                    }
                }

                if (result.Count >= GeProtonMaxItems)
                    return result;
            }

            if (releaseCountOnPage < GeProtonPerPage)
                break;
        }

        return result;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<string> DownloadAndInstallGeReleaseAsync(GeProtonReleaseOption release)
    {
        if (release == null)
            throw new ArgumentNullException(nameof(release));

        var baseRelativePath = Path.Combine("Emulators", "ProtonVersions");
        var baseAbsolutePath = AppPaths.ResolveDataPath(baseRelativePath);
        Directory.CreateDirectory(baseAbsolutePath);

        var tempArchivePath = Path.Combine(Path.GetTempPath(), $"retromind_ge_{Guid.NewGuid():N}.tar.gz");

        try
        {
            await using (var remote = await GitHubHttpClient.GetStreamAsync(release.DownloadUrl).ConfigureAwait(false))
            await using (var local = File.Create(tempArchivePath))
            {
                await remote.CopyToAsync(local).ConfigureAwait(false);
            }

            var rootFolder = DetectArchiveRootFolderName(tempArchivePath);
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                rootFolder = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(release.AssetName));
            }

            rootFolder = SanitizeFolderName(rootFolder);
            if (string.IsNullOrWhiteSpace(rootFolder))
                throw new InvalidOperationException("Unable to determine installation folder name.");

            var targetDir = Path.Combine(baseAbsolutePath, rootFolder);
            var relativeInstalledPath = NormalizeRelativePath(Path.Combine(baseRelativePath, rootFolder));

            if (Directory.Exists(targetDir))
                return relativeInstalledPath;

            var stagingDir = Path.Combine(baseAbsolutePath, $".tmp_ge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            try
            {
                await using var archiveStream = File.OpenRead(tempArchivePath);
                await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzipStream, stagingDir, overwriteFiles: false);

                var expectedRoot = Path.Combine(stagingDir, rootFolder);
                if (Directory.Exists(expectedRoot))
                {
                    Directory.Move(expectedRoot, targetDir);
                }
                else
                {
                    var extractedDirs = Directory.GetDirectories(stagingDir);
                    if (extractedDirs.Length == 1)
                    {
                        Directory.Move(extractedDirs[0], targetDir);
                    }
                    else
                    {
                        Directory.CreateDirectory(targetDir);
                        MoveDirectoryContents(stagingDir, targetDir);
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, recursive: true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }

            return relativeInstalledPath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchivePath))
                    File.Delete(tempArchivePath);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static string DetectArchiveRootFolderName(string tarGzPath)
    {
        if (string.IsNullOrWhiteSpace(tarGzPath) || !File.Exists(tarGzPath))
            return string.Empty;

        using var fileStream = File.OpenRead(tarGzPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream, leaveOpen: false);

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) != null)
        {
            var name = entry.Name?.Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var firstSegment = name.Split(new[] { '/', '\\' }, 2)[0];
            if (!string.IsNullOrWhiteSpace(firstSegment))
                return firstSegment;
        }

        return string.Empty;
    }

    private static void MoveDirectoryContents(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
            return;

        Directory.CreateDirectory(destinationDir);

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(dir));
            if (Directory.Exists(target))
                throw new IOException($"Target directory already exists: {target}");

            Directory.Move(dir, target);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(file));
            if (File.Exists(target))
                throw new IOException($"Target file already exists: {target}");

            File.Move(file, target);
        }
    }

    private static string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            result = result.Replace(c.ToString(), string.Empty, StringComparison.Ordinal);

        return result.Trim();
    }

    private static string NormalizeRelativePath(string path)
        => (path ?? string.Empty).Replace('\\', '/');

    private static RunnerVersionKind DetectRunnerKindFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RunnerVersionKind.Proton;

        var trimmed = path.Trim();
        var normalized = trimmed.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("/wine") || lower.Contains("wine64"))
            return RunnerVersionKind.Wine;

        if (lower.Contains("proton"))
            return RunnerVersionKind.Proton;

        try
        {
            var candidateDir = Directory.Exists(trimmed)
                ? trimmed
                : (File.Exists(trimmed) ? Path.GetDirectoryName(trimmed) : null);

            if (!string.IsNullOrWhiteSpace(candidateDir))
            {
                var wineBin = Path.Combine(candidateDir, "bin", "wine");
                var wineBin64 = Path.Combine(candidateDir, "bin", "wine64");
                if (File.Exists(wineBin) || File.Exists(wineBin64))
                    return RunnerVersionKind.Wine;

                var protonScript = Path.Combine(candidateDir, "proton");
                var protonFixes = Path.Combine(candidateDir, "protonfixes");
                if (File.Exists(protonScript) || Directory.Exists(protonFixes))
                    return RunnerVersionKind.Proton;
            }
        }
        catch
        {
            // best-effort detection
        }

        return RunnerVersionKind.Proton;
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

        foreach (var row in RunnerVersions.Where(r => !string.Equals(r.Id, SelectedRunnerVersion.Id, StringComparison.Ordinal)))
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
