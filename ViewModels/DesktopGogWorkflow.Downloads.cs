using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

internal sealed partial class DesktopGogWorkflow
{
    internal async Task<DownloadOutcome> RunQueuedDownloadAsync(GogQueuedDownloadService service, MediaItem item,
        DownloadExecution execution, CancellationToken ct)
    {
        var job = execution.Job;
        var gameId = GogMediaItemStateHelper.TryGetGameId(item);
        var platform = job.Platform switch
        {
            "windows" => GogInstallPlatform.Windows,
            "linux" => GogInstallPlatform.Linux,
            _ => GetPreferredInstalledGogPlatform(item)
        };
        if (gameId == null || !platform.HasValue)
            return new(DownloadState.Failed, DownloadReason.InstallerUnavailable);

        string? installPath = null;
        string? revision = null;
        if (job.Kind == DownloadKind.Update)
        {
            if (!item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var stored) ||
                !StoreInstallPathHelper.TryResolveStoredPath(stored, out installPath) ||
                GogInstallDirectorySafety.Assess(installPath, item, rejectSymbolicLinks: false).Status !=
                    GogInstallDirectoryStatus.OwnedDirectory || IsInstallDirectoryUsedByAnotherItem(installPath, item))
                return new(DownloadState.Failed, DownloadReason.UnsafeDirectory);

            revision = GogInstallerArchive.GetRevision(
                item.CustomFields.GetValueOrDefault(CustomFieldKeyHelper.StoreInstalledVersion),
                item.CustomFields.GetValueOrDefault(CustomFieldKeyHelper.StoreInstalledInstallerSignature));
            if (!job.RepairRequested && revision == job.Revision)
                return new(DownloadState.Completed);

            if (job.InstallRequested && (job.PayloadReady || job.RepairRequested))
            {
                if (!job.RepairRequested && job.PayloadRevision == null)
                    return RequireFreshPayload();
                if (IsLaunchInProgress)
                    return new(DownloadState.WaitingForGame, DownloadReason.GameActive, true);
                if (CurrentWindow == null || !execution.BeginInstallation())
                    return new(DownloadState.InstallationPending, DownloadReason.ManualGogInstallation, true);

                IsLaunchInProgress = true;
                var started = false;
                var needsRedownload = false;
                try
                {
                    var operation = job.RepairRequested ? GogInstallOperation.Reinstall : GogInstallOperation.Update;
                    var queuedPayload = job.RepairRequested
                        ? null
                        : new QueuedGogPayload(platform.Value, job.PayloadRevision!, () => needsRedownload = true);
                    var success = await InstallGogItemAsync(item, operation: operation,
                        installationStarted: () => started = true, queuedPayload: queuedPayload);
                    if (needsRedownload)
                        return RequireFreshPayload();
                    return success ? new(DownloadState.Completed, PayloadReady: true)
                        : started || job.RepairRequested ? new(DownloadState.RepairRequired, DownloadReason.InstallInterrupted, true)
                        : new(DownloadState.InstallationPending, DownloadReason.ManualGogInstallation, true);
                }
                finally
                {
                    IsLaunchInProgress = false;
                }
            }
        }

        // Work on the provider's download service, with only a snapshot of the UI metadata.
        return await service.DownloadAsync(execution, gameId, platform.Value, installPath, revision, ct, item.Title)
            .ConfigureAwait(false);
    }

    private static DownloadOutcome RequireFreshPayload() =>
        new(DownloadState.Queued, DownloadReason.GogPayloadChanged, RedownloadRequired: true);

    internal Task<bool> SaveGogInstallationAsync(MediaItem item, GogInstallerPackage package,
        GogInstallPlatform? previousPlatform) =>
        SaveInstalledGameAsync(new InstalledGameVersion(item.Id, DownloadStore.Gog, package.GameId,
            GogInstallerArchive.GetRevision(package), GogInstallerArchive.GetPlatformName(package.Platform),
            previousPlatform.HasValue ? GogInstallerArchive.GetPlatformName(previousPlatform.Value) : null));
}
