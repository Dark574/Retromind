using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services.Stores.Gog;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private const string StoreInstalledVersionField = "Store.InstalledVersion";
    private const string StoreInstalledInstallerSignatureField = "Store.InstalledInstallerSignature";
    private const string StoreUpdateAvailableField = "Store.UpdateAvailable";
    private const string StoreUpdateLastCheckedUtcField = "Store.LastUpdateCheckUtc";
    private const string StoreUpdateLastStatusField = "Store.LastUpdateCheckStatus";

    private static readonly TimeSpan GogUpdateSweepInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan GogUpdateAuthCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan GogUpdatePerItemDelay = TimeSpan.FromSeconds(2);

    private readonly object _gogUpdateLock = new();
    private readonly HashSet<string> _gogUpdateChecksInFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gogUpdateSweepSemaphore = new(1, 1);
    private CancellationTokenSource? _gogUpdateLoopCts;
    private Task? _gogUpdateLoopTask;
    private DateTimeOffset _gogUpdateAuthCacheValidUntilUtc = DateTimeOffset.MinValue;
    private bool _gogUpdateAuthAvailableCached;

    private enum GogUpdateResult
    {
        UpToDate,
        UpdateAvailable,
        NoBaseline,
        NotDue,
        NotApplicable,
        InFlight,
        NoAuth,
        Failed
    }

    private sealed record GogInstalledSnapshot(
        string ItemId,
        string StoreGameId,
        GogInstallPlatform Platform,
        string? InstalledVersion,
        string? InstalledSignature,
        DateTimeOffset? LastCheckedUtc);

    private void StartGogUpdateBackgroundLoop()
    {
        if (_gogUpdateLoopTask is { IsCompleted: false })
            return;

        _gogUpdateLoopCts?.Cancel();
        _gogUpdateLoopCts?.Dispose();
        _gogUpdateLoopCts = new CancellationTokenSource();
        _gogUpdateLoopTask = Task.Run(() => RunGogUpdateBackgroundLoopAsync(_gogUpdateLoopCts.Token));
    }

    private void StopGogUpdateBackgroundLoop()
    {
        _gogUpdateLoopCts?.Cancel();
        _gogUpdateLoopCts?.Dispose();
        _gogUpdateLoopCts = null;
        _gogUpdateLoopTask = null;
    }

    private async Task RunGogUpdateBackgroundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunGogUpdateSweepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Background update sweep failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(GogUpdateSweepInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunGogUpdateSweepAsync(CancellationToken ct)
    {
        if (!await _gogUpdateSweepSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            var candidates = await UiThreadHelper.InvokeAsync(GetInstalledGogItems).ConfigureAwait(false);
            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();
                await CheckGogUpdatesForItemCoreAsync(item, force: true, ct).ConfigureAwait(false);
                await Task.Delay(GogUpdatePerItemDelay, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gogUpdateSweepSemaphore.Release();
        }
    }

    private List<MediaItem> GetInstalledGogItems()
    {
        var result = new List<MediaItem>();
        foreach (var root in RootItems)
            CollectInstalledGogItemsRecursive(root, result);
        return result;
    }

    private static void CollectInstalledGogItemsRecursive(MediaNode node, ICollection<MediaItem> buffer)
    {
        foreach (var item in node.Items)
            buffer.Add(item);

        foreach (var child in node.Children)
            CollectInstalledGogItemsRecursive(child, buffer);
    }

    private bool CanCheckGogUpdatesForItem(MediaItem? item)
    {
        if (item == null || IsLaunchInProgress)
            return false;

        var storeGameId = TryGetStoreGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return false;

        return !ShouldOfferInstallForItem(item);
    }

    private bool ShouldOfferGogUpdateForItem(MediaItem? item)
    {
        if (item == null)
            return false;

        var storeGameId = TryGetStoreGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return false;

        if (ShouldOfferInstallForItem(item))
            return false;

        return item.CustomFields.TryGetValue(StoreUpdateAvailableField, out var raw) &&
               IsTruthyCustomField(raw);
    }

    private async Task<GogUpdateResult> CheckGogUpdatesForItemCoreAsync(
        MediaItem item,
        bool force,
        CancellationToken ct)
    {
        GogInstalledSnapshot? snapshot = null;
        await UiThreadHelper.InvokeAsync(() =>
        {
            snapshot = CreateInstalledGogSnapshot(item);
        }).ConfigureAwait(false);

        if (snapshot == null)
            return GogUpdateResult.NotApplicable;

        if (!force && snapshot.LastCheckedUtc.HasValue &&
            DateTimeOffset.UtcNow - snapshot.LastCheckedUtc.Value < GogUpdateSweepInterval)
        {
            return GogUpdateResult.NotDue;
        }

        if (!TryEnterGogUpdateCheck(snapshot.ItemId))
            return GogUpdateResult.InFlight;

        try
        {
            if (!await EnsureGogAuthForUpdateChecksAsync(forceRefresh: false, ct).ConfigureAwait(false))
            {
                await ApplyGogUpdateCheckMetadataAsync(
                    item,
                    status: "auth_missing",
                    updateAvailable: null,
                    installedVersion: null,
                    installedSignature: null,
                    DateTimeOffset.UtcNow).ConfigureAwait(false);
                return GogUpdateResult.NoAuth;
            }

            GogInstallerPackage? remotePackage;
            try
            {
                remotePackage = await _gogInstallService
                    .ResolveInstallerPackageAsync(snapshot.StoreGameId, snapshot.Platform, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsLikelyGogAuthIssue(ex))
            {
                if (!await EnsureGogAuthForUpdateChecksAsync(forceRefresh: true, ct).ConfigureAwait(false))
                    return GogUpdateResult.NoAuth;

                remotePackage = await _gogInstallService
                    .ResolveInstallerPackageAsync(snapshot.StoreGameId, snapshot.Platform, ct)
                    .ConfigureAwait(false);
            }

            if (remotePackage == null)
            {
                await ApplyGogUpdateCheckMetadataAsync(
                    item,
                    status: "unknown",
                    updateAvailable: null,
                    installedVersion: null,
                    installedSignature: null,
                    DateTimeOffset.UtcNow).ConfigureAwait(false);
                return GogUpdateResult.Failed;
            }

            var remoteVersion = NormalizeGogVersion(remotePackage.Version);
            var remoteSignature = BuildInstallerSignature(remotePackage);
            var hasBaseline = !string.IsNullOrWhiteSpace(snapshot.InstalledVersion) ||
                              !string.IsNullOrWhiteSpace(snapshot.InstalledSignature);

            if (!hasBaseline)
            {
                await ApplyGogUpdateCheckMetadataAsync(
                    item,
                    status: "unknown",
                    updateAvailable: false,
                    installedVersion: null,
                    installedSignature: null,
                    DateTimeOffset.UtcNow).ConfigureAwait(false);
                return GogUpdateResult.NoBaseline;
            }

            var versionChanged = !string.IsNullOrWhiteSpace(snapshot.InstalledVersion) &&
                                 !string.Equals(
                                     NormalizeGogVersion(snapshot.InstalledVersion),
                                     remoteVersion,
                                     StringComparison.OrdinalIgnoreCase);

            var signatureChanged = !string.IsNullOrWhiteSpace(snapshot.InstalledSignature) &&
                                   !string.Equals(
                                       snapshot.InstalledSignature,
                                       remoteSignature,
                                       StringComparison.OrdinalIgnoreCase);

            var updateAvailable = versionChanged || signatureChanged;
            await ApplyGogUpdateCheckMetadataAsync(
                item,
                status: updateAvailable ? "available" : "up_to_date",
                updateAvailable: updateAvailable,
                installedVersion: null,
                installedSignature: null,
                DateTimeOffset.UtcNow).ConfigureAwait(false);

            return updateAvailable ? GogUpdateResult.UpdateAvailable : GogUpdateResult.UpToDate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Update check failed for '{item.Title}': {ex.Message}");
            await ApplyGogUpdateCheckMetadataAsync(
                item,
                status: "error",
                updateAvailable: null,
                installedVersion: null,
                installedSignature: null,
                DateTimeOffset.UtcNow).ConfigureAwait(false);
            return GogUpdateResult.Failed;
        }
        finally
        {
            ExitGogUpdateCheck(snapshot.ItemId);
        }
    }

    private GogInstalledSnapshot? CreateInstalledGogSnapshot(MediaItem item)
    {
        var storeGameId = TryGetStoreGameId(item);
        if (string.IsNullOrWhiteSpace(storeGameId))
            return null;

        if (ShouldOfferInstallForItem(item))
            return null;

        var platform = GetPreferredInstalledGogPlatform(item);
        if (!platform.HasValue)
            return null;

        item.CustomFields.TryGetValue(StoreInstalledVersionField, out var installedVersion);
        item.CustomFields.TryGetValue(StoreInstalledInstallerSignatureField, out var installedSignature);

        DateTimeOffset? lastCheckedUtc = null;
        if (item.CustomFields.TryGetValue(StoreUpdateLastCheckedUtcField, out var rawLastChecked) &&
            DateTimeOffset.TryParse(
                rawLastChecked,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsedLastChecked))
        {
            lastCheckedUtc = parsedLastChecked;
        }

        return new GogInstalledSnapshot(
            item.Id,
            storeGameId,
            platform.Value,
            installedVersion,
            installedSignature,
            lastCheckedUtc);
    }

    private async Task<bool> EnsureGogAuthForUpdateChecksAsync(bool forceRefresh, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!forceRefresh && now < _gogUpdateAuthCacheValidUntilUtc)
            return _gogUpdateAuthAvailableCached;

        var isAuthenticated = false;
        try
        {
            var authState = await _storeAuthProvider.GetAuthStateAsync(ct).ConfigureAwait(false);
            isAuthenticated = authState.IsAuthenticated;
            if (!isAuthenticated)
                isAuthenticated = await _storeAuthProvider.TryRefreshSessionAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Silent auth check for updates failed: {ex.Message}");
            isAuthenticated = false;
        }

        _gogUpdateAuthAvailableCached = isAuthenticated;
        _gogUpdateAuthCacheValidUntilUtc = now.Add(GogUpdateAuthCacheTtl);
        return isAuthenticated;
    }

    private bool TryEnterGogUpdateCheck(string itemId)
    {
        lock (_gogUpdateLock)
            return _gogUpdateChecksInFlight.Add(itemId);
    }

    private void ExitGogUpdateCheck(string itemId)
    {
        lock (_gogUpdateLock)
            _gogUpdateChecksInFlight.Remove(itemId);
    }

    private static string NormalizeGogVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? string.Empty : version.Trim();

    private static bool IsTruthyCustomField(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        return string.Equals(raw.Trim(), "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildInstallerSignature(GogInstallerPackage package)
    {
        var builder = new StringBuilder();
        foreach (var file in package.Files
                     .OrderBy(f => f.FileName ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(file.FileName?.Trim() ?? string.Empty);
            builder.Append('|');
            builder.Append(file.Size?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            builder.Append('\n');
        }

        var payload = builder.ToString();
        if (payload.Length == 0)
            return string.Empty;

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes);
    }

    private async Task ApplyGogUpdateCheckMetadataAsync(
        MediaItem item,
        string status,
        bool? updateAvailable,
        string? installedVersion,
        string? installedSignature,
        DateTimeOffset checkedUtc)
    {
        var changed = await UiThreadHelper.InvokeAsync(() =>
        {
            var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal);
            var hasChanged = false;

            hasChanged |= SetField(fields, StoreUpdateLastStatusField, status);
            hasChanged |= SetField(fields, StoreUpdateLastCheckedUtcField, checkedUtc.ToString("O", CultureInfo.InvariantCulture));

            if (updateAvailable.HasValue)
                hasChanged |= SetField(fields, StoreUpdateAvailableField, updateAvailable.Value ? "true" : "false");

            if (!string.IsNullOrWhiteSpace(installedVersion))
                hasChanged |= SetField(fields, StoreInstalledVersionField, installedVersion.Trim());

            if (!string.IsNullOrWhiteSpace(installedSignature))
                hasChanged |= SetField(fields, StoreInstalledInstallerSignatureField, installedSignature.Trim());

            if (!hasChanged)
                return false;

            item.CustomFields = fields;
            NotifyPlayAvailabilityChanged();
            return true;
        }).ConfigureAwait(false);

        if (changed)
            MarkLibraryDirtyAndSaveSoon();
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
        var version = NormalizeGogVersion(package.Version);
        var signature = BuildInstallerSignature(package);
        var fields = new Dictionary<string, string>(item.CustomFields, StringComparer.Ordinal);
        SetField(fields, StoreInstalledVersionField, version);
        SetField(fields, StoreInstalledInstallerSignatureField, signature);
        SetField(fields, StoreUpdateAvailableField, "false");
        SetField(fields, StoreUpdateLastStatusField, "up_to_date");
        SetField(fields, StoreUpdateLastCheckedUtcField, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        item.CustomFields = fields;
    }
}
