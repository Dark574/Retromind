using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Downloads;

internal enum DownloadStore { Gog, Epic }
internal enum DownloadKind { Update, OfflineInstaller }
internal enum DownloadState
{
    Queued, WaitingForWindow, WaitingForSpace, WaitingForGame, Downloading, Paused,
    InstallationPending, Installing, Completed, Failed, Cancelled, RepairRequired
}

internal enum DownloadReason
{
    None, DownloadSpace, InstallationSpace, GameActive, SignIn, GameUnavailable,
    UnsafeDirectory, InstallerUnavailable, ManualGogInstallation, EpicCombinedUpdate,
    Excluded,
    InstallInterrupted, OperationFailed, SavePending, ArchiveUnavailable, GogPayloadChanged
}

internal sealed record DownloadJob(
    Guid Id, string ItemId, DownloadStore Store, DownloadKind Kind, string Revision,
    bool Automatic, bool OverrideWindow, bool InstallRequested, DownloadState State,
    DownloadReason Reason = DownloadReason.None, bool PayloadReady = false, string? Platform = null,
    bool RepairRequested = false, string? StoreGameId = null, string? PayloadRevision = null);

internal readonly record struct DownloadProgress(long Bytes, long? Total, double? BytesPerSecond = null, double? Fraction = null);
internal readonly record struct DownloadOutcome(DownloadState State, DownloadReason Reason = DownloadReason.None,
    bool PayloadReady = false, string? PayloadRevision = null, bool RedownloadRequired = false);
internal enum DownloadAction { Start, Pause, Resume, Cancel, Retry, Earlier, Later, Install, Repair, Remove, DiscardRepair }

internal sealed record DownloadExecution(
    DownloadJob Job, DownloadSettings Settings, bool MayInstall,
    Action<DownloadProgress> Progress, Func<bool> BeginInstallation);

internal delegate Task<DownloadOutcome> ExecuteDownloadAsync(DownloadExecution execution, CancellationToken token);
