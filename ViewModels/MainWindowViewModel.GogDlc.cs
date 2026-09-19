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
            (entries, ct) => InstallGogDlcsAsync(item, entries, dialog, ct));
        dialog.DataContext = viewModel;
        await dialog.ShowDialog(owner);
    }

    private async Task<GogDlcInstallBatchResult> InstallGogDlcsAsync(
        MediaItem item,
        IReadOnlyList<GogDlcCatalogEntry> entries,
        Window owner,
        CancellationToken dialogCancellationToken)
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
                T("Gog.Dlc.SystemWineRequired", "Installing Windows DLCs requires system Wine."));
        }

        if (!await EnsureGogSignInForInstallAsync(owner))
        {
            return CreateGogDlcInstallError(
                T("Gog.Dlc.SignInRequired", "DLC installation requires a GOG sign-in."));
        }

        var request = new GogInstallDialogViewModel.GogInstallDialogResult(
            installPath,
            platform.Value,
            Runner: null,
            WindowsInstallerPreference: GetPreferredInstalledWindowsInstallerPreference(item) ??
                GogInstallDialogViewModel.WindowsInstallerPreference.AutoPrefer64,
            CreateDesktopShortcut: false,
            CreateStartMenuShortcuts: false,
            CleanInstall: false,
            DeleteStagingAfterSuccess: true);

        var progressTitle = string.Format(
            CultureInfo.CurrentCulture,
            T("Gog.Dlc.ProgressTitleFormat", "GOG DLC installation - {0}"),
            string.IsNullOrWhiteSpace(item.Title) ? "GOG" : item.Title);
        var progressLogVm = new ProcessLogViewModel(progressTitle, newestFirst: true);
        var progressLogView = new ProcessLogView { DataContext = progressLogVm };
        await UiThreadHelper.InvokeAsync(() => progressLogView.Show(owner));
        progressLogVm.EnableCancel();

        var installedProductIds = new HashSet<string>(StringComparer.Ordinal);
        string? failureMessage = null;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            dialogCancellationToken,
            progressLogVm.Token);
        var ct = linkedCancellation.Token;

        try
        {
            for (var index = 0; index < entries.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[index];
                AppendProcessLog(
                    progressLogVm,
                    $"[DLC {index + 1}/{entries.Count}] {entry.Title} ({entry.ProductId})");

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
                    failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.ResolveFailedFormat", "The installer for '{0}' could not be loaded: {1}"),
                        entry.Title,
                        BuildShortErrorDetail(ex));
                    AppendProcessLog(progressLogVm, failureMessage);
                    break;
                }

                if (installerPackage == null)
                {
                    failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.NoInstallerForPlatformFormat", "No installer for the installed platform is currently available for '{0}'."),
                        entry.Title);
                    AppendProcessLog(progressLogVm, failureMessage);
                    break;
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
                    failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.DownloadFailedFormat", "The installer for '{0}' could not be downloaded: {1}"),
                        entry.Title,
                        BuildShortErrorDetail(ex));
                    AppendProcessLog(progressLogVm, failureMessage);
                    break;
                }

                var runResult = await RunInstallerAsync(
                    item,
                    baseGameId,
                    request,
                    downloadedPackage,
                    progressLogVm,
                    ct,
                    useTemporaryLinuxDestination: false);
                if (!runResult.Success)
                {
                    failureMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.RunFailedFormat", "The installer for '{0}' failed: {1}"),
                        entry.Title,
                        runResult.ErrorMessage ?? T("Gog.Install.RunFailed", "Installer execution failed."));
                    AppendProcessLog(progressLogVm, failureMessage);
                    break;
                }

                UpdateInstalledGogDlcState(item, entry, platform.Value, installerPackage);
                _libraryTracker.MarkDirty();
                await SaveData();
                installedProductIds.Add(entry.ProductId);

                if (request.DeleteStagingAfterSuccess)
                    TryDeleteGogStagingDirectoryBestEffort(downloadedPackage.StagingDirectory);

                AppendProcessLog(
                    progressLogVm,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T("Gog.Dlc.InstalledFormat", "Installed: {0}"),
                        entry.Title));
            }
        }
        catch (OperationCanceledException)
        {
            progressLogVm.MarkCancelled(T("Gog.Install.Cancelled", "Installation cancelled by user."));
            failureMessage = T("Gog.Install.Cancelled", "Installation cancelled by user.");
        }
        finally
        {
            progressLogVm.MarkFinished();
            UiThreadHelper.Post(() => progressLogVm.IsRunning = false);
        }

        if (!string.IsNullOrWhiteSpace(failureMessage))
            return new GogDlcInstallBatchResult(installedProductIds, failureMessage);

        var successMessage = string.Format(
            CultureInfo.CurrentCulture,
            T("Gog.Dlc.InstallSuccessFormat", "Successfully installed {0:N0} DLC(s)."),
            installedProductIds.Count);
        return new GogDlcInstallBatchResult(installedProductIds, successMessage);
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
            InstalledInstallerSignature = BuildInstallerSignature(package)
        });
        item.GogDlcInstallations = states;
    }

    private static GogDlcInstallBatchResult CreateGogDlcInstallError(string message) =>
        new(new HashSet<string>(), message);
}
