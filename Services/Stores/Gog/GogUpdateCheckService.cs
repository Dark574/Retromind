using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Services.Stores.Abstractions;

namespace Retromind.Services.Stores.Gog;

internal enum GogUpdateResult
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

internal sealed record GogInstalledSnapshot(
    string ItemId,
    string StoreGameId,
    GogInstallPlatform Platform,
    string? InstalledVersion,
    string? InstalledSignature,
    IReadOnlyList<GogDlcInstallationState> InstalledDlcs,
    DateTimeOffset? LastCheckedUtc);

internal sealed record GogUpdateMetadata(string Status, bool? UpdateAvailable, DateTimeOffset CheckedUtc);

internal sealed class GogUpdateCheckService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan AuthCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PerItemDelay = TimeSpan.FromSeconds(2);

    private readonly IStoreAuthProvider _authProvider;
    private readonly Func<string, GogInstallPlatform, CancellationToken, Task<GogInstallerPackage?>> _resolveInstallerPackage;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<GogDlcCatalogItem>>> _getOwnedDlcCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly object _inFlightLock = new();
    private readonly HashSet<string> _checksInFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sweepSemaphore = new(1, 1);
    private readonly SemaphoreSlim _authSemaphore = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private DateTimeOffset _authCacheValidUntilUtc = DateTimeOffset.MinValue;
    private bool _authAvailableCached;

    internal GogUpdateCheckService(IStoreAuthProvider authProvider, GogInstallService installService)
        : this(authProvider, installService.ResolveInstallerPackageAsync, installService.GetOwnedDlcCatalogAsync)
    {
    }

    internal GogUpdateCheckService(
        IStoreAuthProvider authProvider,
        Func<string, GogInstallPlatform, CancellationToken, Task<GogInstallerPackage?>> resolveInstallerPackage,
        Func<string, CancellationToken, Task<IReadOnlyList<GogDlcCatalogItem>>> getOwnedDlcCatalog,
        TimeProvider? timeProvider = null)
    {
        _authProvider = authProvider;
        _resolveInstallerPackage = resolveInstallerPackage;
        _getOwnedDlcCatalog = getOwnedDlcCatalog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal void StartBackgroundChecks(
        Func<Task<IReadOnlyList<MediaItem>>> getCandidates,
        Func<MediaItem, CancellationToken, Task<GogUpdateResult>> checkCandidate)
    {
        if (_loopTask is { IsCompleted: false })
            return;

        StopBackgroundChecks();
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loopTask = Task.Run(() => RunBackgroundLoopAsync(getCandidates, checkCandidate, token));
    }

    internal void StopBackgroundChecks()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    private async Task RunBackgroundLoopAsync(
        Func<Task<IReadOnlyList<MediaItem>>> getCandidates,
        Func<MediaItem, CancellationToken, Task<GogUpdateResult>> checkCandidate,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(getCandidates, checkCandidate, ct).ConfigureAwait(false);
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
                await Task.Delay(SweepInterval, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSweepAsync(
        Func<Task<IReadOnlyList<MediaItem>>> getCandidates,
        Func<MediaItem, CancellationToken, Task<GogUpdateResult>> checkCandidate,
        CancellationToken ct)
    {
        if (!await _sweepSemaphore.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            var candidates = await getCandidates().ConfigureAwait(false);
            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var result = await checkCandidate(item, ct).ConfigureAwait(false);
                if (result is GogUpdateResult.UpToDate or GogUpdateResult.UpdateAvailable
                    or GogUpdateResult.NoBaseline or GogUpdateResult.Failed)
                {
                    await Task.Delay(PerItemDelay, _timeProvider, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sweepSemaphore.Release();
        }
    }

    internal async Task<GogUpdateResult> CheckAsync(
        GogInstalledSnapshot? snapshot,
        bool force,
        Func<GogUpdateMetadata, Task> publishMetadata,
        Func<bool, Task> publishDlcAvailability,
        CancellationToken ct)
    {
        if (snapshot == null)
            return GogUpdateResult.NotApplicable;

        if (!force && snapshot.LastCheckedUtc is { } lastChecked &&
            _timeProvider.GetUtcNow() - lastChecked < SweepInterval)
        {
            return GogUpdateResult.NotDue;
        }

        lock (_inFlightLock)
        {
            if (!_checksInFlight.Add(snapshot.ItemId))
                return GogUpdateResult.InFlight;
        }

        Task PublishAsync(string status, bool? available = null) =>
            publishMetadata(new GogUpdateMetadata(status, available, _timeProvider.GetUtcNow()));

        try
        {
            if (!await EnsureAuthAsync(forceRefresh: false, ct).ConfigureAwait(false))
            {
                await PublishAsync("auth_missing").ConfigureAwait(false);
                return GogUpdateResult.NoAuth;
            }

            GogInstallerPackage? remotePackage;
            try
            {
                remotePackage = await _resolveInstallerPackage(snapshot.StoreGameId, snapshot.Platform, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (GogInstallService.IsLikelyAuthenticationIssue(ex))
            {
                if (!await EnsureAuthAsync(forceRefresh: true, ct).ConfigureAwait(false))
                    return GogUpdateResult.NoAuth;

                remotePackage = await _resolveInstallerPackage(snapshot.StoreGameId, snapshot.Platform, ct)
                    .ConfigureAwait(false);
            }

            if (remotePackage == null)
            {
                await PublishAsync("unknown").ConfigureAwait(false);
                return GogUpdateResult.Failed;
            }

            await RefreshDlcAvailabilityAsync(snapshot, publishDlcAvailability, ct).ConfigureAwait(false);

            var remoteVersion = GogInstallerFingerprint.NormalizeVersion(remotePackage.Version);
            var remoteSignature = GogInstallerFingerprint.BuildSignature(remotePackage);
            var hasVersion = !string.IsNullOrWhiteSpace(snapshot.InstalledVersion);
            var hasSignature = !string.IsNullOrWhiteSpace(snapshot.InstalledSignature);
            if (!hasVersion && !hasSignature)
            {
                await PublishAsync("unknown", false).ConfigureAwait(false);
                return GogUpdateResult.NoBaseline;
            }

            var versionChanged = hasVersion && !string.Equals(
                GogInstallerFingerprint.NormalizeVersion(snapshot.InstalledVersion), remoteVersion,
                StringComparison.OrdinalIgnoreCase);
            var signatureChanged = hasSignature && !string.Equals(
                snapshot.InstalledSignature, remoteSignature, StringComparison.OrdinalIgnoreCase);
            var available = versionChanged || signatureChanged;

            await PublishAsync(available ? "available" : "up_to_date", available).ConfigureAwait(false);
            return available ? GogUpdateResult.UpdateAvailable : GogUpdateResult.UpToDate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Update check failed for item '{snapshot.ItemId}': {ex.Message}");
            await PublishAsync("error").ConfigureAwait(false);
            return GogUpdateResult.Failed;
        }
        finally
        {
            lock (_inFlightLock)
                _checksInFlight.Remove(snapshot.ItemId);
        }
    }

    private async Task RefreshDlcAvailabilityAsync(
        GogInstalledSnapshot snapshot, Func<bool, Task> publishDlcAvailability, CancellationToken ct)
    {
        if (snapshot.InstalledDlcs.Count == 0)
        {
            await publishDlcAvailability(false).ConfigureAwait(false);
            return;
        }

        try
        {
            var catalog = await _getOwnedDlcCatalog(snapshot.StoreGameId, ct).ConfigureAwait(false);
            var available = GogDlcUpdateComparer.HasUpdate(snapshot.InstalledDlcs, catalog, snapshot.Platform);
            await publishDlcAvailability(available).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] DLC update check failed for item '{snapshot.ItemId}': {ex.Message}");
        }
    }

    private async Task<bool> EnsureAuthAsync(bool forceRefresh, CancellationToken ct)
    {
        await _authSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh && now < _authCacheValidUntilUtc)
                return _authAvailableCached;

            var authenticated = false;
            try
            {
                var authState = await _authProvider.GetAuthStateAsync(ct).ConfigureAwait(false);
                authenticated = authState.IsAuthenticated;
                if (!authenticated)
                    authenticated = await _authProvider.TryRefreshSessionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"[GOG] Silent auth check for updates failed: {ex.Message}");
                authenticated = false;
            }

            _authAvailableCached = authenticated;
            _authCacheValidUntilUtc = now.Add(AuthCacheTtl);
            return authenticated;
        }
        finally
        {
            _authSemaphore.Release();
        }
    }
}
