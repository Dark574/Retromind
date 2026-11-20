using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Services.Downloads;

namespace Retromind.Services.Stores.Gog;

public sealed class GogQueuedDownloadService(GogInstallService installer)
{
    internal async Task<DownloadOutcome> DownloadAsync(DownloadExecution request, string gameId,
        GogInstallPlatform platform, string? installPath, string? installedRevision, CancellationToken ct)
    {
        GogInstallerPackage? package;
        try
        {
            package = await installer.ResolveInstallerPackageAsync(gameId, platform, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && GogInstallService.IsLikelyAuthenticationIssue(ex))
        {
            return new(DownloadState.Failed, DownloadReason.SignIn);
        }

        if (package == null)
            return new(DownloadState.Failed, DownloadReason.InstallerUnavailable);
        if (request.Job.Kind == DownloadKind.Update && GogInstallerArchive.GetRevision(package) == installedRevision)
            return new(DownloadState.Completed);

        var offlineOnly = request.Job.Kind == DownloadKind.OfflineInstaller;
        var staging = GogInstallerArchive.GetStagingDirectory(package);
        string? archive = null;
        try
        {
            if (offlineOnly || request.Settings.KeepGogInstallers)
            {
                archive = GogInstallerArchive.GetPackageDirectory(request.Settings, package);
                GogInstallerArchive.EnsureSeparateFromInstallation(archive, installPath);
                if (await installer.ValidateArchivedPackageAsync(package, archive, ct).ConfigureAwait(false))
                    return Ready(offlineOnly);
                if (Directory.Exists(archive))
                    return new(DownloadState.Failed, DownloadReason.ArchiveUnavailable);
            }
        }
        catch (IOException)
        {
            return new(DownloadState.Failed, DownloadReason.ArchiveUnavailable);
        }

        var space = GogInstallSpaceService.Assess(package, staging, staging);
        if (space.Status is GogInstallSpaceStatus.InsufficientForDownload or GogInstallSpaceStatus.FreeSpaceUnavailable)
            return new(DownloadState.WaitingForSpace, DownloadReason.DownloadSpace);
        if (archive != null && space.InstallerBytes is > 0 &&
            !GogInstallSpaceService.CanFitAdditionalCopy(staging, archive, space))
            return new(DownloadState.WaitingForSpace, DownloadReason.DownloadSpace);

        if (!offlineOnly && installPath != null && space.InstallerBytes is > 0 &&
            !GogInstallSpaceService.CanFitAdditionalCopy(staging, installPath, space))
            return new(DownloadState.WaitingForSpace, DownloadReason.InstallationSpace);

        var limit = request.Settings.BytesPerSecond;
        var limiter = limit > 0 ? new DownloadRateLimiter(limit, TimeProvider.System) : null;
        try
        {
            var downloaded = await installer.DownloadInstallerPackageAsync(package,
                staging, new TransferProgress(request.Progress), limiter, null, ct).ConfigureAwait(false);
            if (archive != null)
                await GogInstallerArchive.PublishAsync(archive, downloaded, ct).ConfigureAwait(false);
        }
        catch (GogInsufficientDiskSpaceException)
        {
            return new(DownloadState.WaitingForSpace, DownloadReason.DownloadSpace);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new(DownloadState.Failed, DownloadReason.SignIn);
        }

        // The offline installer can require licenses, installer UI, Wine or DLC reapplication.
        return Ready(offlineOnly);
    }

    private static DownloadOutcome Ready(bool offlineOnly) => offlineOnly
        ? new(DownloadState.Completed, PayloadReady: true)
        : new(DownloadState.InstallationPending, DownloadReason.ManualGogInstallation, PayloadReady: true);

    private sealed class TransferProgress(Action<DownloadProgress> publish) : IProgress<GogInstallerDownloadProgress>
    {
        private long _bytes;
        private long _timestamp = TimeProvider.System.GetTimestamp();
        private double? _rate;

        public void Report(GogInstallerDownloadProgress value)
        {
            var elapsed = TimeProvider.System.GetElapsedTime(_timestamp).TotalSeconds;
            if (elapsed >= 1)
            {
                _rate = Math.Max(0, value.NetworkBytesOverall - _bytes) / elapsed;
                _timestamp = TimeProvider.System.GetTimestamp();
                _bytes = value.NetworkBytesOverall;
            }
            publish(new DownloadProgress(value.BytesDownloadedOverall, value.BytesTotalOverall, _rate));
        }
    }
}
