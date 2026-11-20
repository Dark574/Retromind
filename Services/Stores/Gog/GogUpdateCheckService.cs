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
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset? RetryAfterUtc = null,
    string? LastStatus = null,
    bool NeedsAvailableRevision = false);

internal sealed record GogUpdateMetadata(
    string Status, bool? UpdateAvailable, DateTimeOffset? CheckedUtc, DateTimeOffset? RetryAfterUtc = null, string? AvailableRevision = null);

internal sealed class GogUpdateCheckService
{
    private const string AuthMissingStatus = "auth_missing";
    private const string InstallerUnknownStatus = "unknown";
    private const string ErrorStatus = "error";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan BackgroundSweepInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedCheckRetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MissingAuthRetryInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UnknownInstallerRetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan AuthCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PerItemDelay = TimeSpan.FromSeconds(2);

    private readonly IStoreAuthProvider _authProvider;
    private readonly Func<string, GogInstallPlatform, CancellationToken, Task<GogInstallerPackage?>> _resolveInstallerPackage;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<GogDlcCatalogItem>>> _getOwnedDlcCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly object _inFlightLock = new();
    private readonly HashSet<string> _checksInFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _authSemaphore = new(1, 1);
    private readonly object _loopLock = new();
    private readonly SemaphoreSlim _sweepRequested = new(0, 1);
    private CancellationTokenSource? _loopCts;
    private Task _loopTask = Task.CompletedTask;
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
        Func<MediaItem, CancellationToken, Task<GogUpdateResult>> checkCandidate,
        bool requestImmediateSweep = false)
    {
        lock (_loopLock)
        {
            if (_loopCts is { IsCancellationRequested: false })
            {
                if (requestImmediateSweep && _sweepRequested.CurrentCount == 0)
                    _sweepRequested.Release();
                return;
            }

            var previousLoop = _loopTask;
            var cancellation = new CancellationTokenSource();
            _loopCts = cancellation;
            _loopTask = Task.Run(async () =>
            {
                try
                {
                    await previousLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await RunBackgroundLoopAsync(getCandidates, checkCandidate, cancellation.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_loopLock)
                    {
                        if (ReferenceEquals(_loopCts, cancellation))
                            _loopCts = null;
                        cancellation.Dispose();
                    }
                }
            });
        }
    }

    internal Task StopBackgroundChecksAsync()
    {
        lock (_loopLock)
        {
            var stoppedLoop = _loopTask;
            _loopCts?.Cancel();
            return stoppedLoop;
        }
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GOG] Background update sweep failed: {ex.Message}");
            }

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var request = _sweepRequested.WaitAsync(wait.Token);
                var interval = Task.Delay(BackgroundSweepInterval, _timeProvider, wait.Token);
                await Task.WhenAny(request, interval).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
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

    internal bool IsAutomaticCheckDue(GogInstalledSnapshot snapshot)
    {
        var now = _timeProvider.GetUtcNow();
        if (snapshot.RetryAfterUtc is { } retryAfter)
            return now >= retryAfter;

        // Older available-update records did not include the target revision that the queue uses.
        if (snapshot.NeedsAvailableRevision &&
            snapshot.LastStatus is not (AuthMissingStatus or InstallerUnknownStatus or ErrorStatus))
            return true;

        // Older records used LastCheckedUtc for failures as well.
        return snapshot.LastCheckedUtc is not { } lastChecked ||
               now - lastChecked >= GetCheckInterval(snapshot.LastStatus);
    }

    private static TimeSpan GetCheckInterval(string? status) => status switch
    {
        AuthMissingStatus => MissingAuthRetryInterval,
        InstallerUnknownStatus => UnknownInstallerRetryInterval,
        ErrorStatus => FailedCheckRetryInterval,
        _ => CheckInterval
    };

    internal async Task<GogUpdateResult> CheckAsync(
        GogInstalledSnapshot? snapshot,
        bool force,
        Func<GogUpdateMetadata, Task> publishMetadata,
        Func<bool, Task> publishDlcAvailability,
        CancellationToken ct)
    {
        if (snapshot == null)
            return GogUpdateResult.NotApplicable;

        if (!force && !IsAutomaticCheckDue(snapshot))
            return GogUpdateResult.NotDue;

        lock (_inFlightLock)
        {
            if (!_checksInFlight.Add(snapshot.ItemId))
                return GogUpdateResult.InFlight;
        }

        Task PublishAsync(GogUpdateMetadata metadata)
        {
            ct.ThrowIfCancellationRequested();
            return publishMetadata(metadata);
        }

        async Task<GogUpdateResult> PublishRetryAsync(GogUpdateResult result, string status)
        {
            var retryAfter = _timeProvider.GetUtcNow() + GetCheckInterval(status);
            await PublishAsync(new GogUpdateMetadata(status, null, null, retryAfter)).ConfigureAwait(false);
            return result;
        }

        try
        {
            if (!await EnsureAuthAsync(forceRefresh: false, ct).ConfigureAwait(false))
                return await PublishRetryAsync(GogUpdateResult.NoAuth, AuthMissingStatus).ConfigureAwait(false);

            GogInstallerPackage? remotePackage;
            try
            {
                remotePackage = await _resolveInstallerPackage(snapshot.StoreGameId, snapshot.Platform, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (GogInstallService.IsLikelyAuthenticationIssue(ex))
            {
                if (!await EnsureAuthAsync(forceRefresh: true, ct).ConfigureAwait(false))
                    return await PublishRetryAsync(GogUpdateResult.NoAuth, AuthMissingStatus).ConfigureAwait(false);

                remotePackage = await _resolveInstallerPackage(snapshot.StoreGameId, snapshot.Platform, ct)
                    .ConfigureAwait(false);
            }

            if (remotePackage == null)
                return await PublishRetryAsync(GogUpdateResult.Failed, InstallerUnknownStatus).ConfigureAwait(false);

            await RefreshDlcAvailabilityAsync(snapshot, publishDlcAvailability, ct).ConfigureAwait(false);

            var remoteVersion = GogInstallerFingerprint.NormalizeVersion(remotePackage.Version);
            var remoteSignature = GogInstallerFingerprint.BuildSignature(remotePackage);
            var hasVersion = !string.IsNullOrWhiteSpace(snapshot.InstalledVersion);
            var hasSignature = !string.IsNullOrWhiteSpace(snapshot.InstalledSignature);
            if (!hasVersion && !hasSignature)
            {
                await PublishAsync(new GogUpdateMetadata("no_baseline", false, _timeProvider.GetUtcNow()))
                    .ConfigureAwait(false);
                return GogUpdateResult.NoBaseline;
            }

            var versionChanged = hasVersion && !string.Equals(
                GogInstallerFingerprint.NormalizeVersion(snapshot.InstalledVersion), remoteVersion,
                StringComparison.OrdinalIgnoreCase);
            var signatureChanged = hasSignature && !string.Equals(
                snapshot.InstalledSignature, remoteSignature, StringComparison.OrdinalIgnoreCase);
            var available = versionChanged || signatureChanged;

            await PublishAsync(new GogUpdateMetadata(
                available ? "available" : "up_to_date", available, _timeProvider.GetUtcNow(),
                AvailableRevision: available ? GogInstallerArchive.GetRevision(remotePackage) : null)).ConfigureAwait(false);
            return available ? GogUpdateResult.UpdateAvailable : GogUpdateResult.UpToDate;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GOG] Update check failed for item '{snapshot.ItemId}': {ex.Message}");
            return await PublishRetryAsync(GogUpdateResult.Failed, ErrorStatus).ConfigureAwait(false);
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
        ct.ThrowIfCancellationRequested();
        if (snapshot.InstalledDlcs.Count == 0)
        {
            await publishDlcAvailability(false).ConfigureAwait(false);
            return;
        }

        try
        {
            var catalog = await _getOwnedDlcCatalog(snapshot.StoreGameId, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var available = GogDlcUpdateComparer.HasUpdate(snapshot.InstalledDlcs, catalog, snapshot.Platform);
            await publishDlcAvailability(available).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
