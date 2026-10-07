using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Gog;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private enum GogInstallOperation
    {
        Install,
        Reinstall,
        Update
    }

    private bool ShouldOfferInstallForItem(MediaItem? item)
        => GogMediaItemStateHelper.ShouldOfferInstall(item);

    private async Task<bool> InstallGogItemAsync(
        MediaItem item,
        Window? ownerOverride = null,
        GogInstallOperation operation = GogInstallOperation.Install)
    {
        var owner = ownerOverride ?? CurrentWindow;
        if (owner == null)
            return false;

        var installedDlcsToReapply = operation != GogInstallOperation.Install
            ? item.GogDlcInstallations?
                .Where(static state => !string.IsNullOrWhiteSpace(state.ProductId))
                .ToArray() ?? []
            : [];

        var storeGameId = GogMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return false;

        var signedIn = await EnsureGogSignInForInstallAsync(owner);
        if (!signedIn)
            return false;

        IReadOnlyList<GogInstallPlatform> availablePlatforms;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                availablePlatforms = await _gogInstallService.GetAvailableInstallerPlatformsAsync(storeGameId);
            }
        }
        catch (Exception ex) when (IsLikelyGogAuthIssue(ex))
        {
            Debug.WriteLine($"[GOG] Installer platform query auth issue: {ex.Message}");
            var reloginSucceeded = await EnsureGogSignInForInstallAsync(owner, forceInteractiveSignIn: true);
            if (!reloginSucceeded)
                return false;

            try
            {
                using (BeginBusyCursor(owner))
                {
                    await Task.Yield();
                    availablePlatforms = await _gogInstallService.GetAvailableInstallerPlatformsAsync(storeGameId);
                }
            }
            catch (Exception retryEx)
            {
                Debug.WriteLine($"[GOG] Failed to query installer platforms after re-login: {retryEx.Message}");
                await ShowInfoDialog(
                    owner,
                    string.Format(
                        T("Gog.Install.ResolveFailedFormat", "GOG installer metadata could not be loaded: {0}"),
                        BuildShortErrorDetail(retryEx)));
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to query installer platforms: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    T("Gog.Install.ResolveFailedFormat", "GOG installer metadata could not be loaded: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }

        if (availablePlatforms.Count == 0)
        {
            await ShowInfoDialog(
                owner,
                T("Gog.Install.NoInstallerAvailable", "No installable package is available for this GOG title."));
            return false;
        }

        var defaultInstallPath = ResolveDefaultGogInstallPath(item, storeGameId);
        var preferredPlatform = GetPreferredInstalledGogPlatform(item);
        var inheritedEmulator = FindInheritedEmulator(item);
        var preferredRunnerId = GogLaunchConfigurationHelper.ResolvePreferredRunnerVersionId(
            item,
            inheritedEmulator);
        var preferredWindowsInstallerPreference = GetPreferredInstalledWindowsInstallerPreference(item);
        var dialogVm = new GogInstallDialogViewModel(
            item.Title,
            defaultInstallPath,
            _currentSettings.RunnerVersions,
            availablePlatforms,
            preferredPlatform,
            preferredRunnerId,
            preferredWindowsInstallerPreference,
            isUpdate: operation == GogInstallOperation.Update,
            installedDlcsToReinstall: installedDlcsToReapply);
        dialogVm.RequestBrowseInstallPath += async () => await BrowseFolderForGogInstallAsync(owner);

        var dialog = new GogInstallDialogView { DataContext = dialogVm };
        var accepted = await dialog.ShowDialog<bool>(owner);
        if (!accepted || dialogVm.Result == null)
            return false;

        var installRequest = dialogVm.Result;
        if (operation == GogInstallOperation.Reinstall &&
            installRequest.CleanInstall &&
            installRequest.DlcProductIdsToReinstall != null)
        {
            var selectedDlcProductIds = installRequest.DlcProductIdsToReinstall
                .ToHashSet(StringComparer.Ordinal);
            installedDlcsToReapply = installedDlcsToReapply
                .Where(state => selectedDlcProductIds.Contains(state.ProductId.Trim()))
                .ToArray();
        }

        if (!await ValidateGogInstallRuntimeRequirementsAsync(owner, installRequest))
            return false;

        GogInstallerPackage? installerPackage;
        try
        {
            using (BeginBusyCursor(owner))
            {
                await Task.Yield();
                installerPackage = await _gogInstallService.ResolveInstallerPackageAsync(
                    storeGameId,
                    installRequest.Platform);
            }
        }
        catch (Exception ex) when (IsLikelyGogAuthIssue(ex))
        {
            Debug.WriteLine($"[GOG] Installer resolve auth issue: {ex.Message}");
            var reloginSucceeded = await EnsureGogSignInForInstallAsync(owner, forceInteractiveSignIn: true);
            if (!reloginSucceeded)
                return false;

            try
            {
                using (BeginBusyCursor(owner))
                {
                    await Task.Yield();
                    installerPackage = await _gogInstallService.ResolveInstallerPackageAsync(
                        storeGameId,
                        installRequest.Platform);
                }
            }
            catch (Exception retryEx)
            {
                Debug.WriteLine($"[GOG] Failed to resolve installer package after re-login: {retryEx.Message}");
                await ShowInfoDialog(
                    owner,
                    string.Format(
                        T("Gog.Install.ResolveFailedFormat", "GOG installer metadata could not be loaded: {0}"),
                        BuildShortErrorDetail(retryEx)));
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to resolve installer package: {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    T("Gog.Install.ResolveFailedFormat", "GOG installer metadata could not be loaded: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }

        if (installerPackage == null)
        {
            var platformName = installRequest.Platform == GogInstallPlatform.Windows
                ? T("Gog.Install.PlatformWindows", "Windows")
                : T("Gog.Install.PlatformLinux", "Linux");
            await ShowInfoDialog(
                owner,
                string.Format(
                    T("Gog.Install.NoInstallerForPlatformFormat", "No installer found for platform: {0}."),
                    platformName));
            return false;
        }

        var selectedInstallerPackage = installerPackage;

        if (!await PrepareGogInstallDirectoryAsync(owner, item, installRequest))
            return false;

        var platformFolder = installRequest.Platform == GogInstallPlatform.Windows ? "windows" : "linux";
        var stagingRoot = Path.Combine(
            installRequest.InstallPath,
            ".retromind-gog-installers",
            storeGameId,
            platformFolder);

        var progressTitle = string.Format(
            T("Gog.Install.ProgressLogTitleFormat", "GOG install - {0}"),
            string.IsNullOrWhiteSpace(item.Title) ? "GOG" : item.Title);
        var progressLogVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressLogView = new ProcessLogView { DataContext = progressLogVm };
        await UiThreadHelper.InvokeAsync(() => progressLogView.Show(owner));

        // Enable cancel button for the download and install phases
        progressLogVm.EnableCancel();

        try
        {
            var workflowResult = await _gogInstallerWorkflowService.RunAsync(
                new GogInstallerWorkflowRequest(
                    item,
                    storeGameId,
                    installRequest.InstallPath,
                    installRequest.Platform,
                    selectedInstallerPackage,
                    stagingRoot,
                    installRequest.WindowsInstallerPreference,
                    installRequest.CreateDesktopShortcut,
                    installRequest.CreateStartMenuShortcuts,
                    installRequest.CleanInstall,
                    UseTemporaryLinuxDestination: true,
                    RequireLinuxPayloadChange: false),
                line => AppendProcessLog(progressLogVm, line),
                progressLogVm.Token);
            if (!workflowResult.Success)
            {
                if (workflowResult.FailureStage == GogInstallerWorkflowFailureStage.Cancelled)
                {
                    progressLogVm.MarkCancelled(T("Gog.Install.Cancelled", "Installation cancelled by user."));
                    AppendProcessLog(
                        progressLogVm,
                        T(
                            "Gog.Install.StagingPreserved",
                            "Staging files preserved for resume on next attempt."));
                }
                else if (workflowResult.FailureStage == GogInstallerWorkflowFailureStage.Download)
                {
                    AppendProcessLog(
                        progressLogVm,
                        string.Format(
                            T("Gog.Install.DownloadFailedFormat", "GOG installer download failed: {0}"),
                            BuildShortErrorDetail(workflowResult.ErrorMessage)));
                }
                else
                {
                    var message = string.IsNullOrWhiteSpace(workflowResult.ErrorMessage)
                        ? T("Gog.Install.RunFailed", "Installer execution failed.")
                        : string.Format(
                            T("Gog.Install.RunFailedFormat", "Installer execution failed: {0}"),
                            workflowResult.ErrorMessage);
                    AppendProcessLog(progressLogVm, message);
                }

                return false;
            }

            var downloadedPackage = workflowResult.DownloadedPackage!;

            AppendProcessLog(progressLogVm, "[Detect] Resolving launch executable...");
            var launchInfo = await DetectGogLaunchInfoAsync(
                item,
                installRequest.InstallPath,
                storeGameId,
                installRequest.Platform);

            if (launchInfo == null)
            {
                AppendProcessLog(progressLogVm, "[Detect] Automatic detection failed, waiting for manual executable selection...");
                launchInfo = await PromptForManualExecutableFallbackAsync(owner, installRequest);
                if (launchInfo == null)
                {
                    AppendProcessLog(
                        progressLogVm,
                        T(
                            "Gog.Install.DetectExecutableFailed",
                            "Installation finished, but Retromind could not detect a launch executable automatically. Configure launch settings manually."));
                    return false;
                }
            }

            var preserveRunnerInheritance = GogLaunchConfigurationHelper.ShouldPreserveRunnerInheritance(
                item,
                inheritedEmulator,
                installRequest.Runner?.Id);
            var applied = ApplyDetectedGogLaunchConfiguration(
                item,
                storeGameId,
                installRequest,
                launchInfo,
                preserveRunnerInheritance);
            if (!applied)
            {
                AppendProcessLog(
                    progressLogVm,
                    T(
                        "Gog.Install.ApplyLaunchFailed",
                        "Installation finished, but launch configuration could not be applied."));
                return false;
            }

            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = GogInstallPathHelper.ToStoredPath(
                launchInfo.InstallRoot,
                _currentSettings.PreferPortableLaunchPaths);
            item.CustomFields[CustomFieldKeyHelper.StoreInstallPlatform] = installRequest.Platform == GogInstallPlatform.Windows ? "windows" : "linux";
            if (installRequest.Platform == GogInstallPlatform.Windows && installRequest.Runner != null)
            {
                item.CustomFields[CustomFieldKeyHelper.StoreInstallRunnerVersionId] = installRequest.Runner.Id;
                item.CustomFields[CustomFieldKeyHelper.StoreInstallWindowsInstallerPreference] = ToInstallerPreferenceStorageValue(installRequest.WindowsInstallerPreference);
            }
            else
            {
                item.CustomFields.Remove(CustomFieldKeyHelper.StoreInstallRunnerVersionId);
                item.CustomFields.Remove(CustomFieldKeyHelper.StoreInstallWindowsInstallerPreference);
            }

            try
            {
                GogInstallDirectorySafety.WriteMarker(launchInfo.InstallRoot, item);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Warning] Failed to write final install marker to '{launchInfo.InstallRoot}': {ex.Message}");
                AppendProcessLog(
                    progressLogVm,
                    string.Format(
                        T(
                            "Gog.Install.MarkerWriteWarningFormat",
                            "Installation succeeded, but Retromind could not mark the install directory as managed: {0}"),
                        BuildShortErrorDetail(ex)));
            }
            
            UpdateInstalledGogFingerprint(item, selectedInstallerPackage);
            if (installRequest.CleanInstall)
            {
                item.GogDlcInstallations = null;
                item.CustomFields = item.CustomFields
                    .Where(static field => field.Key != CustomFieldKeyHelper.StoreDlcUpdateAvailable)
                    .ToDictionary(static field => field.Key, static field => field.Value, StringComparer.Ordinal);
            }

            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();

            if (installRequest.DeleteStagingAfterSuccess)
            {
                GogInstallerWorkflowService.DeleteStagingDirectoryBestEffort(downloadedPackage.StagingDirectory);
                AppendProcessLog(progressLogVm, "Staging data deleted after successful installation.");
            }

            AppendProcessLog(progressLogVm, T("Gog.Install.Success", "Installation completed."));

            if (installedDlcsToReapply.Length > 0)
            {
                await ReapplyInstalledGogDlcsAsync(
                    item,
                    installedDlcsToReapply,
                    owner,
                    progressLogVm,
                    installRequest.DeleteStagingAfterSuccess);
            }
        }
        finally
        {
            progressLogVm.MarkFinished();
            UiThreadHelper.Post(() => progressLogVm.IsRunning = false);
        }

        return true;
    }

    private async Task<bool> PrepareGogInstallDirectoryAsync(
        Window owner,
        MediaItem item,
        GogInstallDialogViewModel.GogInstallDialogResult request)
    {
        var assessment = GogInstallDirectorySafety.Assess(
            request.InstallPath,
            item,
            rejectSymbolicLinks: request.CleanInstall);

        if (!assessment.IsAllowed)
        {
            var message = assessment.Status switch
            {
                GogInstallDirectoryStatus.DangerousPath => string.Format(
                    T(
                        "Gog.Install.DirectoryUnsafeFormat",
                        "The selected install directory is unsafe and cannot be used:\n{0}"),
                    assessment.FullPath),
                GogInstallDirectoryStatus.SymbolicLink => string.Format(
                    T(
                        "Gog.Install.DirectorySymbolicLinkFormat",
                        "The selected install directory contains a symbolic link and cannot be cleaned automatically:\n{0}"),
                    assessment.FullPath),
                GogInstallDirectoryStatus.UnreadableDirectory => string.Format(
                    T(
                        "Gog.Install.DirectoryUnreadableFormat",
                        "Retromind could not inspect the selected install directory:\n{0}"),
                    assessment.FullPath),
                GogInstallDirectoryStatus.UnownedDirectory => string.Format(
                    T(
                        "Gog.Install.DirectoryUnownedFormat",
                        "The selected directory contains files but is not recognized as this game's Retromind installation. Choose another directory or empty it manually:\n{0}"),
                    assessment.FullPath),
                _ => string.Format(
                    T(
                        "Gog.Install.DirectoryInvalidFormat",
                        "The selected install directory is invalid:\n{0}"),
                    assessment.FullPath)
            };

            await ShowInfoDialog(owner, message);
            return false;
        }

        if (IsInstallDirectoryUsedByAnotherItem(assessment.FullPath, item))
        {
            await ShowInfoDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Install.DirectorySharedFormat",
                        "The selected install directory is also used by another library item and cannot be managed safely:\n{0}"),
                    assessment.FullPath));
            return false;
        }

        if (request.CleanInstall && assessment.Status == GogInstallDirectoryStatus.OwnedDirectory)
        {
            var confirmed = await ShowConfirmDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Install.CleanInstallConfirmFormat",
                        "Clean install will permanently delete the contents of this Retromind-managed game directory, including mods and other files stored there. The separate Wine/Proton prefix is preserved:\n{0}\n\nContinue?"),
                    assessment.FullPath));
            if (!confirmed)
                return false;
        }

        try
        {
            // Claim a new/empty directory before download data is written into it.
            // A later clean-install check can then prove that the directory belongs
            // to this exact GOG item.
            GogInstallDirectorySafety.WriteMarker(assessment.FullPath, item);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to write install marker to '{assessment.FullPath}': {ex.Message}");
            await ShowInfoDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Install.MarkerWriteFailedFormat",
                        "Retromind could not safely claim the install directory: {0}"),
                    BuildShortErrorDetail(ex)));
            return false;
        }
    }
    
    private async Task<bool> EnsureGogSignInForInstallAsync(Window owner, bool forceInteractiveSignIn = false)
    {
        if (!forceInteractiveSignIn)
        {
            StoreAuthState authState;
            try
            {
                authState = await _storeAuthProvider.GetAuthStateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Failed to query auth state for install: {ex.Message}");
                await ShowInfoDialog(owner, T("Gog.AuthCheckFailed", "GOG sign-in state could not be verified."));
                return false;
            }

            if (authState.IsAuthenticated)
                return true;

            try
            {
                var refreshed = await _storeAuthProvider.TryRefreshSessionAsync();
                if (refreshed)
                    return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Silent session refresh failed (install): {ex.Message}");
            }
        }

        var promptMessage = forceInteractiveSignIn
            ? T("Gog.SignInRetryPrompt", "GOG session appears to be invalid or expired. Sign in again now?")
            : T("Gog.SignInRequiredPrompt", "GOG sign-in is required. Open secure sign-in now?");

        var signIn = await ShowConfirmDialog(owner, promptMessage);
        if (!signIn)
            return false;

        try
        {
            await _storeAuthProvider.SignInInteractiveAsync(
                (authorizeUri, signInCt) => CaptureGogCallbackUriInAppAsync(owner, authorizeUri, signInCt));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (TimeoutException ex)
        {
            Debug.WriteLine($"[GOG] Interactive sign-in timed out (install): {ex.Message}");
            await ShowInfoDialog(owner, T("Gog.SignInTimeout", "GOG sign-in timed out. Please retry."));
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Interactive sign-in failed (install): {ex.Message}");
            await ShowInfoDialog(owner, GetGogSignInErrorMessage(ex));
            return false;
        }
    }

    private static bool IsLikelyGogAuthIssue(Exception ex)
    {
        var message = ex.Message;
        return message.IndexOf("authentication is required", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("401", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0 ||
               message.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildShortErrorDetail(Exception ex) =>
        BuildShortErrorDetail(ex.Message);

    private static string BuildShortErrorDetail(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Unknown error";

        var compact = message.Trim().Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return compact.Length <= 260 ? compact : compact[..260] + "...";
    }

    private string ResolveDefaultGogInstallPath(MediaItem item, string storeGameId)
    {
        if (item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var savedPath) &&
            GogInstallPathHelper.TryResolveStoredPath(savedPath, out var resolvedSavedPath))
        {
            return resolvedSavedPath;
        }

        var safeTitle = PathHelper.SanitizePathSegment(item.Title);
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = $"gog_{storeGameId}";

        return Path.Combine(AppPaths.LibraryRoot, "Games", "GOG", safeTitle);
    }

    private static GogInstallPlatform? GetPreferredInstalledGogPlatform(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPlatform, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return GogLaunchConfigurationHelper.InferLegacyInstallPlatform(item);
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "linux" => GogInstallPlatform.Linux,
            "windows" => GogInstallPlatform.Windows,
            _ => null
        };
    }

    private static GogWindowsInstallerPreference? GetPreferredInstalledWindowsInstallerPreference(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallWindowsInstallerPreference, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "prefer64" or "64" => GogWindowsInstallerPreference.Prefer64,
            "prefer32" or "32" => GogWindowsInstallerPreference.Prefer32,
            "auto" or "autoprefer64" => GogWindowsInstallerPreference.AutoPrefer64,
            _ => null
        };
    }

    private static string ToInstallerPreferenceStorageValue(GogWindowsInstallerPreference value)
    {
        return value switch
        {
            GogWindowsInstallerPreference.Prefer64 => "prefer64",
            GogWindowsInstallerPreference.Prefer32 => "prefer32",
            _ => "auto"
        };
    }

    private async Task<string?> BrowseFolderForGogInstallAsync(Window owner)
    {
        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("Gog.Install.PickInstallPathTitle", "Select install folder"),
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return null;

        return result[0].Path.LocalPath;
    }

    private async Task<bool> ValidateGogInstallRuntimeRequirementsAsync(
        Window owner,
        GogInstallDialogViewModel.GogInstallDialogResult request)
    {
        if (request.Platform != GogInstallPlatform.Windows)
            return true;

        if (request.Runner == null)
        {
            await ShowInfoDialog(
                owner,
                T(
                    "Gog.Install.ValidationRunnerRequired",
                    "Select a Wine/Proton runner for Windows installation."));
            return false;
        }

        if (string.IsNullOrWhiteSpace(GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
                request.Runner.Kind,
                request.Runner.Path)))
        {
            await ShowInfoDialog(
                owner,
                string.Format(
                    T(
                        "Gog.Install.RunnerInvalidFormat",
                        "The selected runner '{0}' is unavailable or incomplete. Check its path in Settings -> Runner."),
                    request.Runner.Name));
            return false;
        }

        if (string.IsNullOrWhiteSpace(EmulatorResolverHelper.ResolveSystemWine()))
        {
            await ShowInfoDialog(
                owner,
                T(
                    "Gog.Install.SystemWineRequired",
                    "Installing GOG Windows games requires system Wine. Install Wine and try again."));
            return false;
        }

        if (request.Runner.Kind != RunnerVersionKind.Proton || IsUmuRunAvailable())
            return true;

        await ShowInfoDialog(
            owner,
            T(
                "Gog.Install.UmuRequired",
                "Proton runner requires umu-run in PATH. Install umu or choose a Wine runner."));
        return false;
    }

    private static bool IsUmuRunAvailable()
        => !string.IsNullOrWhiteSpace(EnvironmentPathHelper.TryFindExecutableInCurrentPath("umu-run"));

    private static void AppendProcessLog(ProcessLogViewModel logVm, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        UiThreadHelper.Post(() => logVm.AppendLine(line));
    }

    private bool ApplyDetectedGogLaunchConfiguration(
        MediaItem item,
        string storeGameId,
        GogInstallDialogViewModel.GogInstallDialogResult request,
        GogDetectedLaunchInfo launchInfo,
        bool preserveRunnerInheritance)
    {
        if (string.IsNullOrWhiteSpace(launchInfo.ExecutablePath))
            return false;

        string? launcherPath;
        string? launcherArgs;
        string? runnerVersionId;
        string? prefixPath;
        if (request.Platform == GogInstallPlatform.Windows)
        {
            var runner = request.Runner;
            if (runner == null)
                return false;

            var runnerExecutable = GogLaunchConfigurationHelper.ResolveRunnerExecutablePath(
                runner.Kind,
                runner.Path);
            if (string.IsNullOrWhiteSpace(runnerExecutable))
                return false;

            if (runner.Kind == RunnerVersionKind.Proton && !IsUmuRunAvailable())
                return false;

            launcherPath = runner.Kind == RunnerVersionKind.Proton
                ? "umu-run"
                : PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(runnerExecutable)
                  ?? runnerExecutable;
            launcherArgs = string.IsNullOrWhiteSpace(launchInfo.LaunchArguments)
                ? "{file}"
                : LaunchArgumentHelper.NormalizeWhitespace($"{{file}} {launchInfo.LaunchArguments}");
            runnerVersionId = preserveRunnerInheritance ? null : runner.Id;

            prefixPath = item.PrefixPath;
            if (string.IsNullOrWhiteSpace(prefixPath))
            {
                var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(item.Title);
                var folderName = string.IsNullOrWhiteSpace(safeTitle)
                    ? $"gog_{storeGameId}"
                    : $"gog_{storeGameId}_{safeTitle}";
                prefixPath = Path.Combine("Prefixes", folderName);
            }
        }
        else
        {
            launcherPath = null;
            launcherArgs = null;
            runnerVersionId = null;
            prefixPath = null;
        }

        if (request.Platform == GogInstallPlatform.Linux)
            LinuxFileSystemHelper.EnsureExecutableBitBestEffort(launchInfo.ExecutablePath);

        var storedFilePath = launchInfo.ExecutablePath;
        var storedFileKind = MediaFileKind.Absolute;
        if (_currentSettings.PreferPortableLaunchPaths &&
            PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(launchInfo.ExecutablePath, out var relativeExecutable))
        {
            storedFilePath = relativeExecutable;
            storedFileKind = MediaFileKind.LibraryRelative;
        }

        var files = new List<MediaFileRef>
        {
            new()
            {
                Kind = storedFileKind,
                Path = storedFilePath,
                Index = 1
            }
        };
        var workingDirectory = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(
            launchInfo.WorkingDirectory);

        item.Files = files;
        item.WorkingDirectory = workingDirectory;
        item.LauncherPath = launcherPath;
        item.LauncherArgs = launcherArgs;
        item.RunnerVersionId = runnerVersionId;
        item.PrefixPath = prefixPath;
        GogLaunchConfigurationHelper.SetInstalledMediaType(item, request.Platform);

        return true;
    }

    private async Task<GogDetectedLaunchInfo?> DetectGogLaunchInfoAsync(
        MediaItem item,
        string selectedInstallPath,
        string storeGameId,
        GogInstallPlatform platform)
    {
        var fromLocalInfo = _gogLaunchDetectionService.DetectFromLocalMetadata(
            selectedInstallPath,
            storeGameId,
            platform);
        if (fromLocalInfo != null && File.Exists(fromLocalInfo.ExecutablePath))
            return platform == GogInstallPlatform.Linux
                ? _gogLaunchDetectionService.PreferLinuxStartScript(fromLocalInfo, selectedInstallPath)
                : fromLocalInfo;

        try
        {
            var playTasks = await _gogInstallService.GetGameDetailsPlayTasksAsync(storeGameId).ConfigureAwait(false);
            var fromPlayTasks = _gogLaunchDetectionService.DetectFromPlayTasks(
                selectedInstallPath,
                platform,
                playTasks);
            if (fromPlayTasks != null && File.Exists(fromPlayTasks.ExecutablePath))
                return platform == GogInstallPlatform.Linux
                    ? _gogLaunchDetectionService.PreferLinuxStartScript(fromPlayTasks, selectedInstallPath)
                    : fromPlayTasks;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] playTasks fallback detection failed: {ex.Message}");
        }

        var fromFilesystemFallback = _gogLaunchDetectionService.DetectFromFilesystem(
            item.Title,
            selectedInstallPath,
            platform);
        if (fromFilesystemFallback != null && File.Exists(fromFilesystemFallback.ExecutablePath))
            return platform == GogInstallPlatform.Linux
                ? _gogLaunchDetectionService.PreferLinuxStartScript(fromFilesystemFallback, selectedInstallPath)
                : fromFilesystemFallback;

        return null;
    }

    private async Task<GogDetectedLaunchInfo?> PromptForManualExecutableFallbackAsync(
        Window owner,
        GogInstallDialogViewModel.GogInstallDialogResult request)
    {
        var storageProvider = StorageProvider ?? owner.StorageProvider;
        if (storageProvider == null)
            return null;

        var options = new FilePickerOpenOptions
        {
            Title = T("Gog.Install.PickExecutableTitle", "Select launch executable"),
            AllowMultiple = false
        };

        if (request.Platform == GogInstallPlatform.Windows)
        {
            options.FileTypeFilter = new[]
            {
                new FilePickerFileType("Windows executables")
                {
                    Patterns = new[] { "*.exe" }
                }
            };
        }

        var result = await storageProvider.OpenFilePickerAsync(options);
        var selectedFile = result?.FirstOrDefault();
        if (selectedFile == null)
            return null;

        var executablePath = selectedFile.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return null;

        var workingDirectory = Path.GetDirectoryName(executablePath);
        var installRoot = request.InstallPath;
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            installRoot = workingDirectory ?? request.InstallPath;

        return new GogDetectedLaunchInfo(
            executablePath,
            null,
            workingDirectory,
            installRoot);
    }

}
