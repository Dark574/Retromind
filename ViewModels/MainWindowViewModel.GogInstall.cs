using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    private static readonly object InstallerLogFileWriteLock = new();

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
        var preferredRunnerId = GetPreferredInstalledGogRunnerVersionId(item);
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

        var selectedInstallerPackage = SelectInstallerPackageForRequest(installerPackage, installRequest);

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
            GogDownloadedInstallerPackage downloadedPackage;
            var lastLoggedFileIndex = -1;
            var lastLoggedFilePercent = -1;
            var lastLoggedOverallPercent = -1;
            var lastLoggedAtUtc = DateTimeOffset.MinValue;

            AppendProcessLog(progressLogVm, "[Download] Starting installer download...");
            if (installRequest.Platform == GogInstallPlatform.Windows &&
                selectedInstallerPackage.Files.Count != installerPackage.Files.Count)
            {
                AppendProcessLog(
                    progressLogVm,
                    $"[Download] Installer file filter active ({selectedInstallerPackage.Files.Count}/{installerPackage.Files.Count} files).");
            }

            var reusableStagingFiles = CountExistingStagedInstallerFiles(selectedInstallerPackage, stagingRoot);
            if (reusableStagingFiles > 0)
            {
                AppendProcessLog(
                    progressLogVm,
                    $"[Download] Reusing {reusableStagingFiles}/{selectedInstallerPackage.Files.Count} staged installer file(s).");
            }

            var downloadProgress = new Progress<GogInstallerDownloadProgress>(progress =>
            {
                var now = DateTimeOffset.UtcNow;
                var currentFilePercent = CalculateProgressPercent(progress.BytesDownloadedCurrentFile, progress.BytesTotalCurrentFile);
                var overallPercent = CalculateProgressPercent(progress.BytesDownloadedOverall, progress.BytesTotalOverall);
                var fileChanged = progress.FileIndex != lastLoggedFileIndex;
                var fileAdvanced = currentFilePercent >= 0 && currentFilePercent >= lastLoggedFilePercent + 2;
                var overallAdvanced = overallPercent >= 0 && overallPercent >= lastLoggedOverallPercent + 1;
                var timedPulse = now - lastLoggedAtUtc >= TimeSpan.FromMilliseconds(900);

                if (!fileChanged && !fileAdvanced && !overallAdvanced && !timedPulse)
                    return;

                lastLoggedFileIndex = progress.FileIndex;
                if (currentFilePercent >= 0)
                    lastLoggedFilePercent = currentFilePercent;
                if (overallPercent >= 0)
                    lastLoggedOverallPercent = overallPercent;
                lastLoggedAtUtc = now;

                AppendProcessLog(progressLogVm, BuildDownloadProgressLine(progress));
            });

            try
            {
                await Task.Yield();
                downloadedPackage = await _gogInstallService.DownloadInstallerPackageAsync(
                    selectedInstallerPackage,
                    stagingRoot,
                    downloadProgress,
                    progressLogVm.Token);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[GOG] Installer download cancelled by user.");
                progressLogVm.MarkCancelled(T("Gog.Install.Cancelled", "Installation cancelled by user."));
                AppendProcessLog(
                    progressLogVm,
                    T(
                        "Gog.Install.StagingPreserved",
                        "Staging files preserved for resume on next attempt."));
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Installer download failed: {ex.Message}");
                AppendProcessLog(
                    progressLogVm,
                    string.Format(
                        T("Gog.Install.DownloadFailedFormat", "GOG installer download failed: {0}"),
                        BuildShortErrorDetail(ex)));
                return false;
            }

            AppendProcessLog(progressLogVm, "[Install] Starting installer execution...");
            var runResult = await RunInstallerAsync(
                item,
                storeGameId,
                installRequest,
                downloadedPackage,
                progressLogVm,
                progressLogVm.Token);
            if (!runResult.Success)
            {
                var message = string.IsNullOrWhiteSpace(runResult.ErrorMessage)
                    ? T("Gog.Install.RunFailed", "Installer execution failed.")
                    : string.Format(
                        T("Gog.Install.RunFailedFormat", "Installer execution failed: {0}"),
                        runResult.ErrorMessage);
                AppendProcessLog(progressLogVm, message);
                return false;
            }

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

            var applied = ApplyDetectedGogLaunchConfiguration(item, storeGameId, installRequest, launchInfo);
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
                TryDeleteGogStagingDirectoryBestEffort(downloadedPackage.StagingDirectory);
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

    private static string BuildShortErrorDetail(Exception ex)
    {
        var message = ex.Message.Trim();
        if (string.IsNullOrWhiteSpace(message))
            return "Unknown error";

        var compact = message.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return compact.Length <= 260 ? compact : compact[..260] + "...";
    }

    private static int CalculateProgressPercent(long downloadedBytes, long? totalBytes)
    {
        if (!totalBytes.HasValue || totalBytes.Value <= 0)
            return -1;

        var value = (int)Math.Clamp(
            Math.Round(downloadedBytes * 100.0 / totalBytes.Value, MidpointRounding.AwayFromZero),
            0,
            100);
        return value;
    }

    private static string BuildDownloadProgressLine(GogInstallerDownloadProgress progress)
    {
        var filePercent = CalculateProgressPercent(progress.BytesDownloadedCurrentFile, progress.BytesTotalCurrentFile);
        var overallPercent = CalculateProgressPercent(progress.BytesDownloadedOverall, progress.BytesTotalOverall);

        var filePart = $"{progress.FileIndex}/{progress.FileCount} {progress.FileName}";
        var fileBytes = progress.BytesTotalCurrentFile.HasValue && progress.BytesTotalCurrentFile.Value > 0
            ? $"{FormatByteSize(progress.BytesDownloadedCurrentFile)} / {FormatByteSize(progress.BytesTotalCurrentFile.Value)}"
            : FormatByteSize(progress.BytesDownloadedCurrentFile);
        var fileProgress = filePercent >= 0 ? $"{filePercent}% ({fileBytes})" : fileBytes;

        var overallProgress = progress.BytesTotalOverall.HasValue && progress.BytesTotalOverall.Value > 0
            ? (overallPercent >= 0
                ? $"{overallPercent}% ({FormatByteSize(progress.BytesDownloadedOverall)} / {FormatByteSize(progress.BytesTotalOverall.Value)})"
                : $"{FormatByteSize(progress.BytesDownloadedOverall)} / {FormatByteSize(progress.BytesTotalOverall.Value)}")
            : FormatByteSize(progress.BytesDownloadedOverall);

        return $"Download {filePart}: {fileProgress} | Overall: {overallProgress}";
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024d && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} {units[unitIndex]}"
            : $"{value:0.0} {units[unitIndex]}";
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
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "linux" => GogInstallPlatform.Linux,
            "windows" => GogInstallPlatform.Windows,
            _ => null
        };
    }

    private static string? GetPreferredInstalledGogRunnerVersionId(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallRunnerVersionId, out var runnerId) ||
            string.IsNullOrWhiteSpace(runnerId))
        {
            return null;
        }

        return runnerId.Trim();
    }

    private static GogInstallDialogViewModel.WindowsInstallerPreference? GetPreferredInstalledWindowsInstallerPreference(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallWindowsInstallerPreference, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "prefer64" or "64" => GogInstallDialogViewModel.WindowsInstallerPreference.Prefer64,
            "prefer32" or "32" => GogInstallDialogViewModel.WindowsInstallerPreference.Prefer32,
            "auto" or "autoprefer64" => GogInstallDialogViewModel.WindowsInstallerPreference.AutoPrefer64,
            _ => null
        };
    }

    private static string ToInstallerPreferenceStorageValue(GogInstallDialogViewModel.WindowsInstallerPreference value)
    {
        return value switch
        {
            GogInstallDialogViewModel.WindowsInstallerPreference.Prefer64 => "prefer64",
            GogInstallDialogViewModel.WindowsInstallerPreference.Prefer32 => "prefer32",
            _ => "auto"
        };
    }

    private static GogInstallerPackage SelectInstallerPackageForRequest(
        GogInstallerPackage package,
        GogInstallDialogViewModel.GogInstallDialogResult request)
    {
        // Keep all package files for Windows installers.
        // Some GOG installers rely on companion payload files that are not safely inferable by filename heuristics.
        return package;
    }

    private enum WindowsInstallerArchitecture
    {
        Unknown = 0,
        X64 = 1,
        X86 = 2
    }

    private static WindowsInstallerArchitecture DetectWindowsInstallerArchitecture(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return WindowsInstallerArchitecture.Unknown;

        var normalized = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var tokens = normalized
            .Split(['_', '-', '.', ' ', '(', ')', '[', ']', '{', '}', '+'], StringSplitOptions.RemoveEmptyEntries);

        var has64 = tokens.Any(token =>
            token is "x64" or "64" or "64bit" or "win64" or "amd64" ||
            token.EndsWith("x64", StringComparison.Ordinal) ||
            token.EndsWith("64bit", StringComparison.Ordinal));
        var has32 = tokens.Any(token =>
            token is "x86" or "32" or "32bit" or "win32" or "i386" ||
            token.EndsWith("x86", StringComparison.Ordinal) ||
            token.EndsWith("32bit", StringComparison.Ordinal));

        if (has64 && !has32)
            return WindowsInstallerArchitecture.X64;
        if (has32 && !has64)
            return WindowsInstallerArchitecture.X86;

        return WindowsInstallerArchitecture.Unknown;
    }

    private static int CountExistingStagedInstallerFiles(GogInstallerPackage package, string stagingDirectory)
    {
        if (package.Files.Count == 0 || string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
            return 0;

        var existing = 0;
        foreach (var file in package.Files)
        {
            var fileName = SanitizeStagedInstallerFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var candidatePath = Path.Combine(stagingDirectory, fileName);
            if (File.Exists(candidatePath))
                existing++;
        }

        return existing;
    }

    private static string SanitizeStagedInstallerFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "installer.bin";

        var sanitized = fileName;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            sanitized = sanitized.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(sanitized) ? "installer.bin" : sanitized;
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

    private sealed record InstallerRunResult(bool Success, string? ErrorMessage = null);
    private sealed record WindowsInstallerArgumentProfile(
        string Name,
        string? DirectoryArgumentPrefix,
        IReadOnlyList<string>? AdditionalArguments = null);
    private async Task<InstallerRunResult> RunInstallerAsync(
        MediaItem item,
        string storeGameId,
        GogInstallDialogViewModel.GogInstallDialogResult request,
        GogDownloadedInstallerPackage downloadedPackage,
        ProcessLogViewModel logVm,
        CancellationToken ct = default,
        bool useTemporaryLinuxDestination = true,
        bool requireLinuxPayloadChange = false)
    {
        InstallerRunResult Fail(string? errorMessage) => new(false, errorMessage);
        InstallerRunResult Success() => new(true, null);

        try
        {
            Directory.CreateDirectory(request.InstallPath);
            Directory.CreateDirectory(downloadedPackage.StagingDirectory);

            var installerLogPath = Path.Combine(downloadedPackage.StagingDirectory, "retromind-install.log");
            InitializeInstallerLogFile(installerLogPath, request.InstallPath, downloadedPackage.StagingDirectory);
            AppendProcessLog(logVm, $"Detailed log file: {installerLogPath}", installerLogPath);

            if (request.CleanInstall)
            {
                AppendProcessLog(logVm, "Clean install requested: removing existing target files.", installerLogPath);
                if (!TryPrepareCleanInstallDirectory(
                        item,
                        request.InstallPath,
                        downloadedPackage.StagingDirectory,
                        logVm,
                        installerLogPath,
                        out var cleanError))
                {
                    return Fail(cleanError ?? "Clean install preparation failed.");
                }
            }

            if (request.Platform == GogInstallPlatform.Linux)
            {
                Action<string> appendInstallerLog = line => AppendProcessLog(logVm, line, installerLogPath);
                var result = await _gogInstallerExecutionService.RunLinuxAsync(
                    new GogLinuxInstallerExecutionRequest(
                        storeGameId,
                        request.InstallPath,
                        downloadedPackage,
                        useTemporaryLinuxDestination,
                        requireLinuxPayloadChange),
                    appendInstallerLog,
                    ct).ConfigureAwait(false);
                return new InstallerRunResult(result.Success, result.ErrorMessage);
            }

            if (request.Platform == GogInstallPlatform.Windows)
            {
                var winePath = EmulatorResolverHelper.ResolveSystemWine();
                if (winePath == null)
                    return Fail("Windows installation requires a system wine version.");

                var prefixRoot = ResolveOrCreatePrefixRoot(item, storeGameId);
                var prefixDrivePath = Path.Combine(prefixRoot, "drive_c");
                var windowsInstallDestinationPath = ToWineWindowsAbsolutePath(request.InstallPath);
                
                AppendProcessLog(logVm, $"Windows prefix root: {prefixRoot}", installerLogPath);
                AppendProcessLog(logVm, $"Windows prefix dosdevices: {Path.Combine(prefixRoot, "dosdevices")}", installerLogPath);
                Action<string> appendInstallerLog = line => AppendProcessLog(logVm, line, installerLogPath);
                GogInstallerProcessService.AppendWineDosDeviceMappings(appendInstallerLog, prefixRoot);
                AppendProcessLog(logVm, $"Windows installer destination: {windowsInstallDestinationPath}", installerLogPath);
                
                // 1. get install file
                var installerCandidates = BuildWindowsInstallerEntryCandidates(downloadedPackage, request.WindowsInstallerPreference);
                if (installerCandidates.Count == 0)
                    return Fail("Windows installer entry file was not found.");
                
                var installerPath = installerCandidates.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(installerPath))
                    return Fail("Windows installer entry file was not found.");

                AppendProcessLog(logVm, $"[Windows installer] Using: {installerPath}", installerLogPath);
                var payloadBaseline = GogInstallPayloadTracker.Capture(request.InstallPath);
                var prefixPayloadBaseline = GogInstallPayloadTracker.Capture(prefixDrivePath);
                var profiles = BuildWindowsInstallerArgumentProfiles(
                    request.CreateDesktopShortcut,
                    request.CreateStartMenuShortcuts);
                GogInstallerProcessResult? lastExecution = null;

                for (var attemptIndex = 0; attemptIndex < profiles.Count; attemptIndex++)
                {
                    var profile = profiles[attemptIndex];
                    var startInfo = EmulatorResolverHelper.BuildWineInstallStartInfo(winePath, prefixRoot);
                    startInfo.WorkingDirectory = downloadedPackage.StagingDirectory;
                    GogWindowsShortcutPolicy.ApplyInstallerEnvironment(
                        startInfo,
                        request.CreateDesktopShortcut,
                        request.CreateStartMenuShortcuts);
                    startInfo.ArgumentList.Add(installerPath);

                    if (profile.AdditionalArguments is { Count: > 0 })
                    {
                        foreach (var argument in profile.AdditionalArguments)
                        {
                            if (!string.IsNullOrWhiteSpace(argument))
                                startInfo.ArgumentList.Add(argument);
                        }
                    }

                    var dirArg = BuildWindowsInstallerDirectoryArgument(
                        windowsInstallDestinationPath,
                        profile.DirectoryArgumentPrefix);
                    if (!string.IsNullOrEmpty(dirArg))
                        startInfo.ArgumentList.Add(dirArg);

                    var innoLogHostPath = attemptIndex == 0
                        ? Path.Combine(downloadedPackage.StagingDirectory, "inno-setup.log")
                        : Path.Combine(downloadedPackage.StagingDirectory, $"inno-setup-{profile.Name}.log");
                    startInfo.ArgumentList.Add(BuildInnoSetupLogArgument(innoLogHostPath));

                    AppendProcessLog(logVm, $"[Windows installer] Inno log path: {innoLogHostPath}", installerLogPath);
                    AppendProcessLog(
                        logVm,
                        $"[Windows installer] Attempt {attemptIndex + 1}/{profiles.Count} ({profile.Name})",
                        installerLogPath);
                    GogInstallerProcessService.AppendRunnerEnvironmentSnapshot(appendInstallerLog, startInfo);
                    AppendProcessLog(logVm, $"> {GogInstallerProcessService.FormatCommand(startInfo)}", installerLogPath);

                    GogInstallerProcessResult windowsExecution;
                    try
                    {
                        windowsExecution = await _gogInstallerProcessService.ExecuteAsync(
                            startInfo,
                            appendInstallerLog,
                            ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        var removedShortcutExports = GogWindowsShortcutPolicy.RemoveUnwantedExports(
                            prefixRoot,
                            request.CreateDesktopShortcut,
                            request.CreateStartMenuShortcuts,
                            startInfo.Environment);
                        if (removedShortcutExports > 0)
                        {
                            AppendProcessLog(
                                logVm,
                                $"[Windows installer] Removed {removedShortcutExports} unwanted Wine shortcut export(s).",
                                installerLogPath);
                        }
                    }
                    if (!windowsExecution.Started)
                        return Fail(windowsExecution.StartErrorMessage ?? "Installer process could not be started.");

                    lastExecution = windowsExecution;
                    AppendProcessLog(
                        logVm,
                        $"[Windows installer] Process outcome: exit={windowsExecution.ExitCode}, durationMs={windowsExecution.DurationMs}, runtimeCrash={windowsExecution.HasRuntimeCrashError}, unsupportedFlags={windowsExecution.HasUnsupportedFlagsError}, shellParse={windowsExecution.HasShellParsingError}, terminalSpawn={windowsExecution.HasTerminalSpawnError}",
                        installerLogPath);

                    var payloadDetected = await GogInstallPayloadTracker.WaitForChangeAsync(
                        request.InstallPath,
                        payloadBaseline,
                        TimeSpan.FromSeconds(25),
                        ct).ConfigureAwait(false);
                    if (payloadDetected)
                    {
                        if (attemptIndex > 0)
                        {
                            AppendProcessLog(
                                logVm,
                                "[Windows installer] Interactive fallback succeeded; install payload detected.",
                                installerLogPath);
                        }

                        return Success();
                    }

                    if (attemptIndex < profiles.Count - 1)
                    {
                        AppendProcessLog(
                            logVm,
                            "[Windows installer] Silent attempt failed or produced no payload. Starting interactive fallback with prefilled target directory.",
                            installerLogPath);
                        continue;
                    }
                }

                if (lastExecution is { ExitCode: not 0 })
                {
                    AppendProcessLog(logVm, $"[Windows installer] Warning: Exit code {lastExecution.ExitCode}. Payload unchanged.", installerLogPath);
                    return Fail("Windows installer exited with code " + lastExecution.ExitCode + " and did not place files.");
                }

                if (GogInstallPayloadTracker.HasChanged(prefixDrivePath, prefixPayloadBaseline))
                {
                    AppendProcessLog(
                        logVm,
                        $"Installer changed files inside prefix drive_c ({prefixDrivePath}), but target path stayed unchanged. /DIR may have been ignored.",
                        installerLogPath);
                }

                return Fail("Windows installer did not complete successfully.");
            }

            return Success();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            AppendProcessLog(logVm, "Error: executable not found.");
            Debug.WriteLine($"[GOG] Installer execution failed: {ex.Message}");
            return Fail(ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 13)
        {
            AppendProcessLog(logVm, "Error: permission denied while starting installer.");
            Debug.WriteLine($"[GOG] Installer execution failed: {ex.Message}");
            return Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            AppendProcessLog(logVm, "Installer execution cancelled by user.");
            Debug.WriteLine("[GOG] Installer execution cancelled by user.");
            return Fail("Installation cancelled by user.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Installer execution failed: {ex.Message}");
            AppendProcessLog(logVm, $"Error: {ex.Message}");
            return Fail(ex.Message);
        }
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

    private static List<WindowsInstallerArgumentProfile> BuildWindowsInstallerArgumentProfiles(
        bool createDesktopShortcut,
        bool createStartMenuShortcuts)
    {
        var shortcutArguments = BuildWindowsShortcutArguments(
            createDesktopShortcut,
            createStartMenuShortcuts);

        return
        [
            new WindowsInstallerArgumentProfile(
                "inno-silent-dir-argument",
                "/DIR=",
                ["/SP-", "/SILENT", "/NOGUI", "/SUPPRESSMSGBOXES", "/NORESTART", .. shortcutArguments]),
            new WindowsInstallerArgumentProfile(
                "inno-interactive-dir-argument",
                "/DIR=",
                ["/SP-", .. shortcutArguments])
        ];
    }

    // GOG's customized Inno Setup creates shortcuts in installer code instead of
    // exposing the conventional desktopicon task, so /MERGETASKS does not affect it.
    internal static IReadOnlyList<string> BuildWindowsShortcutArguments(
        bool createDesktopShortcut,
        bool createStartMenuShortcuts)
    {
        var arguments = new List<string>(3);
        if (!createDesktopShortcut)
        {
            // GOG's Galaxy setup scripts contain this historical typo. Heroic sends
            // both spellings for compatibility, while offline setup executables vary.
            arguments.Add("/nodesktopshorctut");
            arguments.Add("/nodesktopshortcut");
        }
        if (!createStartMenuShortcuts)
            arguments.Add("/nostartmenushortcut");

        return arguments;
    }

    private static List<string> BuildWindowsInstallerEntryCandidates(
        GogDownloadedInstallerPackage downloadedPackage,
        GogInstallDialogViewModel.WindowsInstallerPreference preference)
    {
        if (downloadedPackage == null)
            return new List<string>();

        var candidates = downloadedPackage.DownloadedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(downloadedPackage.EntryFilePath) &&
            downloadedPackage.EntryFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !candidates.Contains(downloadedPackage.EntryFilePath, StringComparer.Ordinal))
        {
            candidates.Add(downloadedPackage.EntryFilePath);
        }

        var preferredEntry = downloadedPackage.EntryFilePath ?? string.Empty;
        var orderedCandidates = candidates
            .OrderByDescending(path => ScoreWindowsInstallerCandidate(path, preferredEntry, preference))
            .ThenBy(path => path.Length)
            .ToList();

        var preferredArchitectureCandidates = orderedCandidates
            .Where(path => MatchesWindowsInstallerPreference(path, preference))
            .ToList();

        if (preferredArchitectureCandidates.Count == 0)
            return orderedCandidates;

        var fallbackCandidates = orderedCandidates
            .Where(path => !preferredArchitectureCandidates.Contains(path, StringComparer.Ordinal))
            .ToList();

        return preferredArchitectureCandidates
            .Concat(fallbackCandidates)
            .ToList();
    }

    private static int ScoreWindowsInstallerCandidate(
        string path,
        string preferredEntryPath,
        GogInstallDialogViewModel.WindowsInstallerPreference preference)
    {
        if (string.IsNullOrWhiteSpace(path))
            return int.MinValue;

        var score = 0;
        var fileName = Path.GetFileName(path).ToLowerInvariant();

        if (path.Equals(preferredEntryPath, StringComparison.Ordinal))
            score += 1;
        if (fileName.Contains("setup", StringComparison.Ordinal))
            score += 4;
        var has64Token = fileName.Contains("x64", StringComparison.Ordinal) ||
                         fileName.Contains("64", StringComparison.Ordinal);
        var has32Token = fileName.Contains("x86", StringComparison.Ordinal) ||
                         fileName.Contains("32", StringComparison.Ordinal);

        if (has64Token)
        {
            score += preference switch
            {
                GogInstallDialogViewModel.WindowsInstallerPreference.Prefer32 => -3,
                _ => 3
            };
        }

        if (has32Token)
        {
            score += preference switch
            {
                GogInstallDialogViewModel.WindowsInstallerPreference.Prefer32 => 3,
                _ => -2
            };
        }

        return score;
    }

    private static bool MatchesWindowsInstallerPreference(
        string path,
        GogInstallDialogViewModel.WindowsInstallerPreference preference)
    {
        var architecture = DetectWindowsInstallerArchitecture(path);
        if (architecture == WindowsInstallerArchitecture.Unknown)
            return false;

        return preference switch
        {
            GogInstallDialogViewModel.WindowsInstallerPreference.Prefer32 => architecture == WindowsInstallerArchitecture.X86,
            _ => architecture == WindowsInstallerArchitecture.X64
        };
    }

    private static string? BuildWindowsInstallerDirectoryArgument(string targetInstallPath, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        var value = targetInstallPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return prefix;

        return prefix + value;
    }

    private static string BuildInnoSetupLogArgument(string hostLogPath)
    {
        if (string.IsNullOrWhiteSpace(hostLogPath))
            return "/LOG";

        return "/LOG=" + ToWineWindowsAbsolutePath(hostLogPath);
    }

    private static string ToWineWindowsAbsolutePath(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
            return @"Z:\";

        var fullPath = Path.GetFullPath(hostPath);
        var windowsSlashes = fullPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '\\');

        if (windowsSlashes.Length >= 2 && windowsSlashes[1] == ':')
            return windowsSlashes;

        if (windowsSlashes.StartsWith('\\'))
            return $"Z:{windowsSlashes}";

        return $@"Z:\{windowsSlashes.TrimStart('\\')}";
    }

    private static void TryDeleteGogStagingDirectoryBestEffort(string? stagingDirectory)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory))
            return;

        string fullStagingPath;
        try
        {
            fullStagingPath = Path.GetFullPath(stagingDirectory);
        }
        catch
        {
            return;
        }

        var marker = $"{Path.DirectorySeparatorChar}.retromind-gog-installers{Path.DirectorySeparatorChar}";
        if (fullStagingPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        try
        {
            if (Directory.Exists(fullStagingPath))
                Directory.Delete(fullStagingPath, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to delete staging directory '{fullStagingPath}': {ex.Message}");
        }
    }

    private static bool TryPrepareCleanInstallDirectory(
        MediaItem item,
        string installPath,
        string? preservePath,
        ProcessLogViewModel logVm,
        string? installerLogPath,
        out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(installPath))
            return true;

        var assessment = GogInstallDirectorySafety.Assess(
            installPath,
            item,
            rejectSymbolicLinks: true);
        var fullInstallPath = assessment.FullPath;
        if (!assessment.IsAllowed)
        {
            errorMessage = "Clean install was blocked because the selected folder is unsafe or is no longer owned by this Retromind item.";
            AppendProcessLog(logVm, errorMessage, installerLogPath);
            return false;
        }

        Directory.CreateDirectory(fullInstallPath);
        var preservedPaths = new List<string>
        {
            Path.Combine(fullInstallPath, GogInstallDirectorySafety.MarkerFileName)
        };
        if (!string.IsNullOrWhiteSpace(preservePath))
        {
            var fullPreservePath = Path.GetFullPath(preservePath);
            if (IsSubPathOfOrEqual(fullPreservePath, fullInstallPath))
                preservedPaths.Add(fullPreservePath);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(fullInstallPath))
        {
            if (ShouldPreserveDuringCleanInstall(entry, preservedPaths))
            {
                AppendProcessLog(logVm, $"Preserving: {entry}", installerLogPath);
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else if (File.Exists(entry))
                    File.Delete(entry);
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to remove '{entry}': {ex.Message}";
                AppendProcessLog(logVm, errorMessage, installerLogPath);
                return false;
            }
        }

        try
        {
            GogInstallDirectorySafety.WriteMarker(fullInstallPath, item);
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to restore the Retromind install marker: {ex.Message}";
            AppendProcessLog(logVm, errorMessage, installerLogPath);
            return false;
        }

        return true;
    }

    private static bool ShouldPreserveDuringCleanInstall(string candidatePath, IReadOnlyList<string> preservedPaths)
    {
        if (preservedPaths.Count == 0 || string.IsNullOrWhiteSpace(candidatePath))
            return false;

        var fullCandidatePath = Path.GetFullPath(candidatePath);
        foreach (var preservedPath in preservedPaths)
        {
            if (PathsEqual(fullCandidatePath, preservedPath))
                return true;

            if (IsSubPathOfOrEqual(preservedPath, fullCandidatePath))
                return true;
        }

        return false;
    }

    private static bool PathsEqual(string pathA, string pathB)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(pathA), Path.GetFullPath(pathB), comparison);
    }

    private static bool IsSubPathOfOrEqual(string path, string potentialParentPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(potentialParentPath))
            return false;

        var fullPath = Path.GetFullPath(path);
        var fullParent = Path.GetFullPath(potentialParentPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(fullPath, fullParent, comparison))
            return true;

        var parentWithSeparator = fullParent.EndsWith(Path.DirectorySeparatorChar)
            ? fullParent
            : fullParent + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(parentWithSeparator, comparison);
    }

    private static void InitializeInstallerLogFile(string logFilePath, string installPath, string stagingPath)
    {
        if (string.IsNullOrWhiteSpace(logFilePath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var header = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] Retromind GOG installer log{Environment.NewLine}" +
                         $"Install path: {installPath}{Environment.NewLine}" +
                         $"Staging path: {stagingPath}{Environment.NewLine}" +
                         new string('-', 70) + Environment.NewLine;
            File.WriteAllText(logFilePath, header, Encoding.UTF8);
        }
        catch
        {
            // best-effort
        }
    }

    private static void AppendProcessLog(ProcessLogViewModel logVm, string line, string? logFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (!string.IsNullOrWhiteSpace(logFilePath))
            TryAppendLineToInstallerLogFile(logFilePath, line);

        UiThreadHelper.Post(() => logVm.AppendLine(line));
    }

    private static void TryAppendLineToInstallerLogFile(string logFilePath, string line)
    {
        try
        {
            lock (InstallerLogFileWriteLock)
            {
                File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private string ResolveOrCreatePrefixRoot(MediaItem item, string storeGameId)
    {
        string absolutePrefixPath;
        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
        {
            absolutePrefixPath = PrefixPathHelper.ResolveAbsolutePrefixPath(
                item.PrefixPath,
                AppPaths.LibraryRoot);
        }
        else
        {
            var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(item.Title);
            var folderName = string.IsNullOrWhiteSpace(safeTitle)
                ? $"gog_{storeGameId}"
                : $"gog_{storeGameId}_{safeTitle}";
            absolutePrefixPath = Path.Combine(AppPaths.LibraryRoot, "Prefixes", folderName);
        }

        Directory.CreateDirectory(absolutePrefixPath);
        return absolutePrefixPath;
    }

    private bool ApplyDetectedGogLaunchConfiguration(
        MediaItem item,
        string storeGameId,
        GogInstallDialogViewModel.GogInstallDialogResult request,
        GogDetectedLaunchInfo launchInfo)
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
            runnerVersionId = runner.Id;

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
