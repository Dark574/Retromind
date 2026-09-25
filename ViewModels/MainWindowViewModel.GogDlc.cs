using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private async Task ShowGogDlcManagerAsync(MediaItem item, Window owner)
    {
        var gameId = GogMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(gameId))
            return;

        var installedPlatform = TryResolveInstalledGogPath(item, out _)
            ? GetPreferredInstalledGogPlatform(item)
            : null;
        var dialog = new GogDlcDialogView();
        var viewModel = new GogDlcDialogViewModel(
            _gogInstallService,
            gameId,
            installedPlatform,
            item.GogDlcInstallations,
            (entries, ct) => InstallGogDlcsAsync(item, entries, dialog, ct),
            hasUpdate => SetGogDlcUpdateAvailability(item, hasUpdate));
        dialog.DataContext = viewModel;
        await dialog.ShowDialog(owner);
    }

    private async Task<GogDlcInstallBatchResult> InstallGogDlcsAsync(
        MediaItem item,
        IReadOnlyList<GogDlcCatalogEntry> entries,
        Window owner,
        CancellationToken dialogCancellationToken,
        ProcessLogViewModel? existingProgressLog = null,
        bool deleteStagingAfterSuccess = true)
    {
        if (entries.Count == 0)
            return new GogDlcInstallBatchResult(new HashSet<string>(), string.Empty);

        var baseGameId = GogMediaItemStateHelper.TryGetGameId(item);
        var platform = GetPreferredInstalledGogPlatform(item);
        if (string.IsNullOrWhiteSpace(baseGameId) ||
            !platform.HasValue ||
            !TryResolveInstalledGogPath(item, out var installPath))
        {
            return CreateGogDlcInstallError(
                T("Gog.Dlc.MainGameRequired", "Install the main game before installing DLCs."));
        }

        if (platform == GogInstallPlatform.Windows &&
            string.IsNullOrWhiteSpace(EmulatorResolverHelper.ResolveSystemWine()))
        {
            return CreateGogDlcInstallError(
                T("Gog.Dlc.SystemWineRequired", "Installing or updating Windows DLCs requires system Wine."));
        }

        if (!await EnsureGogSignInForInstallAsync(owner))
        {
            return CreateGogDlcInstallError(
                T("Gog.Dlc.SignInRequired", "DLC installation or update requires a GOG sign-in."));
        }

        var request = CreateGogDlcInstallRequest(
            installPath,
            platform.Value,
            GetPreferredInstalledWindowsInstallerPreference(item) ??
                GogInstallDialogViewModel.WindowsInstallerPreference.AutoPrefer64,
            deleteStagingAfterSuccess);

        var ownsProgressLog = existingProgressLog == null;
        var progressLogVm = existingProgressLog;
        if (progressLogVm == null)
        {
            var progressTitle = string.Format(
                CultureInfo.CurrentCulture,
                T("Gog.Dlc.ProgressTitleFormat", "GOG DLC installation - {0}"),
                string.IsNullOrWhiteSpace(item.Title) ? "GOG" : item.Title);
            progressLogVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
            var progressLogView = new ProcessLogView { DataContext = progressLogVm };
            await UiThreadHelper.InvokeAsync(() => progressLogView.Show(owner));
            progressLogVm.EnableCancel();
        }

        var installedProductIds = new HashSet<string>(StringComparer.Ordinal);
        var installedCount = 0;
        var updatedCount = 0;
        var failureMessages = new List<string>();
        var wasCancelled = false;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            dialogCancellationToken,
            progressLogVm.Token);
        var ct = linkedCancellation.Token;

        try
        {
            if (platform == GogInstallPlatform.Linux)
            {
                try
                {
                    var repairedFiles = GogLinuxInstallRelocationRepair.RepairFromManifest(installPath);
                    if (repairedFiles > 0)
                    {
                        AppendProcessLog(
                            progressLogVm,
                            string.Format(
                                CultureInfo.CurrentCulture,
                                T("Gog.Dlc.LinuxMetadataRepairedFormat", "Repaired {0:N0} relocated Linux installer metadata file(s)."),
                                repairedFiles));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GOG] Failed to repair Linux installer metadata before DLC installation: {ex.Message}");
                    var failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.LinuxMetadataRepairFailedFormat", "The Linux installation metadata could not be repaired: {0}"),
                        BuildShortErrorDetail(ex));
                    AppendProcessLog(progressLogVm, failureMessage);
                    return new GogDlcInstallBatchResult(installedProductIds, failureMessage);
                }
            }

            for (var index = 0; index < entries.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[index];
                var isUpdate = entry.IsInstalled;
                AppendProcessLog(
                    progressLogVm,
                    $"[DLC {index + 1}/{entries.Count}] {(isUpdate ? "Update" : "Install")} {entry.Title} ({entry.ProductId})");

                GogInstallerPackage? installerPackage;
                try
                {
                    installerPackage = await _gogInstallService.ResolveInstallerPackageAsync(
                        entry.ProductId,
                        platform.Value,
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[GOG] Failed to resolve DLC installer '{entry.ProductId}': {ex.Message}");
                    var failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.ResolveFailedFormat", "The installer for '{0}' could not be loaded: {1}"),
                        entry.Title,
                        BuildShortErrorDetail(ex));
                    failureMessages.Add(failureMessage);
                    AppendProcessLog(progressLogVm, failureMessage);
                    continue;
                }

                if (installerPackage == null)
                {
                    var failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.NoInstallerForPlatformFormat", "No installer for the installed platform is currently available for '{0}'."),
                        entry.Title);
                    failureMessages.Add(failureMessage);
                    AppendProcessLog(progressLogVm, failureMessage);
                    continue;
                }

                var platformFolder = platform == GogInstallPlatform.Windows ? "windows" : "linux";
                var stagingRoot = Path.Combine(
                    installPath,
                    ".retromind-gog-installers",
                    "dlc",
                    entry.ProductId,
                    platformFolder);

                GogDownloadedInstallerPackage downloadedPackage;
                try
                {
                    var lastLoggedFileIndex = -1;
                    var lastLoggedFilePercent = -1;
                    var lastLoggedOverallPercent = -1;
                    var lastLoggedAtUtc = DateTimeOffset.MinValue;
                    var downloadProgress = new Progress<GogInstallerDownloadProgress>(progress =>
                    {
                        var now = DateTimeOffset.UtcNow;
                        var currentFilePercent = CalculateProgressPercent(
                            progress.BytesDownloadedCurrentFile,
                            progress.BytesTotalCurrentFile);
                        var overallPercent = CalculateProgressPercent(
                            progress.BytesDownloadedOverall,
                            progress.BytesTotalOverall);
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
                    downloadedPackage = await _gogInstallService.DownloadInstallerPackageAsync(
                        installerPackage,
                        stagingRoot,
                        downloadProgress,
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[GOG] Failed to download DLC installer '{entry.ProductId}': {ex.Message}");
                    var failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.DownloadFailedFormat", "The installer for '{0}' could not be downloaded: {1}"),
                        entry.Title,
                        BuildShortErrorDetail(ex));
                    failureMessages.Add(failureMessage);
                    AppendProcessLog(progressLogVm, failureMessage);
                    continue;
                }

                var runResult = await RunInstallerAsync(
                    item,
                    baseGameId,
                    request,
                    downloadedPackage,
                    progressLogVm,
                    ct,
                    useTemporaryLinuxDestination: false,
                    requireLinuxPayloadChange: platform == GogInstallPlatform.Linux);
                if (!runResult.Success)
                {
                    var failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.RunFailedFormat", "The installer for '{0}' failed: {1}"),
                        entry.Title,
                        runResult.ErrorMessage ?? T("Gog.Install.RunFailed", "Installer execution failed."));
                    failureMessages.Add(failureMessage);
                    AppendProcessLog(progressLogVm, failureMessage);
                    continue;
                }

                UpdateInstalledGogDlcState(item, entry, platform.Value, installerPackage);
                _libraryTracker.MarkDirty();
                await SaveData();
                installedProductIds.Add(entry.ProductId);
                if (isUpdate)
                    updatedCount++;
                else
                    installedCount++;

                if (request.DeleteStagingAfterSuccess)
                    TryDeleteGogStagingDirectoryBestEffort(downloadedPackage.StagingDirectory);

                AppendProcessLog(
                    progressLogVm,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        isUpdate
                            ? T("Gog.Dlc.UpdatedFormat", "Updated: {0}")
                            : T("Gog.Dlc.InstalledFormat", "Installed: {0}"),
                        entry.Title));
            }
        }
        catch (OperationCanceledException)
        {
            progressLogVm.MarkCancelled(T("Gog.Install.Cancelled", "Installation cancelled by user."));
            wasCancelled = true;
        }
        finally
        {
            if (ownsProgressLog)
            {
                progressLogVm.MarkFinished();
                UiThreadHelper.Post(() => progressLogVm.IsRunning = false);
            }
        }

        if (wasCancelled)
            return new GogDlcInstallBatchResult(
                installedProductIds,
                T("Gog.Install.Cancelled", "Installation cancelled by user."));

        if (failureMessages.Count > 0)
        {
            var partialMessage = string.Format(
                CultureInfo.CurrentCulture,
                T(
                    "Gog.Dlc.PartialSuccessFormat",
                    "{0:N0} DLC(s) completed; {1:N0} failed. See the installation log for details."),
                installedCount + updatedCount,
                failureMessages.Count);
            AppendProcessLog(progressLogVm, partialMessage);
            return new GogDlcInstallBatchResult(installedProductIds, partialMessage);
        }

        var successMessage = installedCount > 0 && updatedCount > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                T("Gog.Dlc.MixedSuccessFormat", "Successfully installed {0:N0} and updated {1:N0} DLC(s)."),
                installedCount,
                updatedCount)
            : updatedCount > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    T("Gog.Dlc.UpdateSuccessFormat", "Successfully updated {0:N0} DLC(s)."),
                    updatedCount)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    T("Gog.Dlc.InstallSuccessFormat", "Successfully installed {0:N0} DLC(s)."),
                    installedCount);
        AppendProcessLog(progressLogVm, successMessage);
        return new GogDlcInstallBatchResult(installedProductIds, successMessage);
    }

    private async Task ReapplyInstalledGogDlcsAsync(
        MediaItem item,
        IReadOnlyList<GogDlcInstallationState> previouslyInstalledDlcs,
        Window owner,
        ProcessLogViewModel progressLogVm,
        bool deleteStagingAfterSuccess)
    {
        var gameId = GogMediaItemStateHelper.TryGetGameId(item);
        var platform = GetPreferredInstalledGogPlatform(item);
        if (string.IsNullOrWhiteSpace(gameId) || !platform.HasValue)
        {
            SetGogDlcUpdateAvailability(item, true);
            AppendProcessLog(
                progressLogVm,
                T(
                    "Gog.Dlc.ReapplyMissingInstallState",
                    "[DLC] The main game installation completed, but its installed DLCs could not be reapplied because the installation state is incomplete."));
            return;
        }

        AppendProcessLog(
            progressLogVm,
            string.Format(
                CultureInfo.CurrentCulture,
                T(
                    "Gog.Dlc.ReapplyPreparingFormat",
                    "[DLC] Preparing to reapply {0:N0} installed DLC(s)..."),
                previouslyInstalledDlcs.Count));

        IReadOnlyList<GogDlcCatalogItem> catalog;
        try
        {
            catalog = await _gogInstallService.GetOwnedDlcCatalogAsync(gameId, progressLogVm.Token);
        }
        catch (OperationCanceledException)
        {
            SetGogDlcUpdateAvailability(item, true);
            AppendProcessLog(
                progressLogVm,
                T("Gog.Dlc.ReapplyCancelled", "[DLC] Automatic DLC reinstallation was cancelled."));
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Failed to prepare automatic DLC updates for '{item.Title}': {ex.Message}");
            SetGogDlcUpdateAvailability(item, true);
            AppendProcessLog(
                progressLogVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Gog.Dlc.ReapplyCatalogFailedFormat",
                        "[DLC] The main game installation completed, but the installed DLCs could not be prepared for reinstallation: {0}"),
                    BuildShortErrorDetail(ex)));
            return;
        }

        var plan = GogDlcUpdateComparer.CreateReapplyPlan(
            previouslyInstalledDlcs,
            catalog,
            platform.Value);
        foreach (var unavailable in plan.Unavailable)
        {
            AppendProcessLog(
                progressLogVm,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Gog.Dlc.ReapplyUnavailableFormat",
                        "[DLC] No current installer is available for installed DLC '{0}'."),
                    string.IsNullOrWhiteSpace(unavailable.Title) ? unavailable.ProductId : unavailable.Title));
        }

        var linuxText = T("Gog.Install.PlatformLinux", "Linux");
        var windowsText = T("Gog.Install.PlatformWindows", "Windows");
        var noInstallerText = T("Gog.Dlc.NoSeparateInstaller", "Currently no Linux/Windows offline installer");
        var upToDateText = T("Gog.Dlc.UpToDate", "Up to date");
        var updateAvailableText = T("Gog.Dlc.UpdateAvailable", "Update available");
        var updateUnknownText = T("Gog.Dlc.UpdateUnknown", "Installed · update status unknown");
        var entries = plan.Targets
            .Select(target => new GogDlcCatalogEntry(
                target.CatalogItem,
                linuxText,
                windowsText,
                noInstallerText,
                upToDateText,
                updateAvailableText,
                updateUnknownText,
                platform.Value,
                isInstalled: true,
                installedState: target.Installed))
            .ToArray();

        var result = entries.Length > 0
            ? await InstallGogDlcsAsync(
                item,
                entries,
                owner,
                progressLogVm.Token,
                progressLogVm,
                deleteStagingAfterSuccess)
            : new GogDlcInstallBatchResult(new HashSet<string>(), string.Empty);

        var updatedCount = plan.Targets.Count(target =>
            result.InstalledProductIds.Contains(target.CatalogItem.ProductId));
        var allDlcsUpdated = updatedCount == plan.Targets.Count && plan.Unavailable.Count == 0;
        SetGogDlcUpdateAvailability(item, !allDlcsUpdated);
        await SaveData();

        AppendProcessLog(
            progressLogVm,
            allDlcsUpdated
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Gog.Dlc.ReapplySuccessFormat",
                        "[DLC] All {0:N0} installed DLC(s) were updated successfully."),
                    updatedCount)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    T(
                        "Gog.Dlc.ReapplyPartialFormat",
                        "[DLC] {0:N0} of {1:N0} installed DLC(s) were updated. The GOG update indicator remains active."),
                    updatedCount,
                    plan.Targets.Count + plan.Unavailable.Count));
    }

    private static bool TryResolveInstalledGogPath(MediaItem item, out string installPath)
    {
        installPath = string.Empty;
        return item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var storedPath) &&
               GogInstallPathHelper.TryResolveStoredPath(storedPath, out installPath) &&
               Directory.Exists(installPath);
    }

    private static void UpdateInstalledGogDlcState(
        MediaItem item,
        GogDlcCatalogEntry entry,
        GogInstallPlatform platform,
        GogInstallerPackage package)
    {
        var states = item.GogDlcInstallations?
            .Where(state => !string.Equals(state.ProductId, entry.ProductId, StringComparison.Ordinal))
            .ToList() ?? new List<GogDlcInstallationState>();

        states.Add(new GogDlcInstallationState
        {
            ProductId = entry.ProductId,
            Title = entry.Title,
            Platform = platform == GogInstallPlatform.Windows ? "windows" : "linux",
            InstalledVersion = NormalizeGogVersion(package.Version),
            InstalledInstallerSignature = !string.IsNullOrWhiteSpace(entry.CurrentInstallerMetadata?.Signature)
                ? entry.CurrentInstallerMetadata.Signature
                : BuildInstallerSignature(package)
        });
        item.GogDlcInstallations = states;
    }

    internal static GogInstallDialogViewModel.GogInstallDialogResult CreateGogDlcInstallRequest(
        string installPath,
        GogInstallPlatform platform,
        GogInstallDialogViewModel.WindowsInstallerPreference windowsInstallerPreference,
        bool deleteStagingAfterSuccess) =>
        new(
            installPath,
            platform,
            Runner: null,
            WindowsInstallerPreference: windowsInstallerPreference,
            CreateDesktopShortcut: false,
            CreateStartMenuShortcuts: false,
            CleanInstall: false,
            DeleteStagingAfterSuccess: deleteStagingAfterSuccess);

    private static GogDlcInstallBatchResult CreateGogDlcInstallError(string message) =>
        new(new HashSet<string>(), message);
}
