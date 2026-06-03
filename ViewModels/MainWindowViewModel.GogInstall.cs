using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    private const string StoreInstallPathField = "Store.InstallPath";
    private const string StoreInstallPlatformField = "Store.InstallPlatform";
    private const string StoreInstallRunnerVersionIdField = "Store.InstallRunnerVersionId";
    private const string StoreInstallWindowsInstallerPreferenceField = "Store.InstallWindowsInstallerPreference";
    private static readonly object InstallerLogFileWriteLock = new();

    private sealed record DetectedGogLaunchInfo(
        string ExecutablePath,
        string? LaunchArguments,
        string? WorkingDirectory,
        string InstallRoot);

    private bool ShouldOfferInstallForItem(MediaItem? item)
    {
        if (item == null)
            return false;

        var storeGameId = TryGetStoreGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return false;

        return !HasPlayableLaunchConfiguration(item);
    }

    private static bool HasPlayableLaunchConfiguration(MediaItem item)
    {
        var primaryLaunchPath = item.GetPrimaryLaunchPath();
        if (!string.IsNullOrWhiteSpace(primaryLaunchPath) && File.Exists(primaryLaunchPath))
            return true;

        var launcherPath = item.LauncherPath?.Trim();
        if (string.IsNullOrWhiteSpace(launcherPath))
            return false;

        var resolvedLauncherPath = ResolvePathForExistenceCheck(launcherPath);
        if (string.IsNullOrWhiteSpace(resolvedLauncherPath))
            return true;

        return File.Exists(resolvedLauncherPath);
    }

    private static string? ResolvePathForExistenceCheck(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        if (!path.Contains(Path.DirectorySeparatorChar) && !path.Contains(Path.AltDirectorySeparatorChar))
            return null;

        return AppPaths.ResolveDataPath(path);
    }

    private async Task InstallGogItemAsync(MediaItem item)
    {
        if (CurrentWindow is not { } owner)
            return;

        var storeGameId = TryGetStoreGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return;

        var signedIn = await EnsureGogSignInForInstallAsync(owner);
        if (!signedIn)
            return;

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
                return;

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
                return;
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
            return;
        }

        if (availablePlatforms.Count == 0)
        {
            await ShowInfoDialog(
                owner,
                T("Gog.Install.NoInstallerAvailable", "No installable package is available for this GOG title."));
            return;
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
            preferredWindowsInstallerPreference);
        dialogVm.RequestBrowseInstallPath += async () => await BrowseFolderForGogInstallAsync(owner);

        var dialog = new GogInstallDialogView { DataContext = dialogVm };
        var accepted = await dialog.ShowDialog<bool>(owner);
        if (!accepted || dialogVm.Result == null)
            return;

        var installRequest = dialogVm.Result;
        if (!await ValidateGogInstallRuntimeRequirementsAsync(owner, installRequest))
            return;

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
                return;

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
                return;
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
            return;
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
            return;
        }

        var selectedInstallerPackage = SelectInstallerPackageForRequest(installerPackage, installRequest);

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
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Installer download failed: {ex.Message}");
                AppendProcessLog(
                    progressLogVm,
                    string.Format(
                        T("Gog.Install.DownloadFailedFormat", "GOG installer download failed: {0}"),
                        BuildShortErrorDetail(ex)));
                return;
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
                return;
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
                    return;
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
                return;
            }

            item.CustomFields[StoreInstallPathField] = launchInfo.InstallRoot;
            item.CustomFields[StoreInstallPlatformField] = installRequest.Platform == GogInstallPlatform.Windows ? "windows" : "linux";
            if (installRequest.Platform == GogInstallPlatform.Windows && installRequest.Runner != null)
            {
                item.CustomFields[StoreInstallRunnerVersionIdField] = installRequest.Runner.Id;
                item.CustomFields[StoreInstallWindowsInstallerPreferenceField] = ToInstallerPreferenceStorageValue(installRequest.WindowsInstallerPreference);
            }
            else
            {
                item.CustomFields.Remove(StoreInstallRunnerVersionIdField);
                item.CustomFields.Remove(StoreInstallWindowsInstallerPreferenceField);
            }

            WriteInstallMarker(launchInfo.InstallRoot, item);
            
            UpdateInstalledGogFingerprint(item, selectedInstallerPackage);

            _libraryTracker.MarkDirty();
            NotifyPlayAvailabilityChanged();
            await SaveData();

            if (installRequest.DeleteStagingAfterSuccess)
            {
                TryDeleteGogStagingDirectoryBestEffort(downloadedPackage.StagingDirectory);
                AppendProcessLog(progressLogVm, "Staging data deleted after successful installation.");
            }

            AppendProcessLog(progressLogVm, T("Gog.Install.Success", "Installation completed."));
        }
        finally
        {
            progressLogVm.MarkFinished();
            UiThreadHelper.Post(() => progressLogVm.IsRunning = false);
        }
    }

    private static void WriteInstallMarker(string installPath, MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return;

        if (!item.CustomFields.TryGetValue("Store.GameId", out var gameId) ||
            string.IsNullOrWhiteSpace(gameId))
        {
            return;
        }

        var marker = new
        {
            ProviderId = "gog",
            StoreGameId = gameId,
            MediaItemId = item.Id
        };

        var markerPath = Path.Combine(installPath, ".retromind-install.json");
        try
        {
            var json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(markerPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Warning] Failed to write install marker to '{markerPath}': {ex.Message}");
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
            var inAppUnavailable = ex is PlatformNotSupportedException;
            var waylandAppImageUnsupported = inAppUnavailable &&
                                             ex.Message.IndexOf("AppImage Wayland", StringComparison.OrdinalIgnoreCase) >= 0;
            var mismatch = ex.Message.IndexOf("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase) >= 0;
            var missingCode = ex.Message.IndexOf("authorization code", StringComparison.OrdinalIgnoreCase) >= 0;
            var invalidAuthorizeUri = ex.Message.IndexOf("authorization URL", StringComparison.OrdinalIgnoreCase) >= 0;
            var invalidRedirectUri = ex.Message.IndexOf("redirect URI", StringComparison.OrdinalIgnoreCase) >= 0;
            var signInErrorMessage = waylandAppImageUnsupported
                ? T(
                    "Gog.InAppAuthUnavailableWaylandAppImage",
                    "Embedded web authentication is currently not supported in AppImage Wayland sessions. Restart Retromind with --avalonia-platform=x11 and retry.")
                : inAppUnavailable
                ? T("Gog.InAppAuthUnavailable", "Embedded web authentication is not available on this platform.")
                : mismatch
                    ? T("Gog.RedirectMismatch", "GOG rejected the OAuth redirect URI. Please update OAuth client settings or use a compatible client.")
                    : missingCode
                        ? T("Gog.CallbackMissingCode", "The authentication callback did not include an authorization code.")
                        : invalidAuthorizeUri
                            ? T("Gog.InvalidAuthorizeUri", "The received GOG authorization URL is invalid.")
                            : invalidRedirectUri
                                ? T("Gog.InvalidRedirectUri", "The configured OAuth redirect URI is invalid.")
                                : T("Gog.SignInFailed", "GOG sign-in failed.");
            await ShowInfoDialog(owner, signInErrorMessage);
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
        if (item.CustomFields.TryGetValue(StoreInstallPathField, out var savedPath) &&
            !string.IsNullOrWhiteSpace(savedPath) &&
            Path.IsPathRooted(savedPath))
        {
            return savedPath;
        }

        var safeTitle = PathHelper.SanitizePathSegment(item.Title);
        if (string.IsNullOrWhiteSpace(safeTitle))
            safeTitle = $"gog_{storeGameId}";

        return Path.Combine(AppPaths.LibraryRoot, "Games", "GOG", safeTitle);
    }

    private static GogInstallPlatform? GetPreferredInstalledGogPlatform(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(StoreInstallPlatformField, out var raw) ||
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
        if (!item.CustomFields.TryGetValue(StoreInstallRunnerVersionIdField, out var runnerId) ||
            string.IsNullOrWhiteSpace(runnerId))
        {
            return null;
        }

        return runnerId.Trim();
    }

    private static GogInstallDialogViewModel.WindowsInstallerPreference? GetPreferredInstalledWindowsInstallerPreference(MediaItem item)
    {
        if (!item.CustomFields.TryGetValue(StoreInstallWindowsInstallerPreferenceField, out var raw) ||
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
    private sealed record InstallerProcessExecutionResult(
        bool Started,
        int ExitCode,
        long DurationMs,
        string? StartErrorMessage,
        bool HasUnsupportedFlagsError,
        bool HasShellParsingError,
        bool HasTerminalSpawnError,
        bool HasRuntimeCrashError);
    private sealed record LinuxInstallerArgumentProfile(string Name, IReadOnlyList<string> Arguments);
    private sealed record WindowsInstallerArgumentProfile(
        string Name,
        string? DirectoryArgumentPrefix,
        IReadOnlyList<string>? AdditionalArguments = null);
    private sealed record LinuxInstallerCompatibilityEnvironment(string? ShimDirectory, string? RealKonsolePath);

    private async Task<InstallerRunResult> RunInstallerAsync(
        MediaItem item,
        string storeGameId,
        GogInstallDialogViewModel.GogInstallDialogResult request,
        GogDownloadedInstallerPackage downloadedPackage,
        ProcessLogViewModel logVm,
        CancellationToken ct = default)
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
                var installerPath = downloadedPackage.EntryFilePath;
                if (!File.Exists(installerPath))
                    return Fail("Linux installer entry file was not found.");

                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(
                            installerPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch
                    {
                        // best-effort: installer might still be executable
                    }
                }

                AppendProcessLog(logVm, $"Install path: {request.InstallPath}", installerLogPath);
                AppendProcessLog(logVm, $"Staging path: {downloadedPackage.StagingDirectory}", installerLogPath);
                var effectiveInstallPath = ResolveSafeLinuxInstallerDestinationPath(request.InstallPath, storeGameId);
                var usesTemporaryInstallPath = !PathsEqual(effectiveInstallPath, request.InstallPath);
                if (usesTemporaryInstallPath)
                {
                    Directory.CreateDirectory(effectiveInstallPath);
                    AppendProcessLog(logVm, $"Installer destination (temporary): {effectiveInstallPath}", installerLogPath);
                }

                var linuxCompatibilityEnvironment = PrepareLinuxInstallerCompatibilityEnvironment(logVm, installerLogPath);
                try
                {
                    var linuxProfiles = BuildLinuxInstallerArgumentProfiles(effectiveInstallPath);
                    var profileSucceeded = false;
                    for (var i = 0; i < linuxProfiles.Count; i++)
                    {
                        var profile = linuxProfiles[i];
                        var startInfo = CreateInstallerProcessStartInfo(downloadedPackage.StagingDirectory);
                        ApplyLinuxInstallerCompatibilityEnvironment(startInfo, linuxCompatibilityEnvironment);
                        startInfo.FileName = installerPath;
                        foreach (var argument in profile.Arguments)
                            startInfo.ArgumentList.Add(argument);

                        AppendProcessLog(logVm, $"[Linux installer] Attempt {i + 1}/{linuxProfiles.Count} ({profile.Name})", installerLogPath);
                        AppendProcessLog(logVm, $"> {FormatProcessCommand(startInfo)}", installerLogPath);

                        var execution = await ExecuteInstallerProcessWithLogAsync(startInfo, logVm, installerLogPath, ct).ConfigureAwait(false);
                        if (!execution.Started)
                            return Fail(execution.StartErrorMessage ?? "Installer process could not be started.");

                        if (execution.ExitCode == 0)
                        {
                            profileSucceeded = true;
                            break;
                        }

                        if (i < linuxProfiles.Count - 1 &&
                            (execution.HasUnsupportedFlagsError || execution.HasShellParsingError || execution.HasTerminalSpawnError))
                        {
                            if (execution.HasUnsupportedFlagsError)
                                AppendProcessLog(logVm, "Installer rejected flags, retrying with compatibility profile.");
                            else if (execution.HasShellParsingError)
                                AppendProcessLog(logVm, "Installer hit shell argument parsing issue, retrying with compatibility profile.");
                            else
                                AppendProcessLog(logVm, "Installer hit terminal launch issue, retrying with compatibility profile.");
                            continue;
                        }

                        break;
                    }

                    if (!profileSucceeded)
                    {
                        AppendProcessLog(logVm, "[Linux installer] Fallback: extract mode (noexec + startmojo direct)", installerLogPath);
                        var extractedFallbackResult = await RunLinuxInstallerViaExtractedStartMojoAsync(
                            installerPath,
                            downloadedPackage.StagingDirectory,
                            effectiveInstallPath,
                            linuxCompatibilityEnvironment,
                            logVm,
                            installerLogPath,
                            ct).ConfigureAwait(false);
                        if (!extractedFallbackResult.Success)
                            return extractedFallbackResult;
                    }
                }
                finally
                {
                    CleanupLinuxInstallerCompatibilityEnvironment(linuxCompatibilityEnvironment);
                }

                if (usesTemporaryInstallPath)
                {
                    AppendProcessLog(logVm, $"Promoting install from temporary path to requested path: {request.InstallPath}", installerLogPath);
                    MoveDirectoryContentsOverwrite(effectiveInstallPath, request.InstallPath);
                    TryDeleteDirectoryIfEmpty(effectiveInstallPath);
                }

                EnsureLinuxInstalledExecutablePermissionsBestEffort(request.InstallPath);

                return Success();
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
                AppendWineDosDeviceMappings(logVm, installerLogPath, prefixRoot);
                AppendProcessLog(logVm, $"Windows installer destination: {windowsInstallDestinationPath}", installerLogPath);
                
                // 1. get install file
                var installerCandidates = BuildWindowsInstallerEntryCandidates(downloadedPackage, request.WindowsInstallerPreference);
                if (installerCandidates.Count == 0)
                    return Fail("Windows installer entry file was not found.");
                
                var installerPath = installerCandidates.FirstOrDefault(File.Exists);
                if (string.IsNullOrWhiteSpace(installerPath))
                    return Fail("Windows installer entry file was not found.");

                AppendProcessLog(logVm, $"[Windows installer] Using: {installerPath}", installerLogPath);
                var payloadBaseline = CaptureInstallPayloadSnapshot(request.InstallPath);
                var prefixPayloadBaseline = CaptureInstallPayloadSnapshot(prefixDrivePath);
                var profiles = BuildWindowsInstallerArgumentProfiles();
                InstallerProcessExecutionResult? lastExecution = null;

                for (var attemptIndex = 0; attemptIndex < profiles.Count; attemptIndex++)
                {
                    var profile = profiles[attemptIndex];
                    var startInfo = EmulatorResolverHelper.BuildWineInstallStartInfo(winePath, prefixRoot);
                    startInfo.WorkingDirectory = downloadedPackage.StagingDirectory;
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
                    AppendRunnerEnvironmentSnapshot(logVm, installerLogPath, startInfo);
                    AppendProcessLog(logVm, $"> {FormatProcessCommand(startInfo)}", installerLogPath);

                    var windowsExecution = await ExecuteInstallerProcessWithLogAsync(startInfo, logVm, installerLogPath, ct).ConfigureAwait(false);
                    if (!windowsExecution.Started)
                        return Fail(windowsExecution.StartErrorMessage ?? "Installer process could not be started.");

                    lastExecution = windowsExecution;
                    AppendProcessLog(
                        logVm,
                        $"[Windows installer] Process outcome: exit={windowsExecution.ExitCode}, durationMs={windowsExecution.DurationMs}, runtimeCrash={windowsExecution.HasRuntimeCrashError}, unsupportedFlags={windowsExecution.HasUnsupportedFlagsError}, shellParse={windowsExecution.HasShellParsingError}, terminalSpawn={windowsExecution.HasTerminalSpawnError}",
                        installerLogPath);

                    var payloadDetected = await WaitForWindowsInstallPayloadChangeAsync(
                        request.InstallPath,
                        payloadBaseline).ConfigureAwait(false);
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

                if (HasInstallPayloadChanged(prefixDrivePath, prefixPayloadBaseline))
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

    private static ProcessStartInfo CreateInstallerProcessStartInfo(string stagingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            WorkingDirectory = stagingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        HostProcessEnvironmentSanitizer.Sanitize(startInfo);
        return startInfo;
    }

    private static List<LinuxInstallerArgumentProfile> BuildLinuxInstallerArgumentProfiles(string installPath)
    {
        return
        [
            new LinuxInstallerArgumentProfile(
                "makeself-pass-through",
                ["--nox11", "--", "--i-agree-to-all-licenses", "--noreadme", "--destination", installPath]),
            new LinuxInstallerArgumentProfile(
                "legacy-direct",
                ["--nox11", "--i-agree-to-all-licenses", "--noreadme", "--destination", installPath]),
            new LinuxInstallerArgumentProfile(
                "pass-through-destination-only",
                ["--nox11", "--", "--destination", installPath]),
            new LinuxInstallerArgumentProfile(
                "legacy-destination-only",
                ["--nox11", "--destination", installPath])
        ];
    }

    private static LinuxInstallerCompatibilityEnvironment PrepareLinuxInstallerCompatibilityEnvironment(
        ProcessLogViewModel logVm,
        string? installerLogPath)
    {
        if (!OperatingSystem.IsLinux())
            return new LinuxInstallerCompatibilityEnvironment(null, null);

        var realKonsolePath = TryFindExecutableInCurrentPath("konsole");
        if (string.IsNullOrWhiteSpace(realKonsolePath))
            return new LinuxInstallerCompatibilityEnvironment(null, null);

        try
        {
            var shimDirectory = Path.Combine(
                Path.GetTempPath(),
                "retromind-gog-shims",
                $"konsole-{Guid.NewGuid():N}");
            Directory.CreateDirectory(shimDirectory);

            var shimPath = Path.Combine(shimDirectory, "konsole");
            const string shimScript = """
            #!/usr/bin/env bash
            real="${RETROMIND_REAL_KONSOLE:-konsole}"
            args=()
            for arg in "$@"; do
              if [[ "$arg" == "-title" ]]; then
                args+=("--title")
              else
                args+=("$arg")
              fi
            done
            exec "$real" "${args[@]}"
            """;
            File.WriteAllText(shimPath, shimScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(
                shimPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            AppendProcessLog(logVm, $"Linux compatibility shim enabled for konsole: {shimPath}", installerLogPath);
            return new LinuxInstallerCompatibilityEnvironment(shimDirectory, realKonsolePath);
        }
        catch (Exception ex)
        {
            AppendProcessLog(logVm, $"Warning: could not initialize konsole compatibility shim ({ex.Message})", installerLogPath);
            return new LinuxInstallerCompatibilityEnvironment(null, null);
        }
    }

    private static void ApplyLinuxInstallerCompatibilityEnvironment(
        ProcessStartInfo startInfo,
        LinuxInstallerCompatibilityEnvironment compatibilityEnvironment)
    {
        if (string.IsNullOrWhiteSpace(compatibilityEnvironment.ShimDirectory))
            return;

        var currentPath = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
            ? configuredPath
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? compatibilityEnvironment.ShimDirectory
            : compatibilityEnvironment.ShimDirectory + Path.PathSeparator + currentPath;

        if (!string.IsNullOrWhiteSpace(compatibilityEnvironment.RealKonsolePath))
            startInfo.Environment["RETROMIND_REAL_KONSOLE"] = compatibilityEnvironment.RealKonsolePath;
    }

    private static void CleanupLinuxInstallerCompatibilityEnvironment(LinuxInstallerCompatibilityEnvironment compatibilityEnvironment)
    {
        if (string.IsNullOrWhiteSpace(compatibilityEnvironment.ShimDirectory))
            return;

        try
        {
            if (Directory.Exists(compatibilityEnvironment.ShimDirectory))
                Directory.Delete(compatibilityEnvironment.ShimDirectory, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private static string? TryFindExecutableInCurrentPath(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
            return null;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var segment in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = segment.Trim();
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidatePath;
            try
            {
                candidatePath = Path.Combine(directory, executableName);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(candidatePath))
                continue;

            return candidatePath;
        }

        return null;
    }

    private static string? ResolveProtonRunnerRootPath(
        GogInstallDialogViewModel.RunnerOption runner,
        string? resolvedRunnerExecutablePath)
    {
        var configuredPath = runner.Path?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var resolvedConfiguredPath = Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : AppPaths.ResolveDataPath(configuredPath);

            if (Directory.Exists(resolvedConfiguredPath))
                return resolvedConfiguredPath;

            if (File.Exists(resolvedConfiguredPath))
                return Path.GetDirectoryName(resolvedConfiguredPath);
        }

        if (string.IsNullOrWhiteSpace(resolvedRunnerExecutablePath))
            return null;

        if (Directory.Exists(resolvedRunnerExecutablePath))
            return resolvedRunnerExecutablePath;

        return File.Exists(resolvedRunnerExecutablePath)
            ? Path.GetDirectoryName(resolvedRunnerExecutablePath)
            : null;
    }

    private async Task<bool> ValidateGogInstallRuntimeRequirementsAsync(
        Window owner,
        GogInstallDialogViewModel.GogInstallDialogResult request)
    {
        if (request.Platform != GogInstallPlatform.Windows ||
            request.Runner?.Kind != RunnerVersionKind.Proton)
        {
            return true;
        }

        if (IsUmuRunAvailable())
            return true;

        await ShowInfoDialog(
            owner,
            T(
                "Gog.Install.UmuRequired",
                "Proton runner requires umu-run in PATH. Install umu or choose a Wine runner."));
        return false;
    }

    private static bool IsUmuRunAvailable()
        => !string.IsNullOrWhiteSpace(TryFindExecutableInCurrentPath("umu-run"));

    private static List<WindowsInstallerArgumentProfile> BuildWindowsInstallerArgumentProfiles()
    {
        return
        [
            new WindowsInstallerArgumentProfile(
                "inno-silent-dir-argument",
                "/DIR=",
                ["/SP-", "/SILENT", "/NOGUI", "/SUPPRESSMSGBOXES", "/NORESTART"]),
            new WindowsInstallerArgumentProfile(
                "inno-interactive-dir-argument",
                "/DIR=",
                ["/SP-"])
        ];
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

    private static string ResolveSteamCompatClientInstallPath()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(userHome) ? string.Empty : Path.Combine(userHome, ".steam", "steam"),
            string.IsNullOrWhiteSpace(userHome) ? string.Empty : Path.Combine(userHome, ".local", "share", "Steam"),
            userHome,
            AppPaths.DataRoot
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                if (Directory.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // try next
            }
        }

        return !string.IsNullOrWhiteSpace(userHome) ? userHome : AppPaths.DataRoot;
    }

    private readonly struct InstallPayloadSnapshot(int fileCount, DateTimeOffset latestWriteUtc)
    {
        public int FileCount { get; } = fileCount;
        public DateTimeOffset LatestWriteUtc { get; } = latestWriteUtc;
    }

    private static async Task<bool> WaitForWindowsInstallPayloadChangeAsync(
        string installPath,
        InstallPayloadSnapshot baseline)
    {
        if (HasInstallPayloadChanged(installPath, baseline))
            return true;

        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(25);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (HasInstallPayloadChanged(installPath, baseline))
                return true;
        }

        return HasInstallPayloadChanged(installPath, baseline);
    }

    private static bool HasInstallPayloadChanged(string installPath, InstallPayloadSnapshot baseline)
    {
        var current = CaptureInstallPayloadSnapshot(installPath);
        if (current.FileCount > baseline.FileCount)
            return true;

        return current.LatestWriteUtc > baseline.LatestWriteUtc;
    }

    private static InstallPayloadSnapshot CaptureInstallPayloadSnapshot(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return new InstallPayloadSnapshot(0, DateTimeOffset.MinValue);

        try
        {
            var fileCount = 0;
            var latestWriteUtc = DateTimeOffset.MinValue;
            foreach (var file in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
            {
                if (IsInsideInstallerStaging(file))
                    continue;

                fileCount++;
                var writeUtc = File.GetLastWriteTimeUtc(file);
                if (writeUtc > latestWriteUtc.UtcDateTime)
                    latestWriteUtc = new DateTimeOffset(writeUtc, TimeSpan.Zero);
            }

            return new InstallPayloadSnapshot(fileCount, latestWriteUtc);
        }
        catch
        {
            return new InstallPayloadSnapshot(0, DateTimeOffset.MinValue);
        }
    }

    private static string ResolveSafeLinuxInstallerDestinationPath(string requestedInstallPath, string storeGameId)
    {
        if (!HasShellSensitivePathCharacters(requestedInstallPath))
            return requestedInstallPath;

        var safeId = string.IsNullOrWhiteSpace(storeGameId)
            ? "gog"
            : new string(storeGameId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = "gog";

        var safeRoot = Path.Combine(Path.GetTempPath(), "retromind-gog-install", safeId);
        Directory.CreateDirectory(safeRoot);
        return Path.Combine(
            safeRoot,
            $"install-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
    }

    private static bool HasShellSensitivePathCharacters(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var ch in path)
        {
            if (ch is ' ' or '\t' or '\r' or '\n' or '(' or ')' or '\'' or '"' or '`' or '$' or '&' or ';' or '|' or '<' or '>' or '*' or '?' or '[' or ']' or '{' or '}' or '!')
                return true;
        }

        return false;
    }

    private async Task<InstallerRunResult> RunLinuxInstallerViaExtractedStartMojoAsync(
        string installerPath,
        string installerWorkingDirectory,
        string installPath,
        LinuxInstallerCompatibilityEnvironment compatibilityEnvironment,
        ProcessLogViewModel logVm,
        string? installerLogPath,
        CancellationToken ct = default)
    {
        var extractionRoot = Path.Combine(
            Path.GetTempPath(),
            "retromind-gog-extract",
            $"extract-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(extractionRoot);
        try
        {
            var extractStartInfo = CreateInstallerProcessStartInfo(installerWorkingDirectory);
            ApplyLinuxInstallerCompatibilityEnvironment(extractStartInfo, compatibilityEnvironment);
            extractStartInfo.FileName = installerPath;
            extractStartInfo.ArgumentList.Add("--noexec");
            extractStartInfo.ArgumentList.Add("--target");
            extractStartInfo.ArgumentList.Add(extractionRoot);

            AppendProcessLog(logVm, $"> {FormatProcessCommand(extractStartInfo)}", installerLogPath);
            var extractExecution = await ExecuteInstallerProcessWithLogAsync(extractStartInfo, logVm, installerLogPath, ct).ConfigureAwait(false);
            if (!extractExecution.Started)
                return new InstallerRunResult(false, extractExecution.StartErrorMessage ?? "Installer extraction process could not be started.");
            if (extractExecution.ExitCode != 0)
                return new InstallerRunResult(false, $"Exit code {extractExecution.ExitCode}");

            var startMojoPath = Path.Combine(extractionRoot, "startmojo.sh");
            if (!File.Exists(startMojoPath))
                return new InstallerRunResult(false, "Extracted Linux installer is missing startmojo.sh.");

            if (OperatingSystem.IsLinux())
            {
                try
                {
                    File.SetUnixFileMode(
                        startMojoPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch
                {
                    // best-effort
                }
            }

            var runStartMojoInfo = CreateInstallerProcessStartInfo(extractionRoot);
            ApplyLinuxInstallerCompatibilityEnvironment(runStartMojoInfo, compatibilityEnvironment);
            runStartMojoInfo.FileName = startMojoPath;
            runStartMojoInfo.ArgumentList.Add("--i-agree-to-all-licenses");
            runStartMojoInfo.ArgumentList.Add("--noreadme");
            runStartMojoInfo.ArgumentList.Add("--destination");
            runStartMojoInfo.ArgumentList.Add(installPath);

            AppendProcessLog(logVm, $"> {FormatProcessCommand(runStartMojoInfo)}", installerLogPath);
            var runExecution = await ExecuteInstallerProcessWithLogAsync(runStartMojoInfo, logVm, installerLogPath, ct).ConfigureAwait(false);
            if (!runExecution.Started)
                return new InstallerRunResult(false, runExecution.StartErrorMessage ?? "startmojo process could not be started.");
            if (runExecution.ExitCode != 0)
                return new InstallerRunResult(false, $"Exit code {runExecution.ExitCode}");

            return new InstallerRunResult(true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractionRoot))
                    Directory.Delete(extractionRoot, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static void MoveDirectoryContentsOverwrite(string sourceDirectory, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return;

        Directory.CreateDirectory(targetDirectory);

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (string.IsNullOrWhiteSpace(directoryName))
                continue;

            var targetSubDirectory = Path.Combine(targetDirectory, directoryName);
            Directory.CreateDirectory(targetSubDirectory);
            MoveDirectoryContentsOverwrite(directoryPath, targetSubDirectory);
            TryDeleteDirectoryIfEmpty(directoryPath);
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            var targetFile = Path.Combine(targetDirectory, fileName);
            var sourceMode = TryGetUnixFileModeBestEffort(filePath);
            try
            {
                File.Move(filePath, targetFile, overwrite: true);
            }
            catch (IOException)
            {
                File.Copy(filePath, targetFile, overwrite: true);
                TrySetUnixFileModeBestEffort(targetFile, sourceMode);
                File.Delete(filePath);
            }
        }
    }

    private static UnixFileMode? TryGetUnixFileModeBestEffort(string path)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return File.GetUnixFileMode(path);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetUnixFileModeBestEffort(string path, UnixFileMode? mode)
    {
        if (!OperatingSystem.IsLinux() ||
            mode == null ||
            string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode.Value);
        }
        catch
        {
            // best-effort
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return;

        if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
            return;

        Directory.Delete(directoryPath, recursive: false);
    }

    private static bool PathsEqual(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return false;

        var fullA = Path.GetFullPath(pathA);
        var fullB = Path.GetFullPath(pathB);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullA, fullB, comparison);
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
        string installPath,
        string? preservePath,
        ProcessLogViewModel logVm,
        string? installerLogPath,
        out string? errorMessage)
    {
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(installPath))
            return true;

        var fullInstallPath = Path.GetFullPath(installPath);
        if (IsUnsafeCleanInstallPath(fullInstallPath))
        {
            errorMessage = "Clean install was blocked because the selected folder is unsafe.";
            AppendProcessLog(logVm, errorMessage, installerLogPath);
            return false;
        }

        Directory.CreateDirectory(fullInstallPath);
        var preservedPaths = new List<string>();
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

        return true;
    }

    private static bool IsUnsafeCleanInstallPath(string fullInstallPath)
    {
        if (string.IsNullOrWhiteSpace(fullInstallPath))
            return true;

        var root = Path.GetPathRoot(fullInstallPath);
        if (string.IsNullOrWhiteSpace(root))
            return true;

        if (PathsEqual(fullInstallPath, root))
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && PathsEqual(fullInstallPath, home))
            return true;

        if (PathsEqual(fullInstallPath, AppPaths.DataRoot) ||
            PathsEqual(fullInstallPath, AppPaths.LibraryRoot))
        {
            return true;
        }

        return false;
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

    private static async Task<InstallerProcessExecutionResult> ExecuteInstallerProcessWithLogAsync(
        ProcessStartInfo startInfo,
        ProcessLogViewModel logVm,
        string? installerLogPath = null,
        CancellationToken ct = default)
    {
        var hasUnsupportedFlagsError = false;
        var hasShellParsingError = false;
        var hasTerminalSpawnError = false;
        var hasRuntimeCrashError = false;

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            if (LooksLikeUnsupportedFlagError(e.Data))
                hasUnsupportedFlagsError = true;
            if (LooksLikeShellArgumentParsingError(e.Data))
                hasShellParsingError = true;
            if (LooksLikeTerminalSpawnError(e.Data))
                hasTerminalSpawnError = true;
            if (LooksLikeRuntimeCrashError(e.Data))
                hasRuntimeCrashError = true;

            AppendProcessLog(logVm, e.Data, installerLogPath);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            if (LooksLikeUnsupportedFlagError(e.Data))
                hasUnsupportedFlagsError = true;
            if (LooksLikeShellArgumentParsingError(e.Data))
                hasShellParsingError = true;
            if (LooksLikeTerminalSpawnError(e.Data))
                hasTerminalSpawnError = true;
            if (LooksLikeRuntimeCrashError(e.Data))
                hasRuntimeCrashError = true;

            AppendProcessLog(logVm, e.Data, installerLogPath);
        };

        if (!process.Start())
            return new InstallerProcessExecutionResult(false, -1, 0, "Installer process could not be started.", false, false, false, false);

        var stopwatch = Stopwatch.StartNew();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Poll for cancellation while waiting for process to exit
            while (!process.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // if aborted, the process needs to be killedexplicitly
            if (!process.HasExited)
            {
                try 
                { 
                    process.Kill(true); // true = also child-processes (important for Wine/Shells) 
                }
                catch { /* ignore kill errors */ }
            }
            throw;
        }

        stopwatch.Stop();
        AppendProcessLog(logVm, $"Exit code: {process.ExitCode}", installerLogPath);

        return new InstallerProcessExecutionResult(
            Started: true,
            ExitCode: process.ExitCode,
            DurationMs: stopwatch.ElapsedMilliseconds,
            StartErrorMessage: null,
            HasUnsupportedFlagsError: hasUnsupportedFlagsError,
            HasShellParsingError: hasShellParsingError,
            HasTerminalSpawnError: hasTerminalSpawnError,
            HasRuntimeCrashError: hasRuntimeCrashError);
    }

    private static void AppendRunnerEnvironmentSnapshot(
        ProcessLogViewModel logVm,
        string? installerLogPath,
        ProcessStartInfo startInfo)
    {
        var keys =
            new[]
            {
                "PROTONPATH",
                "STEAM_COMPAT_DATA_PATH",
                "WINEPREFIX",
                "STEAM_COMPAT_CLIENT_INSTALL_PATH",
                "STEAM_COMPAT_INSTALL_PATH",
                "PROTON_LOG",
                "PROTON_LOG_DIR",
                "PROTON_USE_XALIA"
            };

        foreach (var key in keys)
        {
            if (!startInfo.Environment.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            AppendProcessLog(logVm, $"ENV {key}={value}", installerLogPath);
        }
    }

    private static void AppendWineDosDeviceMappings(ProcessLogViewModel logVm, string? installerLogPath, string prefixRoot)
    {
        if (string.IsNullOrWhiteSpace(prefixRoot))
            return;

        var dosdevicesPath = Path.Combine(prefixRoot, "dosdevices");
        if (!Directory.Exists(dosdevicesPath))
            return;

        foreach (var path in Directory.EnumerateFileSystemEntries(dosdevicesPath).OrderBy(p => p, StringComparer.Ordinal))
        {
            var label = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            try
            {
                var linkTarget = File.ResolveLinkTarget(path, returnFinalTarget: false);
                var targetPath = linkTarget?.FullName ?? "(not a symlink)";
                AppendProcessLog(logVm, $"[Windows prefix] {label} -> {targetPath}", installerLogPath);
            }
            catch (Exception ex)
            {
                AppendProcessLog(logVm, $"[Windows prefix] {label} -> <unresolved: {ex.Message}>", installerLogPath);
            }
        }
    }

    private static long TryGetFileSizeBytes(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return -1;
        }
    }

    private static DateTimeOffset TryGetFileLastWriteUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool LooksLikeUnsupportedFlagError(string line)
    {
        return line.IndexOf("unrecognized flag", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeShellArgumentParsingError(string line)
    {
        return line.IndexOf("syntax error", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("syntaxfehler", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("unexpected token", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("unerwarteten symbol", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeTerminalSpawnError(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return (line.IndexOf("konsole", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (line.IndexOf("unknown option", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 line.IndexOf("unbekannte option", StringComparison.OrdinalIgnoreCase) >= 0)) ||
               line.IndexOf("couldn't run mojosetup", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("xterm", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("option", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeRuntimeCrashError(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.IndexOf("Unhandled exception code", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("EXCEPTION_ACCESS_VIOLATION", StringComparison.OrdinalIgnoreCase) >= 0 ||
               line.IndexOf("NtRaiseException", StringComparison.OrdinalIgnoreCase) >= 0;
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

    private static string FormatProcessCommand(ProcessStartInfo startInfo)
    {
        if (startInfo.ArgumentList.Count == 0)
            return startInfo.FileName ?? string.Empty;

        var builder = new StringBuilder(startInfo.FileName ?? string.Empty);
        foreach (var argument in startInfo.ArgumentList)
        {
            builder.Append(' ');
            builder.Append(QuoteArgumentIfNeeded(argument));
        }

        return builder.ToString();
    }

    private string ResolveOrCreatePrefixRoot(MediaItem item, string storeGameId)
    {
        string absolutePrefixPath;
        if (!string.IsNullOrWhiteSpace(item.PrefixPath))
        {
            absolutePrefixPath = Path.IsPathRooted(item.PrefixPath)
                ? Path.GetFullPath(item.PrefixPath)
                : Path.Combine(AppPaths.LibraryRoot, item.PrefixPath);
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

    private static string? ResolveRunnerExecutablePath(GogInstallDialogViewModel.RunnerOption runner)
    {
        var configuredPath = runner.Path?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var resolvedPath = Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : AppPaths.ResolveDataPath(configuredPath);

        if (File.Exists(resolvedPath))
            return resolvedPath;

        if (!Directory.Exists(resolvedPath))
            return null;

        if (runner.Kind == RunnerVersionKind.Wine)
        {
            var binWine = Path.Combine(resolvedPath, "bin", "wine");
            if (File.Exists(binWine))
                return binWine;

            var rootWine = Path.Combine(resolvedPath, "wine");
            if (File.Exists(rootWine))
                return rootWine;
        }
        else
        {
            var proton = Path.Combine(resolvedPath, "proton");
            if (File.Exists(proton))
                return proton;
        }

        return null;
    }

    private bool ApplyDetectedGogLaunchConfiguration(
        MediaItem item,
        string storeGameId,
        GogInstallDialogViewModel.GogInstallDialogResult request,
        DetectedGogLaunchInfo launchInfo)
    {
        if (string.IsNullOrWhiteSpace(launchInfo.ExecutablePath))
            return false;

        if (request.Platform == GogInstallPlatform.Linux)
            EnsureLinuxExecutableBitBestEffort(launchInfo.ExecutablePath);

        item.MediaType = MediaType.Native;

        var storedFilePath = launchInfo.ExecutablePath;
        var storedFileKind = MediaFileKind.Absolute;
        if (_currentSettings.PreferPortableLaunchPaths &&
            PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(launchInfo.ExecutablePath, out var relativeExecutable))
        {
            storedFilePath = relativeExecutable;
            storedFileKind = MediaFileKind.LibraryRelative;
        }

        item.Files = new List<MediaFileRef>
        {
            new()
            {
                Kind = storedFileKind,
                Path = storedFilePath,
                Index = 1
            }
        };

        item.WorkingDirectory = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(launchInfo.WorkingDirectory);

        if (request.Platform == GogInstallPlatform.Windows)
        {
            if (request.Runner == null)
                return false;

            var baseArgs = "{file}";
            if (request.Runner.Kind == RunnerVersionKind.Proton)
            {
                if (!IsUmuRunAvailable())
                    return false;

                item.LauncherPath = "umu-run";
            }
            else
            {
                var runnerExecutable = ResolveRunnerExecutablePath(request.Runner);
                if (string.IsNullOrWhiteSpace(runnerExecutable))
                    return false;

                var launcherPath = PortablePathHelper.ConvertPathToPortableIfInsideDataRootPreserveEmpty(runnerExecutable)
                                   ?? runnerExecutable;
                item.LauncherPath = launcherPath;
            }

            item.LauncherArgs = string.IsNullOrWhiteSpace(launchInfo.LaunchArguments)
                ? baseArgs
                : LaunchArgumentHelper.NormalizeWhitespace($"{baseArgs} {launchInfo.LaunchArguments}");
            item.RunnerVersionId = request.Runner.Id;

            if (string.IsNullOrWhiteSpace(item.PrefixPath))
            {
                var safeTitle = PrefixPathHelper.SanitizePrefixFolderName(item.Title);
                var folderName = string.IsNullOrWhiteSpace(safeTitle)
                    ? $"gog_{storeGameId}"
                    : $"gog_{storeGameId}_{safeTitle}";
                item.PrefixPath = Path.Combine("Prefixes", folderName);
            }
        }
        else
        {
            item.LauncherPath = null;
            item.LauncherArgs = null;
            item.RunnerVersionId = null;
            item.PrefixPath = null;
        }

        return true;
    }

    private static void EnsureLinuxExecutableBitBestEffort(string executablePath)
    {
        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            return;
        }

        try
        {
            var currentMode = File.GetUnixFileMode(executablePath);
            var withExec = currentMode |
                           UnixFileMode.UserExecute |
                           UnixFileMode.GroupExecute |
                           UnixFileMode.OtherExecute;
            if (withExec != currentMode)
                File.SetUnixFileMode(executablePath, withExec);
        }
        catch
        {
            // best-effort
        }
    }

    private static void EnsureLinuxInstalledExecutablePermissionsBestEffort(string installRoot)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return;

        IEnumerable<string> allFiles;
        try
        {
            allFiles = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return;
        }

        foreach (var filePath in allFiles)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || IsInsideInstallerStaging(filePath))
                continue;

            if (!ShouldEnsureLinuxExecutableBit(filePath))
                continue;

            EnsureLinuxExecutableBitBestEffort(filePath);
        }
    }

    private static bool ShouldEnsureLinuxExecutableBit(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (fileName.Equals("start.sh", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension.Equals(".sh", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".run", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".x86", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".x86_64", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".AppImage", StringComparison.OrdinalIgnoreCase);
        }

        return LooksLikeElfBinary(filePath);
    }

    private static bool LooksLikeElfBinary(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 4)
                return false;

            Span<byte> magic = stackalloc byte[4];
            var read = stream.Read(magic);
            return read == 4 &&
                   magic[0] == 0x7F &&
                   magic[1] == (byte)'E' &&
                   magic[2] == (byte)'L' &&
                   magic[3] == (byte)'F';
        }
        catch
        {
            return false;
        }
    }

    private async Task<DetectedGogLaunchInfo?> DetectGogLaunchInfoAsync(
        MediaItem item,
        string selectedInstallPath,
        string storeGameId,
        GogInstallPlatform platform)
    {
        var fromLocalInfo = TryDetectGogLaunchInfo(selectedInstallPath, storeGameId, platform);
        if (fromLocalInfo != null && File.Exists(fromLocalInfo.ExecutablePath))
            return platform == GogInstallPlatform.Linux
                ? PreferLinuxStartScriptIfAvailable(fromLocalInfo, selectedInstallPath)
                : fromLocalInfo;

        try
        {
            var playTasks = await _gogInstallService.GetGameDetailsPlayTasksAsync(storeGameId).ConfigureAwait(false);
            var fromPlayTasks = TryDetectGogLaunchInfoFromPlayTasks(selectedInstallPath, platform, playTasks);
            if (fromPlayTasks != null && File.Exists(fromPlayTasks.ExecutablePath))
                return platform == GogInstallPlatform.Linux
                    ? PreferLinuxStartScriptIfAvailable(fromPlayTasks, selectedInstallPath)
                    : fromPlayTasks;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] playTasks fallback detection failed: {ex.Message}");
        }

        var fromFilesystemFallback = TryDetectGogLaunchInfoByFilesystem(item, selectedInstallPath, platform);
        if (fromFilesystemFallback != null && File.Exists(fromFilesystemFallback.ExecutablePath))
            return platform == GogInstallPlatform.Linux
                ? PreferLinuxStartScriptIfAvailable(fromFilesystemFallback, selectedInstallPath)
                : fromFilesystemFallback;

        return null;
    }

    private static DetectedGogLaunchInfo PreferLinuxStartScriptIfAvailable(
        DetectedGogLaunchInfo launchInfo,
        string selectedInstallPath)
    {
        if (string.IsNullOrWhiteSpace(selectedInstallPath) || !Directory.Exists(selectedInstallPath))
            return launchInfo;

        var directCandidates = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(launchInfo.InstallRoot))
            directCandidates.Add(Path.Combine(launchInfo.InstallRoot, "start.sh"));
        directCandidates.Add(Path.Combine(selectedInstallPath, "start.sh"));

        var preferred = directCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(path => File.Exists(path) && !IsInsideInstallerStaging(path));

        if (string.IsNullOrWhiteSpace(preferred))
        {
            preferred = EnumerateFilesSafe(selectedInstallPath, "start.sh")
                .Where(path => File.Exists(path) && !IsInsideInstallerStaging(path))
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(preferred))
            return launchInfo;

        var workingDirectory = Path.GetDirectoryName(preferred);
        var installRoot = !string.IsNullOrWhiteSpace(selectedInstallPath)
            ? selectedInstallPath
            : launchInfo.InstallRoot;

        return new DetectedGogLaunchInfo(
            preferred,
            null,
            workingDirectory,
            installRoot);
    }

    private async Task<DetectedGogLaunchInfo?> PromptForManualExecutableFallbackAsync(
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

        return new DetectedGogLaunchInfo(
            executablePath,
            null,
            workingDirectory,
            installRoot);
    }

    private static DetectedGogLaunchInfo? TryDetectGogLaunchInfo(
        string selectedInstallPath,
        string storeGameId,
        GogInstallPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(selectedInstallPath) || !Directory.Exists(selectedInstallPath))
            return null;

        var infoFiles = EnumerateFilesSafe(selectedInstallPath, "goggame-*.info");
        var candidates = new List<DetectedGogLaunchInfo>();
        var weakCandidates = new List<DetectedGogLaunchInfo>();

        foreach (var infoFile in infoFiles)
        {
            var parsed = TryParseLaunchInfoFromGogInfoFile(infoFile, platform);
            if (parsed == null)
                continue;

            var rootGameId = parsed.Value.RootGameId;
            var launchInfo = parsed.Value.LaunchInfo;
            if (string.Equals(rootGameId, storeGameId, StringComparison.OrdinalIgnoreCase))
                candidates.Add(launchInfo);
            else
                weakCandidates.Add(launchInfo);
        }

        var firstValid = candidates.FirstOrDefault(c => File.Exists(c.ExecutablePath))
                         ?? weakCandidates.FirstOrDefault(c => File.Exists(c.ExecutablePath));
        if (firstValid != null)
            return firstValid;

        if (platform == GogInstallPlatform.Linux)
        {
            var fallbackStartScript = EnumerateFilesSafe(selectedInstallPath, "start.sh").FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fallbackStartScript))
            {
                var installRoot = Directory.GetParent(fallbackStartScript)?.FullName ?? selectedInstallPath;
                return new DetectedGogLaunchInfo(
                    fallbackStartScript,
                    null,
                    installRoot,
                    installRoot);
            }
        }

        return candidates.FirstOrDefault() ?? weakCandidates.FirstOrDefault();
    }

    private static DetectedGogLaunchInfo? TryDetectGogLaunchInfoFromPlayTasks(
        string installRoot,
        GogInstallPlatform platform,
        IReadOnlyList<GogPlayTaskInfo> playTasks)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return null;

        if (playTasks.Count == 0)
            return null;

        foreach (var task in playTasks.OrderByDescending(t => t.IsPrimary))
        {
            if (string.IsNullOrWhiteSpace(task.Path))
                continue;

            var executablePath = ResolveExecutablePath(installRoot, task.Path, platform);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                continue;

            if (platform == GogInstallPlatform.Windows &&
                !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var workingDirectory = ResolveWorkingDirectory(installRoot, task.WorkingDirectory) ??
                                   Path.GetDirectoryName(executablePath);
            return new DetectedGogLaunchInfo(
                executablePath,
                task.Arguments,
                workingDirectory,
                installRoot);
        }

        return null;
    }

    private static DetectedGogLaunchInfo? TryDetectGogLaunchInfoByFilesystem(
        MediaItem item,
        string installRoot,
        GogInstallPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return null;

        return platform == GogInstallPlatform.Windows
            ? TryDetectWindowsLaunchByFilesystem(item, installRoot)
            : TryDetectLinuxLaunchByFilesystem(item, installRoot);
    }

    private static DetectedGogLaunchInfo? TryDetectLinuxLaunchByFilesystem(MediaItem item, string installRoot)
    {
        var startScript = EnumerateFilesSafe(installRoot, "start.sh")
            .FirstOrDefault(path => File.Exists(path) && !IsInsideInstallerStaging(path));
        if (!string.IsNullOrWhiteSpace(startScript))
        {
            var workingDirectory = Path.GetDirectoryName(startScript);
            return new DetectedGogLaunchInfo(
                startScript,
                null,
                workingDirectory,
                installRoot);
        }

        var candidates = new List<string>();
        foreach (var pattern in new[] { "*.sh", "*.x86_64", "*.x86", "*.AppImage" })
        {
            foreach (var file in EnumerateFilesSafe(installRoot, pattern))
            {
                if (!File.Exists(file) || IsInsideInstallerStaging(file))
                    continue;

                candidates.Add(file);
            }
        }

        var normalizedTitle = NormalizeForComparison(item.Title);
        var best = candidates
            .OrderByDescending(path => ScoreLinuxExecutable(path, normalizedTitle))
            .ThenBy(path => path.Length)
            .FirstOrDefault(path => ScoreLinuxExecutable(path, normalizedTitle) > int.MinValue);

        if (string.IsNullOrWhiteSpace(best))
            return null;

        return new DetectedGogLaunchInfo(
            best,
            null,
            Path.GetDirectoryName(best),
            installRoot);
    }

    private static DetectedGogLaunchInfo? TryDetectWindowsLaunchByFilesystem(MediaItem item, string installRoot)
    {
        var normalizedTitle = NormalizeForComparison(item.Title);
        var best = EnumerateFilesSafe(installRoot, "*.exe")
            .Where(File.Exists)
            .Where(path => !IsInsideInstallerStaging(path))
            .Select(path => new { Path = path, Score = ScoreWindowsExecutable(path, normalizedTitle) })
            .Where(entry => entry.Score > int.MinValue)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Path.Length)
            .Select(entry => entry.Path)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(best))
            return null;

        return new DetectedGogLaunchInfo(
            best,
            null,
            Path.GetDirectoryName(best),
            installRoot);
    }

    private static int ScoreWindowsExecutable(string path, string normalizedTitle)
    {
        var fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName))
            return int.MinValue;

        if (fileName.Contains("unins", StringComparison.Ordinal) ||
            fileName.Contains("uninstall", StringComparison.Ordinal) ||
            fileName.Contains("setup", StringComparison.Ordinal) ||
            fileName.Contains("install", StringComparison.Ordinal) ||
            fileName.Contains("vcredist", StringComparison.Ordinal) ||
            fileName.Contains("dxsetup", StringComparison.Ordinal))
        {
            return int.MinValue;
        }

        var score = 0;
        var normalizedFileName = NormalizeForComparison(fileName);
        if (!string.IsNullOrWhiteSpace(normalizedTitle) &&
            !string.IsNullOrWhiteSpace(normalizedFileName) &&
            normalizedFileName.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 8;
        }

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant();
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}game{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 3;
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 2;
        if (fileName.Contains("launcher", StringComparison.Ordinal))
            score += 1;
        if (normalizedPath.Contains("support", StringComparison.Ordinal) ||
            normalizedPath.Contains("redist", StringComparison.Ordinal) ||
            normalizedPath.Contains("directx", StringComparison.Ordinal))
        {
            score -= 10;
        }

        return score;
    }

    private static int ScoreLinuxExecutable(string path, string normalizedTitle)
    {
        var fileName = Path.GetFileName(path).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fileName))
            return int.MinValue;

        if (fileName.Contains("uninstall", StringComparison.Ordinal) ||
            fileName.Contains("installer", StringComparison.Ordinal))
        {
            return int.MinValue;
        }

        var score = 0;
        if (fileName.Equals("start.sh", StringComparison.Ordinal))
            score += 8;

        var normalizedFileName = NormalizeForComparison(Path.GetFileNameWithoutExtension(fileName));
        if (!string.IsNullOrWhiteSpace(normalizedTitle) &&
            !string.IsNullOrWhiteSpace(normalizedFileName) &&
            normalizedFileName.Contains(normalizedTitle, StringComparison.Ordinal))
        {
            score += 6;
        }

        var normalizedPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToLowerInvariant();
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}game{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 2;
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            score += 1;

        return score;
    }

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return chars.Length == 0 ? string.Empty : new string(chars);
    }

    private static bool IsInsideInstallerStaging(string path)
        => path.IndexOf(".retromind-gog-installers", StringComparison.OrdinalIgnoreCase) >= 0;

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static (string? RootGameId, DetectedGogLaunchInfo LaunchInfo)? TryParseLaunchInfoFromGogInfoFile(
        string infoFilePath,
        GogInstallPlatform platform)
    {
        try
        {
            var jsonText = File.ReadAllText(infoFilePath);
            if (string.IsNullOrWhiteSpace(jsonText))
                return null;

            using var json = JsonDocument.Parse(jsonText);
            var root = json.RootElement;

            var rootGameId = GetJsonString(root, "rootGameId") ?? GetJsonString(root, "gameId");
            if (!root.TryGetProperty("playTasks", out var playTasks) || playTasks.ValueKind != JsonValueKind.Array)
                return null;

            if (!TryGetPrimaryPlayTask(playTasks, out var task))
                return null;

            var relativeExecutable = GetJsonString(task, "path");
            if (string.IsNullOrWhiteSpace(relativeExecutable))
                return null;

            var infoFolder = Directory.GetParent(infoFilePath)?.FullName;
            if (string.IsNullOrWhiteSpace(infoFolder))
                return null;

            var installRoot = infoFolder;
            if (string.Equals(Path.GetFileName(installRoot), "game", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(installRoot)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                    installRoot = parent;
            }

            var executablePath = ResolveExecutablePath(installRoot, relativeExecutable, platform);
            var launchArgs = ParseTaskArguments(task);
            var workingDirectory = ResolveWorkingDirectory(installRoot, GetJsonString(task, "workingDir"));

            return (rootGameId, new DetectedGogLaunchInfo(executablePath, launchArgs, workingDirectory, installRoot));
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveExecutablePath(string installRoot, string relativeExecutable, GogInstallPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(relativeExecutable))
            return string.Empty;

        var normalizedRelative = relativeExecutable
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalizedRelative))
        {
            var rootedPath = Path.GetFullPath(normalizedRelative);
            if (File.Exists(rootedPath))
                return rootedPath;
        }

        if (LooksLikeWindowsAbsolutePath(relativeExecutable))
        {
            normalizedRelative = relativeExecutable[2..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        var direct = Path.Combine(installRoot, normalizedRelative);
        if (File.Exists(direct))
            return direct;

        if (platform == GogInstallPlatform.Linux)
        {
            var gameRelative = Path.Combine(installRoot, "game", normalizedRelative);
            if (File.Exists(gameRelative))
                return gameRelative;

            return gameRelative;
        }

        var fileName = Path.GetFileName(normalizedRelative);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var byFileName = EnumerateFilesSafe(installRoot, fileName).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(byFileName))
                return byFileName;
        }

        return direct;
    }

    private static string? ResolveWorkingDirectory(string installRoot, string? relativeWorkingDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeWorkingDirectory))
            return null;

        var normalized = relativeWorkingDirectory
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(normalized))
            return normalized;

        if (LooksLikeWindowsAbsolutePath(relativeWorkingDirectory))
        {
            normalized = relativeWorkingDirectory[2..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return Path.Combine(installRoot, normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static bool LooksLikeWindowsAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3)
            return false;

        return char.IsLetter(path[0]) &&
               path[1] == ':' &&
               (path[2] == '\\' || path[2] == '/');
    }

    private static bool TryGetPrimaryPlayTask(JsonElement playTasks, out JsonElement task)
    {
        task = default;

        foreach (var candidate in playTasks.EnumerateArray())
        {
            if (candidate.TryGetProperty("isPrimary", out var isPrimary) && isPrimary.ValueKind == JsonValueKind.True)
            {
                task = candidate;
                return true;
            }
        }

        foreach (var candidate in playTasks.EnumerateArray())
        {
            task = candidate;
            return true;
        }

        return false;
    }

    private static string? ParseTaskArguments(JsonElement task)
    {
        if (!task.TryGetProperty("arguments", out var args))
            return null;

        if (args.ValueKind == JsonValueKind.String)
            return args.GetString();

        if (args.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var value in args.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var arg = value.GetString();
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            parts.Add(QuoteArgumentIfNeeded(arg));
        }

        if (parts.Count == 0)
            return null;

        return string.Join(' ', parts);
    }

    private static string QuoteArgumentIfNeeded(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;

        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }
}

