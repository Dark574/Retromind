using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;
using Retromind.Services.Stores.Gog;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task<EpicLaunchPreparation?> PrepareEpicLaunchAsync(MediaItem item, Window owner)
    {
        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            return null;

        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                return await _epicLaunchService.PrepareAsync(appName);
            }
        }
        catch (Exception firstException)
        {
            Debug.WriteLine($"[Epic] Initial launch preparation failed: {firstException.Message}");
        }

        if (!await EnsureEpicSignInAsync(owner))
            return null;

        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                return await _epicLaunchService.PrepareAsync(appName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Launch preparation failed after sign-in: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Epic.Launch.PreparationFailedFormat",
                        "Epic Games launch data could not be prepared: {0}"),
                    BuildShortErrorDetail(ex)));
            return null;
        }
    }

    private static Dictionary<string, string>? MergeEpicLaunchEnvironment(
        IReadOnlyDictionary<string, string>? effectiveEnvironment,
        IReadOnlyDictionary<string, string> epicEnvironment)
    {
        if ((effectiveEnvironment?.Count ?? 0) == 0 && epicEnvironment.Count == 0)
            return null;

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (effectiveEnvironment != null)
        {
            foreach (var entry in effectiveEnvironment)
                merged[entry.Key] = entry.Value;
        }

        foreach (var entry in epicEnvironment)
            merged[entry.Key] = entry.Value;
        return merged;
    }

    private Task<bool> InstallStoreItemAsync(MediaItem item)
    {
        return EpicMediaItemStateHelper.TryGetGameId(item) != null
            ? InstallEpicItemAsync(item)
            : InstallGogItemAsync(item);
    }

    private async Task<bool> InstallEpicItemAsync(MediaItem item)
    {
        if (CurrentWindow is not { } owner)
            return false;

        var appName = EpicMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(appName))
            return false;
        if (!await EnsureEpicSignInAsync(owner))
            return false;

        var inheritedEmulator = FindInheritedEmulator(item);
        var preferredRunnerId = GogLaunchConfigurationHelper.ResolvePreferredRunnerVersionId(
            item,
            inheritedEmulator);
        var dialogVm = new EpicInstallDialogViewModel(
            item.Title,
            ResolveDefaultEpicInstallPath(item, appName),
            _currentSettings.RunnerVersions,
            preferredRunnerId);
        dialogVm.RequestBrowseInstallPath += () => BrowseFolderForEpicInstallAsync(owner);

        var dialog = new EpicInstallDialogView { DataContext = dialogVm };
        var accepted = await dialog.ShowDialog<bool>(owner);
        if (!accepted || dialogVm.Result == null)
            return false;

        var request = dialogVm.Result;
        if (!ValidateEpicRunner(request.Runner, out var runnerExecutable, out var runnerError))
        {
            await ShowInfoDialog(owner, runnerError);
            return false;
        }

        var directoryAssessment = _epicInstallService.AssessDirectory(request.InstallPath, item);
        if (!directoryAssessment.IsAllowed)
        {
            await ShowInfoDialog(owner, BuildEpicDirectoryError(directoryAssessment));
            return false;
        }

        LegendaryInstallEstimate? estimate = null;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                estimate = await _epicInstallService.GetInstallEstimateAsync(appName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Install-size estimate failed: {ex.Message}");
            if (!await ShowConfirmDialog(
                    owner,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Install.SpaceEstimateFailedFormat",
                            "Epic Games did not provide a usable installation size for this title. Retromind cannot check the required free space in advance. Legendary will still perform its own exact check before downloading.\n\nContinue installation?"))))
            {
                return false;
            }
        }

        if (estimate != null && !await ConfirmEpicInstallSpaceAsync(owner, request.InstallPath, estimate))
            return false;

        try
        {
            // Claim the empty target before Legendary writes data. If the user cancels,
            // the matching marker makes the partial directory eligible for resume.
            _epicInstallService.WriteMarker(directoryAssessment.FullPath, item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Could not prepare install marker: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Epic.Install.MarkerRequiredFormat",
                        "Retromind could not safely prepare the install directory: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }

        var progressTitle = string.Format(
            CultureInfo.CurrentCulture,
            T("Epic.Install.ProgressTitleFormat", "Epic install - {0}"),
            string.IsNullOrWhiteSpace(item.Title) ? "Epic Games" : item.Title);
        var progressVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressView = new ProcessLogView { DataContext = progressVm };
        await UiThreadHelper.InvokeAsync(() => progressView.Show(owner));
        progressVm.EnableCancel();

        try
        {
            AppendProcessLog(
                progressVm,
                T("Epic.Install.Starting", "Preparing Legendary download and installation..."));
            var installed = await _epicInstallService.InstallAsync(
                appName,
                directoryAssessment.FullPath,
                line => AppendProcessLog(progressVm, line),
                progressVm.Token);

            if (!ApplyEpicLaunchConfiguration(
                    item,
                    appName,
                    installed,
                    request.Runner,
                    runnerExecutable))
            {
                AppendProcessLog(
                    progressVm,
                    T(
                        "Epic.Install.LaunchConfigurationFailed",
                        "Installation completed, but Retromind could not configure the game executable."));
                return false;
            }

            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = StoreInstallPathHelper.ToStoredPath(
                installed.InstallPath,
                _currentSettings.PreferPortableLaunchPaths);
            item.CustomFields[CustomFieldKeyHelper.StoreInstallPlatform] = "windows";
            item.CustomFields[CustomFieldKeyHelper.StoreInstallRunnerVersionId] = request.Runner.Id;
            if (string.IsNullOrWhiteSpace(installed.Version))
                item.CustomFields.Remove(CustomFieldKeyHelper.StoreInstalledVersion);
            else
                item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = installed.Version;

            try
            {
                _epicInstallService.WriteMarker(installed.InstallPath, item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Epic] Could not write install marker: {ex.Message}");
                AppendProcessLog(
                    progressVm,
                    T(
                        "Epic.Install.MarkerWarning",
                        "Installation succeeded, but Retromind could not mark the install directory as managed."));
            }

            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();
            AppendProcessLog(progressVm, T("Epic.Install.Success", "Installation completed."));
            return true;
        }
        catch (OperationCanceledException)
        {
            progressVm.MarkCancelled(T("Epic.Install.Cancelled", "Installation cancelled by user."));
            AppendProcessLog(
                progressVm,
                T(
                    "Epic.Install.ResumeHint",
                    "Legendary can reuse already downloaded data when installation is started again."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Epic] Installation failed: {ex.Message}");
            AppendProcessLog(
                progressVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("Epic.Install.FailedFormat", "Installation failed: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }
        finally
        {
            progressVm.MarkFinished();
            UiThreadHelper.Post(() => progressVm.IsRunning = false);
        }
    }

    private string ResolveDefaultEpicInstallPath(MediaItem item, string appName)
    {
        if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedPath) &&
            StoreInstallPathHelper.TryResolveStoredPath(storedPath, out var resolvedPath))
        {
            return resolvedPath;
        }

        var safeTitle = PathHelper.SanitizePathSegment(item.Title);
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = $"epic_{PathHelper.SanitizePathSegment(appName)}";
        return Path.Combine(AppPaths.LibraryRoot, "Games", "Epic", safeTitle);
    }

    private async Task<string?> BrowseFolderForEpicInstallAsync(Window owner)
    {
        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Epic.Install.PickPathTitle", "Select Epic Games install folder"),
            AllowMultiple = false
        });
        return result is { Count: > 0 } ? result[0].Path.LocalPath : null;
    }

    private static bool ValidateEpicRunner(
        EpicInstallDialogViewModel.RunnerOption runner,
        out string runnerExecutable,
        out string error)
    {
        runnerExecutable = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
                               runner.Kind,
                               runner.Path) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(runnerExecutable))
        {
            error = $"The selected runner '{runner.Name}' is unavailable or incomplete.";
            return false;
        }

        if (runner.Kind == RunnerVersionKind.Proton &&
            string.IsNullOrWhiteSpace(EnvironmentPathHelper.TryFindExecutableInCurrentPath("umu-run")))
        {
            error = "The selected Proton runner requires umu-run in PATH.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task<bool> ConfirmEpicInstallSpaceAsync(
        Window owner,
        string installPath,
        LegendaryInstallEstimate estimate)
    {
        var assessment = _epicInstallService.AssessSpace(installPath, estimate);
        switch (assessment.Status)
        {
            case EpicInstallSpaceStatus.EstimatedInstallMayNotFit:
                return await ShowConfirmDialog(
                    owner,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Install.SpaceEstimateWarningFormat",
                            "The complete Epic Games manifest may need more free space than is currently available.\n\nEstimated installation: {0}\nEstimated requirement including 1 GB safety reserve: {1}\nAvailable: {2}\nChecked filesystem: {3}\n\nThis estimate can be too high when a game uses optional install groups. Legendary performs its own exact check before downloading. Continue anyway?"),
                        StoreDiskSpaceHelper.FormatByteSize(assessment.EstimatedInstallBytes.GetValueOrDefault()),
                        StoreDiskSpaceHelper.FormatByteSize(assessment.RequiredBytes.GetValueOrDefault()),
                        StoreDiskSpaceHelper.FormatByteSize(assessment.AvailableBytes.GetValueOrDefault()),
                        assessment.CheckedPath));

            case EpicInstallSpaceStatus.FreeSpaceUnavailable:
            case EpicInstallSpaceStatus.EstimateUnavailable:
                return await ShowConfirmDialog(
                    owner,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T(
                            "Epic.Install.SpaceUnavailableFormat",
                            "Retromind could not complete the free-space check for:\n{0}\n\nLegendary will still perform its own exact check before downloading. Continue?"),
                        assessment.CheckedPath));

            default:
                return true;
        }
    }

    private string BuildEpicDirectoryError(EpicInstallDirectoryAssessment assessment)
    {
        return assessment.Status switch
        {
            EpicInstallDirectoryStatus.DangerousPath => string.Format(
                CultureInfo.CurrentCulture,
                T("Epic.Install.DirectoryUnsafeFormat", "The selected install directory is unsafe:\n{0}"),
                assessment.FullPath),
            EpicInstallDirectoryStatus.SymbolicLink => string.Format(
                CultureInfo.CurrentCulture,
                T("Epic.Install.DirectoryLinkFormat", "The install path contains a symbolic link and cannot be used safely:\n{0}"),
                assessment.FullPath),
            EpicInstallDirectoryStatus.UnownedDirectory => string.Format(
                CultureInfo.CurrentCulture,
                T("Epic.Install.DirectoryNotEmptyFormat", "The selected directory is not empty and does not belong to this Retromind game:\n{0}\n\nChoose a new or empty directory."),
                assessment.FullPath),
            _ => string.Format(
                CultureInfo.CurrentCulture,
                T("Epic.Install.DirectoryInvalidFormat", "The selected install directory cannot be used:\n{0}"),
                assessment.FullPath)
        };
    }

    private bool ApplyEpicLaunchConfiguration(
        MediaItem item,
        string appName,
        LegendaryInstalledGame installed,
        EpicInstallDialogViewModel.RunnerOption runner,
        string runnerExecutable)
    {
        if (!ApplyEpicExecutablePath(item, installed))
            return false;

        item.LauncherPath = runner.Kind == RunnerVersionKind.Proton
            ? "umu-run"
            : PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(runnerExecutable)
              ?? runnerExecutable;
        item.LauncherArgs = "{file}";
        // The item has an explicit launcher, so the normal emulator inheritance path
        // is intentionally bypassed at launch time. Persist the selected runner here.
        item.RunnerVersionId = runner.Id;
        if (string.IsNullOrWhiteSpace(item.PrefixPath))
            item.PrefixPath = EpicMediaItemStateHelper.BuildDefaultPrefixPath(appName, item.Title);
        item.MediaType = MediaType.Emulator;
        item.EmulatorId = null;
        return true;
    }

    private bool ApplyEpicExecutablePath(MediaItem item, LegendaryInstalledGame installed)
    {
        if (string.IsNullOrWhiteSpace(installed.Executable))
            return false;

        var executablePath = Path.GetFullPath(Path.Combine(
            installed.InstallPath,
            installed.Executable.Replace('\\', Path.DirectorySeparatorChar)));
        var installRoot = Path.GetFullPath(installed.InstallPath);
        var installRootWithSeparator = Path.EndsInDirectorySeparator(installRoot)
            ? installRoot
            : installRoot + Path.DirectorySeparatorChar;
        if (!FileSystemPathIdentity.Equals(executablePath, installRoot) &&
            !executablePath.StartsWith(installRootWithSeparator, FileSystemPathIdentity.Comparison))
        {
            return false;
        }

        if (!File.Exists(executablePath))
            return false;

        var storedExecutable = executablePath;
        var fileKind = MediaFileKind.Absolute;
        if (_currentSettings.PreferPortableLaunchPaths &&
            PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(executablePath, out var relativeExecutable))
        {
            storedExecutable = relativeExecutable;
            fileKind = MediaFileKind.LibraryRelative;
        }

        item.Files =
        [
            new MediaFileRef
            {
                Kind = fileKind,
                Path = storedExecutable,
                Index = 1
            }
        ];
        item.WorkingDirectory = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(
            Path.GetDirectoryName(executablePath));
        return true;
    }
}
