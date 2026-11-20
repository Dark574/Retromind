using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

internal sealed partial class DesktopGogWorkflow
{
    internal event Action<MediaItem, string>? UpdateFound;

    internal void StartGogUpdateBackgroundLoop(bool requestImmediateSweep = false) =>
        _gogUpdateChecks.StartBackgroundChecks(
            () => UiThreadHelper.InvokeAsync(GetInstalledGogItems),
            (item, ct) => CheckGogUpdatesForItemCoreAsync(item, force: false, ct),
            requestImmediateSweep);

    private IReadOnlyList<MediaItem> GetInstalledGogItems()
    {
        if ((Workspace.Settings.Downloads?.AutomaticUpdates ?? AutomaticUpdateMode.Disabled) == AutomaticUpdateMode.Disabled)
            return [];

        var knownUpdates = new List<MediaItem>();
        var regularChecks = new List<MediaItem>();
        foreach (var root in RootItems)
            CollectInstalledGogItemsRecursive(root, knownUpdates, regularChecks);
        knownUpdates.AddRange(regularChecks);
        return knownUpdates;
    }

    private void CollectInstalledGogItemsRecursive(
        MediaNode node, ICollection<MediaItem> knownUpdates, ICollection<MediaItem> regularChecks)
    {
        foreach (var item in node.Items)
        {
            if (CreateInstalledGogSnapshot(item) is { } snapshot && _gogUpdateChecks.IsAutomaticCheckDue(snapshot))
                (snapshot.NeedsAvailableRevision ? knownUpdates : regularChecks).Add(item);
        }

        foreach (var child in node.Children)
            CollectInstalledGogItemsRecursive(child, knownUpdates, regularChecks);
    }

    internal bool CanCheckGogUpdatesForItem(MediaItem? item)
    {
        if (item == null || IsLaunchInProgress)
            return false;

        return GogMediaItemStateHelper.CanCheckUpdates(item) &&
               !GogMediaItemStateHelper.HasAnyUpdateAvailable(item);
    }

    private static bool ShouldOfferGogUpdateForItem(MediaItem? item) =>
        GogMediaItemStateHelper.HasUpdateAvailable(item);

    internal async Task CheckGogUpdatesNowAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner)
            return;

        await CheckGogUpdatesNowAsync(item, owner);
    }

    internal async Task CheckGogUpdatesNowAsync(MediaItem item, Avalonia.Controls.Window owner)
    {
        if (!CanCheckGogUpdatesForItem(item))
            return;

        var result = await CheckGogUpdatesForItemCoreAsync(
                item,
                force: true,
                CancellationToken.None)
            .ConfigureAwait(false);

        var hasAnyUpdate = await UiThreadHelper
            .InvokeAsync(() => GogMediaItemStateHelper.HasAnyUpdateAvailable(item))
            .ConfigureAwait(false);

        var message = result switch
        {
            GogUpdateResult.UpdateAvailable =>
                T("Gog.Update.CheckAvailable", "A GOG update is available."),
            GogUpdateResult.UpToDate when hasAnyUpdate =>
                T("Gog.Update.CheckAvailable", "A GOG update is available."),
            GogUpdateResult.UpToDate =>
                T("Gog.Update.CheckUpToDate", "No update is currently available for the installed platform."),
            GogUpdateResult.NoBaseline =>
                T("Gog.Update.CheckNoBaseline", "The update status could not be determined because the installed version is unknown."),
            GogUpdateResult.NoAuth =>
                T("Gog.Update.CheckNoAuth", "Sign in to GOG before checking for updates."),
            GogUpdateResult.InFlight =>
                T("Gog.Update.CheckInFlight", "An update check is already running for this game."),
            GogUpdateResult.NotApplicable =>
                T("Gog.Update.CheckNotApplicable", "This game is not currently installed through GOG."),
            _ =>
                T("Gog.Update.CheckFailed", "The GOG update check failed.")
        };

        await ShowInfoDialog(owner, message, showCopyButton: false).ConfigureAwait(false);
    }

    internal async Task<GogUpdateResult> CheckGogUpdatesForItemCoreAsync(
        MediaItem item, bool force, CancellationToken ct)
    {
        var snapshot = await UiThreadHelper.InvokeAsync(() => CreateInstalledGogSnapshot(item))
            .ConfigureAwait(false);
        return await _gogUpdateChecks.CheckAsync(
                snapshot, force,
                metadata => ApplyGogUpdateCheckMetadataAsync(item, metadata),
                available => UiThreadHelper.InvokeAsync(() => SetGogDlcUpdateAvailability(item, available)),
                ct)
            .ConfigureAwait(false);
    }

    private GogInstalledSnapshot? CreateInstalledGogSnapshot(MediaItem item)
    {
        var storeGameId = GogMediaItemStateHelper.TryGetGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return null;

        if (ShouldOfferInstallForItem(item))
            return null;

        var platform = GetPreferredInstalledGogPlatform(item);
        if (!platform.HasValue)
            return null;

        var fields = item.CustomFields;
        fields.TryGetValue(CustomFieldKeyHelper.StoreInstalledVersion, out var installedVersion);
        fields.TryGetValue(CustomFieldKeyHelper.StoreInstalledInstallerSignature, out var installedSignature);
        fields.TryGetValue(CustomFieldKeyHelper.StoreUpdateLastStatus, out var lastStatus);
        fields.TryGetValue(CustomFieldKeyHelper.StoreAvailableRevision, out var availableRevision);
        var needsAvailableRevision =
            Workspace.Settings.Downloads?.AutomaticUpdates is (AutomaticUpdateMode.DownloadOnly or AutomaticUpdateMode.DownloadAndInstall) &&
            string.IsNullOrWhiteSpace(availableRevision) &&
            GogMediaItemStateHelper.HasUpdateAvailable(item) &&
            GogMediaItemStateHelper.IsRetromindManagedInstallation(item);

        return new GogInstalledSnapshot(
            item.Id,
            storeGameId,
            platform.Value,
            installedVersion,
            installedSignature,
            item.GogDlcInstallations?
                .Select(static state => new GogDlcInstallationState
                {
                    ProductId = state.ProductId,
                    Title = state.Title,
                    Platform = state.Platform,
                    InstalledVersion = state.InstalledVersion,
                    InstalledInstallerSignature = state.InstalledInstallerSignature
                })
                .ToArray() ?? Array.Empty<GogDlcInstallationState>(),
            ReadGogUpdateTimestamp(fields, CustomFieldKeyHelper.StoreUpdateLastCheckedUtc),
            ReadGogUpdateTimestamp(fields, CustomFieldKeyHelper.StoreUpdateRetryAfterUtc),
            lastStatus,
            needsAvailableRevision);
    }

    private static DateTimeOffset? ReadGogUpdateTimestamp(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp
            : null;

    private async Task ApplyGogUpdateCheckMetadataAsync(MediaItem item, GogUpdateMetadata metadata)
    {
        var changed = await UiThreadHelper.InvokeAsync(() =>
        {
            var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal);
            var hasChanged = false;

            hasChanged |= SetField(fields, CustomFieldKeyHelper.StoreUpdateLastStatus, metadata.Status);
            if (metadata.CheckedUtc is { } checkedUtc)
                hasChanged |= SetField(fields, CustomFieldKeyHelper.StoreUpdateLastCheckedUtc,
                    checkedUtc.ToString("O", CultureInfo.InvariantCulture));

            hasChanged |= metadata.RetryAfterUtc is { } retryAfter
                ? SetField(fields, CustomFieldKeyHelper.StoreUpdateRetryAfterUtc, retryAfter.ToString("O", CultureInfo.InvariantCulture))
                : fields.Remove(CustomFieldKeyHelper.StoreUpdateRetryAfterUtc);

            if (metadata.UpdateAvailable is { } available)
                hasChanged |= SetField(fields, CustomFieldKeyHelper.StoreUpdateAvailable, available ? "true" : "false");

            if (metadata.UpdateAvailable == true && metadata.AvailableRevision is { } revision)
                hasChanged |= SetField(fields, CustomFieldKeyHelper.StoreAvailableRevision, revision);
            else if (metadata.UpdateAvailable == false)
                hasChanged |= fields.Remove(CustomFieldKeyHelper.StoreAvailableRevision);

            if (!hasChanged)
                return false;

            item.CustomFields = fields;
            NotifyPlayAvailabilityChanged();
            return true;
        }).ConfigureAwait(false);

        if (metadata.UpdateAvailable == true && metadata.AvailableRevision is { } availableRevision)
            await UiThreadHelper.InvokeAsync(() => UpdateFound?.Invoke(item, availableRevision)).ConfigureAwait(false);

        if (changed)
            Workspace.MarkLibraryDirtyAndSaveSoon();
    }

    private void SetGogDlcUpdateAvailability(MediaItem item, bool updateAvailable)
    {
        var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal);
        var changed = updateAvailable
            ? SetField(fields, CustomFieldKeyHelper.StoreDlcUpdateAvailable, "true")
            : fields.Remove(CustomFieldKeyHelper.StoreDlcUpdateAvailable);
        if (!changed)
            return;

        item.CustomFields = fields;
        NotifyPlayAvailabilityChanged();
        Workspace.MarkLibraryDirtyAndSaveSoon();
    }

    private static bool SetField(IDictionary<string, string> fields, string key, string value)
    {
        if (fields.TryGetValue(key, out var existing) &&
            string.Equals(existing, value, StringComparison.Ordinal))
        {
            return false;
        }

        fields[key] = value;
        return true;
    }

    private void UpdateInstalledGogFingerprint(MediaItem item, GogInstallerPackage package)
    {
        var version = GogInstallerFingerprint.NormalizeVersion(package.Version);
        var signature = GogInstallerFingerprint.BuildSignature(package);
        var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal);
        SetField(fields, CustomFieldKeyHelper.StoreInstalledVersion, version);
        SetField(fields, CustomFieldKeyHelper.StoreInstalledInstallerSignature, signature);
        SetField(fields, CustomFieldKeyHelper.StoreUpdateAvailable, "false");
        SetField(fields, CustomFieldKeyHelper.StoreUpdateLastStatus, "up_to_date");
        SetField(fields, CustomFieldKeyHelper.StoreUpdateLastCheckedUtc, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        fields.Remove(CustomFieldKeyHelper.StoreUpdateRetryAfterUtc);
        fields.Remove(CustomFieldKeyHelper.StoreAvailableRevision);
        item.CustomFields = fields;
    }
}
