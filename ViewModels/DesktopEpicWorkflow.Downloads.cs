using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Downloads;
using Retromind.Services.Stores;
using Retromind.Services.Stores.Epic;
using Retromind.Services.Stores.Epic.Legendary;

namespace Retromind.ViewModels;

internal sealed partial class DesktopEpicWorkflow
{
    private readonly SemaphoreSlim _updateCatalogGate = new(1, 1);
    private readonly object _updateCatalogStateGate = new();
    private IReadOnlyDictionary<string, LegendaryUpdateStatus>? _updateCatalog;
    private DateTimeOffset _catalogExpires;
    private long _catalogGeneration;

    internal event Action<MediaItem, string>? UpdateFound;

    internal bool ApplyEpicUpdateCheckMetadata(MediaItem item, LegendaryUpdateStatus status)
    {
        var revision = status.IsUpdateAvailable ? GetAvailableRevision(status.AvailableVersion) : null;
        var changed = SetEpicUpdateAvailability(item, status.IsUpdateAvailable, revision);
        if (revision != null)
            UpdateFound?.Invoke(item, revision);
        return changed;
    }

    private static string GetAvailableRevision(string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(version)));

    private static bool SetEpicUpdateAvailability(MediaItem item, bool available, string? revision = null)
    {
        var value = available ? "true" : "false";
        if (item.CustomFields.GetValueOrDefault(CustomFieldKeyHelper.StoreUpdateAvailable) == value &&
            item.CustomFields.GetValueOrDefault(CustomFieldKeyHelper.StoreAvailableRevision) == revision)
            return false;

        var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal)
        {
            [CustomFieldKeyHelper.StoreUpdateAvailable] = value
        };
        if (revision == null)
            fields.Remove(CustomFieldKeyHelper.StoreAvailableRevision);
        else
            fields[CustomFieldKeyHelper.StoreAvailableRevision] = revision;

        item.CustomFields = fields;
        return true;
    }

    internal async Task<bool> HasUpdateSessionAsync(CancellationToken ct) =>
        (await _epicStoreAuthProvider.GetAuthStateAsync(ct).ConfigureAwait(false)).IsAuthenticated;

    internal async Task<IReadOnlyDictionary<string, LegendaryUpdateStatus>> GetQueuedUpdateCatalogAsync(
        CancellationToken ct)
    {
        await _updateCatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long generation;
                lock (_updateCatalogStateGate)
                {
                    if (_updateCatalog != null && _catalogExpires > DateTimeOffset.UtcNow)
                        return _updateCatalog;
                    generation = _catalogGeneration;
                }

                var catalog = await _epicInstallService.GetUpdateStatusesAsync(ct).ConfigureAwait(false);
                lock (_updateCatalogStateGate)
                {
                    if (generation != _catalogGeneration)
                        continue;
                    _updateCatalog = catalog;
                    _catalogExpires = DateTimeOffset.UtcNow.AddMinutes(5);
                    return catalog;
                }
            }
        }
        finally
        {
            _updateCatalogGate.Release();
        }
    }

    private void InvalidateUpdateCatalog()
    {
        lock (_updateCatalogStateGate)
        {
            _catalogGeneration++;
            _updateCatalog = null;
        }
    }

    internal bool IsCurrentUpdateCatalog(IReadOnlyDictionary<string, LegendaryUpdateStatus> catalog)
    {
        lock (_updateCatalogStateGate)
            return ReferenceEquals(_updateCatalog, catalog);
    }

    internal async Task<bool> SaveEpicInstallationAsync(MediaItem item, string appName, string version, bool repair = false)
    {
        InvalidateUpdateCatalog();
        return await SaveInstalledGameAsync(new InstalledGameVersion(item.Id, DownloadStore.Epic, appName,
            string.IsNullOrWhiteSpace(version) ? string.Empty : GetAvailableRevision(version), "windows",
            CompletesUnversionedUpdates: !repair));
    }

    internal async Task<DownloadOutcome> RunQueuedUpdateAsync(MediaItem item, DownloadExecution execution, CancellationToken ct)
    {
        // This part runs through the desktop's UI dispatcher. Claim the same guard as manual
        // maintenance and launch before any operation that can mutate the installation.
        if (!TryGetQueuedUpdateLocation(item, out var appName, out var installPath))
            return new(DownloadState.Failed, DownloadReason.UnsafeDirectory);
        if (IsLaunchInProgress)
            return new(DownloadState.WaitingForGame, DownloadReason.GameActive);

        if (!await HasUpdateSessionAsync(ct))
            return new(DownloadState.Failed, DownloadReason.SignIn);
        var updates = await GetQueuedUpdateCatalogAsync(ct);
        if (!IsCurrentUpdateCatalog(updates))
            return new(DownloadState.Queued);
        if (IsLaunchInProgress)
            return new(DownloadState.WaitingForGame, DownloadReason.GameActive);
        if (!updates.TryGetValue(appName, out var status))
            return new(DownloadState.Failed, DownloadReason.InstallerUnavailable);
        var targetRevision = GetAvailableRevision(status.AvailableVersion);
        if (!execution.Job.RepairRequested && !status.IsUpdateAvailable)
        {
            if (!ApplyEpicUpdateCheckMetadata(item, status))
                return new(DownloadState.Completed);

            Workspace.MarkLibraryDirty();
            NotifyPlayAvailabilityChanged();
            return new(DownloadState.Completed, await SaveData() ? DownloadReason.None : DownloadReason.SavePending);
        }
        if (!execution.MayInstall)
            return new(DownloadState.InstallationPending, DownloadReason.EpicCombinedUpdate, ResolvedRevision: targetRevision);

        var estimate = await _epicInstallService.GetInstallEstimateAsync(appName, ct);
        if (!IsCurrentUpdateCatalog(updates))
            return new(DownloadState.Queued, ResolvedRevision: targetRevision);
        if (_epicInstallService.AssessSpace(installPath, estimate).Status != EpicInstallSpaceStatus.Sufficient)
            return new(DownloadState.WaitingForSpace, DownloadReason.InstallationSpace, ResolvedRevision: targetRevision);
        if (IsLaunchInProgress)
            return new(DownloadState.WaitingForGame, DownloadReason.GameActive, ResolvedRevision: targetRevision);
        if (!TryGetQueuedUpdateLocation(item, out var currentApp, out var currentPath) ||
            currentApp != appName || !FileSystemPathIdentity.Equals(currentPath, installPath))
            return new(DownloadState.Failed, DownloadReason.UnsafeDirectory);
        if (Workspace.FindParentNode(item) == null)
            return new(DownloadState.Failed, DownloadReason.GameUnavailable);
        ct.ThrowIfCancellationRequested();
        if (!execution.BeginInstallation())
            return new(DownloadState.InstallationPending, DownloadReason.EpicCombinedUpdate, ResolvedRevision: targetRevision);

        IsLaunchInProgress = true;
        try
        {
            var progress = new LegendaryDownloadProgress(execution.Progress);
            var installed = await _epicInstallService.InstallAsync(item, installPath, progress.Read,
                CancellationToken.None, repair: execution.Job.RepairRequested, updateOnly: true);
            if (string.IsNullOrWhiteSpace(installed.Version) ||
                !execution.Job.RepairRequested && installed.Version == status.InstalledVersion)
                throw new LegendaryCliException("Legendary did not report a newer installed build.");

            ApplyEpicExecutablePath(item, installed);
            item.CustomFields[CustomFieldKeyHelper.StoreInstallPath] = StoreInstallPathHelper.ToStoredPath(
                installed.InstallPath, Workspace.Settings.PreferPortableLaunchPaths);
            item.CustomFields[CustomFieldKeyHelper.StoreInstalledVersion] = installed.Version;
            var updateStillAvailable = installed.Version != status.AvailableVersion;
            SetEpicUpdateAvailability(item, updateStillAvailable,
                updateStillAvailable && installed.Version == status.InstalledVersion
                    ? GetAvailableRevision(status.AvailableVersion)
                    : null);
            Workspace.MarkLibraryDirty();
            NotifyPlayAvailabilityChanged();
            InvalidateUpdateCatalog();
            return new(DownloadState.Completed, await SaveData() ? DownloadReason.None : DownloadReason.SavePending);
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private bool TryGetQueuedUpdateLocation(MediaItem item, out string appName, out string path)
    {
        appName = EpicMediaItemStateHelper.TryGetGameId(item) ?? string.Empty;
        path = string.Empty;
        return appName.Length > 0 &&
            item.CustomFields.TryGetValue(CustomFieldKeyHelper.StoreInstallPath, out var stored) &&
            StoreInstallPathHelper.TryResolveStoredPath(stored, out path) &&
            _epicInstallService.AssessDirectory(path, item).Status == EpicInstallDirectoryStatus.OwnedDirectory &&
            !IsInstallDirectoryUsedByAnotherItem(path, item);
    }
}
