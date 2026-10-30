using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Auth;

namespace Retromind.Services.Stores.Gog.Playtime;

internal sealed class GogPlaytimeSyncService : IGogPlaytimeSyncService
{
    private const int QueueSchemaVersion = 1;
    private const int MaximumPendingSessions = 256;
    private const int MaximumQueueBytes = 256 * 1024;
    private const int MaximumSessionMinutes = 31 * 24 * 60;
    private const int MaximumSyncAttemptsPerRun = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 16
    };

    private readonly Func<CancellationToken, Task<GogGameplayCredentials?>> _getCredentialsAsync;
    private readonly Func<CancellationToken, Task<string?>> _getUserIdAsync;
    private readonly IGogPlaytimeApiClient _apiClient;
    private readonly string _queuePath;
    private readonly IErrorService _errorService;
    private readonly Func<string, Stream> _openQueueRead;
    private readonly Action<string, string> _replaceQueue;
    private readonly Action<string> _deleteQueue;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _queueGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _backgroundSyncLock = new();
    private bool _backgroundSyncRequested;
    private Task? _backgroundSyncTask;
    // Only accessed under _syncGate. These are confirmed remote writes whose local commit is still pending.
    private readonly Dictionary<SessionIdentity, PendingSession> _confirmedUploads = new();
    private readonly object _diagnosticLock = new();
    private readonly Dictionary<string, (long Timestamp, int Suppressed)> _transientUploadDiagnostics = new();

    internal GogPlaytimeSyncService(
        GogAuthService authService,
        IGogPlaytimeApiClient apiClient,
        string queuePath,
        IErrorService errorService)
        : this((authService ?? throw new ArgumentNullException(nameof(authService))).GetGameplayCredentialsAsync,
            authService.GetGameplayUserIdAsync, apiClient, queuePath, errorService)
    {
    }

    internal GogPlaytimeSyncService(
        Func<CancellationToken, Task<GogGameplayCredentials?>> getCredentialsAsync,
        Func<CancellationToken, Task<string?>> getUserIdAsync,
        IGogPlaytimeApiClient apiClient,
        string queuePath,
        IErrorService errorService,
        Func<string, Stream>? openQueueRead = null,
        Action<string, string>? replaceQueue = null,
        Action<string>? deleteQueue = null,
        TimeProvider? timeProvider = null)
    {
        _getCredentialsAsync = getCredentialsAsync ??
                               throw new ArgumentNullException(nameof(getCredentialsAsync));
        _getUserIdAsync = getUserIdAsync ?? throw new ArgumentNullException(nameof(getUserIdAsync));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(queuePath);
        _queuePath = Path.GetFullPath(queuePath);
        _errorService = errorService ?? throw new ArgumentNullException(nameof(errorService));
        _openQueueRead = openQueueRead ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
        _replaceQueue = replaceQueue ?? ((source, target) => File.Move(source, target, overwrite: true));
        _deleteQueue = deleteQueue ?? File.Delete;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static string GetDefaultQueuePath(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(
            Path.GetFullPath(dataRoot),
            "Cache",
            "Gog",
            "pending-playtime-sessions.json");
    }

    public async Task EnqueueSessionAsync(
        MediaItem item,
        DateTimeOffset startedAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var gameId = GogMediaItemStateHelper.TryGetGameId(item)?.Trim();
        var timeMinutes = GetWholeMinutes(duration);
        if (!IsPositiveNumericIdentifier(gameId) || timeMinutes < 1)
            return;

        string? userId;
        try
        {
            userId = await _getUserIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportFailure("GogPlaytime.UploadFailed", ErrorSeverity.Warning, "IdentifyAccount", gameId,
                exception: ex, throttleTransient: true);
            throw new GogPlaytimeQueueException(GogPlaytimeQueueFailure.AccountUnavailable, ex);
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            ReportFailure("GogPlaytime.UploadFailed", ErrorSeverity.Warning, "IdentifyAccount", gameId,
                throttleTransient: true);
            throw new GogPlaytimeQueueException(GogPlaytimeQueueFailure.AccountUnavailable);
        }

        var pendingSession = new PendingSession
        {
            AccountIdentifierHash = HashAccountIdentifier(userId.Trim()),
            GameId = gameId!,
            SessionDate = startedAtUtc.ToUniversalTime().ToUnixTimeSeconds(),
            TimeMinutes = timeMinutes
        };
        if (!IsValidSession(pendingSession))
            return;

        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pendingSessions = await LoadQueueAsync("Enqueue", gameId, cancellationToken).ConfigureAwait(false);
            var previousCount = pendingSessions.Count;
            try
            {
                AddOrUpdateSession(pendingSessions, pendingSession);
            }
            catch (GogPlaytimeQueueException ex) when (ex.Failure == GogPlaytimeQueueFailure.Capacity)
            {
                ReportFailure("GogPlaytime.QueueCapacityExceeded", ErrorSeverity.Warning, "Enqueue", gameId, previousCount);
                throw;
            }
            await SaveQueueAsync(pendingSessions, "Enqueue", gameId, previousCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    public void RequestPendingSync()
    {
        lock (_backgroundSyncLock)
        {
            _backgroundSyncRequested = true;
            if (_backgroundSyncTask is { IsCompleted: false })
                return;

            _backgroundSyncTask = Task.Run(ProcessBackgroundSyncRequestsAsync);
        }
    }

    public async Task TrySyncPendingAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PendingSession> pendingSessions;
            await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                pendingSessions = await LoadQueueAsync("Sync", null, cancellationToken).ConfigureAwait(false);
            }
            catch (GogPlaytimeQueueException)
            {
                // An unreadable queue is never an empty queue and must not lead to uploads or writes.
                return;
            }
            finally
            {
                _queueGate.Release();
            }

            var queuedIdentities = pendingSessions.Select(GetIdentity).ToHashSet();
            foreach (var identity in _confirmedUploads.Keys.Where(key => !queuedIdentities.Contains(key)).ToArray())
                _confirmedUploads.Remove(identity);
            if (pendingSessions.Count == 0)
                return;

            GogGameplayCredentials? credentials;
            try
            {
                credentials = await _getCredentialsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ReportFailure("GogPlaytime.UploadFailed", ErrorSeverity.Warning, "Credentials",
                    pendingCount: pendingSessions.Count, exception: ex, throttleTransient: true);
                return;
            }

            if (credentials == null)
                return;

            var accountIdentifierHash = HashAccountIdentifier(credentials.UserId.Trim());
            var matchingSessions = pendingSessions
                .Where(session => string.Equals(
                    session.AccountIdentifierHash,
                    accountIdentifierHash,
                    StringComparison.Ordinal))
                .DistinctBy(GetIdentity)
                .OrderBy(session => session.SessionDate)
                .Take(MaximumSyncAttemptsPerRun)
                .ToArray();
            foreach (var session in matchingSessions)
            {
                var identity = GetIdentity(session);
                if (!_confirmedUploads.TryGetValue(identity, out var confirmedSession))
                {
                    try
                    {
                        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        requestCts.CancelAfter(RequestTimeout);
                        await _apiClient.PostSessionAsync(session.GameId, credentials.UserId, credentials.AccessToken,
                            session.SessionDate, session.TimeMinutes, requestCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (GogPlaytimeApiException ex) when (ex.StatusCode is not null and
                                                             not HttpStatusCode.Unauthorized and
                                                             not HttpStatusCode.Forbidden and
                                                             not HttpStatusCode.TooManyRequests)
                    {
                        // Preserve per-session rejection behavior: later different sessions may still upload.
                        ReportFailure("GogPlaytime.UploadFailed", ErrorSeverity.Warning, "Upload", session.GameId,
                            pendingSessions.Count, ex, ex.StatusCode, throttleTransient: IsTransientFailure(ex));
                        continue;
                    }
                    catch (Exception ex)
                    {
                        ReportFailure("GogPlaytime.UploadFailed", ErrorSeverity.Warning, "Upload", session.GameId,
                            pendingSessions.Count, ex, GetHttpStatus(ex), throttleTransient: IsTransientFailure(ex));
                        break;
                    }

                    // The API client returns normally only for HTTP 201. Record that fact BEFORE local IO/cancellation.
                    confirmedSession = session;
                    _confirmedUploads[identity] = confirmedSession;
                }

                try
                {
                    await RemoveUploadedSessionAsync(confirmedSession, cancellationToken).ConfigureAwait(false);
                    _confirmedUploads.Remove(identity);
                }
                catch (Exception ex)
                {
                    ReportFailure("GogPlaytime.QueueUpdateAfterUploadFailed", ErrorSeverity.Error, "RemoveUploadedSession",
                        confirmedSession.GameId, pendingSessions.Count, ex, HttpStatusCode.Created, uploadConfirmed: true);
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                        throw;
                    // Stop this run. Later runs in THIS instance retry only the local commit, never this POST.
                    break;
                }
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task ProcessBackgroundSyncRequestsAsync()
    {
        while (true)
        {
            lock (_backgroundSyncLock)
            {
                if (!_backgroundSyncRequested)
                {
                    _backgroundSyncTask = null;
                    return;
                }

                _backgroundSyncRequested = false;
            }

            try
            {
                await TrySyncPendingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportFailure("GogPlaytime.UploadFailed", ErrorSeverity.Warning, "BackgroundSync",
                    exception: ex, throttleTransient: true);
            }
        }
    }

    private async Task RemoveUploadedSessionAsync(
        PendingSession uploadedSession,
        CancellationToken cancellationToken)
    {
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pendingSessions = await LoadQueueAsync("RemoveUploadedSession", uploadedSession.GameId, cancellationToken).ConfigureAwait(false);
            var previousCount = pendingSessions.Count;
            // A concurrent observation may have increased this session's duration. Do not discard it,
            // invent a delta upload, or assume the server replaces/deduplicates sessions.
            if (pendingSessions.Any(session => GetIdentity(session) == GetIdentity(uploadedSession) &&
                                               session.TimeMinutes != uploadedSession.TimeMinutes))
                throw new GogPlaytimeQueueException(GogPlaytimeQueueFailure.SessionChangedAfterUpload);
            var removed = pendingSessions.RemoveAll(session =>
                string.Equals(
                    session.AccountIdentifierHash,
                    uploadedSession.AccountIdentifierHash,
                    StringComparison.Ordinal) &&
                string.Equals(session.GameId, uploadedSession.GameId, StringComparison.Ordinal) &&
                session.SessionDate == uploadedSession.SessionDate &&
                session.TimeMinutes == uploadedSession.TimeMinutes);
            if (removed == 0)
                return;

            await SaveQueueAsync(pendingSessions, "RemoveUploadedSession", uploadedSession.GameId, previousCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _queueGate.Release();
        }
    }

    private static int GetWholeMinutes(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || !double.IsFinite(duration.TotalMinutes))
            return 0;

        return (int)Math.Min(Math.Floor(duration.TotalMinutes), MaximumSessionMinutes);
    }

    private static void AddOrUpdateSession(List<PendingSession> sessions, PendingSession pendingSession)
    {
        var existing = sessions.FirstOrDefault(session =>
            string.Equals(
                session.AccountIdentifierHash,
                pendingSession.AccountIdentifierHash,
                StringComparison.Ordinal) &&
            string.Equals(session.GameId, pendingSession.GameId, StringComparison.Ordinal) &&
            session.SessionDate == pendingSession.SessionDate);
        if (existing != null)
        {
            existing.TimeMinutes = Math.Max(existing.TimeMinutes, pendingSession.TimeMinutes);
            return;
        }

        if (sessions.Count >= MaximumPendingSessions)
            throw new GogPlaytimeQueueException(GogPlaytimeQueueFailure.Capacity);
        sessions.Add(pendingSession);
    }

    private async Task<List<PendingSession>> LoadQueueAsync(
        string operation, string? gameId, CancellationToken cancellationToken)
    {
        try
        {
            Stream opened;
            try
            {
                // File.Exists also returns false for access errors: only an actual not-found open is empty.
                opened = _openQueueRead(_queuePath);
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            await using var stream = opened;
            var buffer = new byte[MaximumQueueBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            if (length is <= 0 or > MaximumQueueBytes)
                throw new InvalidDataException("The GOG playtime queue has an invalid size.");
            // Stream deserialization accepted UTF-8 BOMs; retain compatibility for existing valid files.
            var offset = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            var document = JsonSerializer.Deserialize<QueueDocument>(buffer.AsSpan(offset, length - offset), JsonOptions);
            if (document == null ||
                document.SchemaVersion != QueueSchemaVersion ||
                document.Sessions == null ||
                document.Sessions.Count > MaximumPendingSessions ||
                document.Sessions.Any(session => !IsValidSession(session)))
            {
                throw new InvalidDataException("The GOG playtime queue is invalid.");
            }

            return document.Sessions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   JsonException or
                                   InvalidDataException)
        {
            ReportFailure("GogPlaytime.QueueReadFailed", ErrorSeverity.Error, operation, gameId, exception: ex);
            throw new GogPlaytimeQueueException(GogPlaytimeQueueFailure.Read, ex);
        }
    }

    private async Task SaveQueueAsync(
        IReadOnlyCollection<PendingSession> sessions,
        string operation, string? gameId, int previousCount,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(_queuePath)!;
        var tempPath = _queuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessions.Count == 0)
            {
                // This is the commit for removal of the final uploaded session, not best-effort temp cleanup.
                _deleteQueue(_queuePath);
                return;
            }
            Directory.CreateDirectory(targetDirectory);
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer
                    .SerializeAsync(
                        stream,
                        new QueueDocument
                        {
                            SchemaVersion = QueueSchemaVersion,
                            Sessions = sessions.ToList()
                        },
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            RestrictQueuePermissionsBestEffort(tempPath);
            cancellationToken.ThrowIfCancellationRequested();
            _replaceQueue(tempPath, _queuePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportFailure("GogPlaytime.QueueWriteFailed", ErrorSeverity.Error, operation, gameId, previousCount, ex);
            throw new GogPlaytimeQueueException(GogPlaytimeQueueFailure.Write, ex);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void RestrictQueuePermissionsBestEffort(string path)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // The queue contains no credentials; failure to tighten permissions must not lose it.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stale cache file is harmless and can be retried later.
        }
    }

    private static bool IsValidSession(PendingSession? session) =>
        session != null &&
        session.AccountIdentifierHash is { Length: 64 } hash &&
        hash.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        IsPositiveNumericIdentifier(session.GameId) &&
        session.SessionDate > 0 &&
        session.TimeMinutes is >= 1 and <= MaximumSessionMinutes;

    private static bool IsPositiveNumericIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 32 &&
        value.All(character => character is >= '0' and <= '9') &&
        value.Any(character => character != '0');

    private static string HashAccountIdentifier(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static SessionIdentity GetIdentity(PendingSession session) =>
        new(session.AccountIdentifierHash, session.GameId, session.SessionDate);

    private static HttpStatusCode? GetHttpStatus(Exception exception) => exception switch
    {
        GogPlaytimeApiException apiException => apiException.StatusCode,
        HttpRequestException requestException => requestException.StatusCode,
        _ => null
    };

    private static bool IsTransientFailure(Exception exception)
    {
        var status = GetHttpStatus(exception);
        if (status.HasValue)
            return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status.Value >= 500;
        return exception is GogPlaytimeApiException or HttpRequestException or OperationCanceledException or TimeoutException;
    }

    private void ReportFailure(string code, ErrorSeverity severity, string operation, string? gameId = null,
        int? pendingCount = null, Exception? exception = null, HttpStatusCode? httpStatus = null,
        bool uploadConfirmed = false, bool throttleTransient = false)
    {
        try
        {
            var suppressed = 0;
            if (throttleTransient)
            {
                lock (_diagnosticLock)
                {
                    var now = _timeProvider.GetTimestamp();
                    if (_transientUploadDiagnostics.TryGetValue(operation, out var previous))
                    {
                        if (_timeProvider.GetElapsedTime(previous.Timestamp, now) < TimeSpan.FromMinutes(1))
                        {
                            _transientUploadDiagnostics[operation] = (previous.Timestamp, Math.Min(previous.Suppressed + 1, int.MaxValue - 1));
                            return;
                        }
                        suppressed = previous.Suppressed;
                    }
                    _transientUploadDiagnostics[operation] = (now, 0);
                }
            }

            var context = new Dictionary<string, string> { ["Operation"] = operation };
            if (gameId != null) context["GameId"] = gameId;
            if (pendingCount.HasValue) context["PendingSessionCount"] = pendingCount.Value.ToString(CultureInfo.InvariantCulture);
            if (httpStatus.HasValue) context["HttpStatus"] = ((int)httpStatus.Value).ToString(CultureInfo.InvariantCulture);
            if (exception != null) context["ExceptionType"] = exception.GetType().Name;
            if (exception is GogPlaytimeQueueException queueException) context["QueueFailure"] = queueException.Failure.ToString();
            if (suppressed > 0) context["SuppressedReports"] = suppressed.ToString(CultureInfo.InvariantCulture);
            if (uploadConfirmed)
            {
                context["UploadConfirmed"] = "True";
                context["RetryPolicy"] = "LocalQueueUpdateOnlyUntilRestart";
            }

            _errorService.Report(new ErrorReport(code, severity, nameof(GogPlaytimeSyncService), code switch
            {
                "GogPlaytime.QueueReadFailed" => "The existing GOG playtime queue could not be read or validated. It was preserved.",
                "GogPlaytime.QueueWriteFailed" => "The GOG playtime queue could not be saved or deleted. The previous queue was preserved.",
                "GogPlaytime.QueueCapacityExceeded" => "The GOG playtime queue is full. The new session was not queued; no existing sessions were discarded.",
                "GogPlaytime.QueueUpdateAfterUploadFailed" => "GOG confirmed the upload, but the local queue update failed. This instance will retry only the local update. Restarting can upload the session again.",
                _ => "GOG playtime synchronization was not confirmed. Pending sessions were preserved."
            }, Context: context));
            // Deliberately omit exception payloads: HTTP/auth exceptions can embed account IDs,
            // access tokens, URLs containing either, or untrusted JSON property paths.
        }
        catch
        {
            // Output must never replace the original persistence failure or affect queue state.
        }
    }

    private readonly record struct SessionIdentity(string AccountIdentifierHash, string GameId, long SessionDate);

    private sealed class QueueDocument
    {
        public int SchemaVersion { get; set; }
        public List<PendingSession>? Sessions { get; set; }
    }

    private sealed class PendingSession
    {
        public string AccountIdentifierHash { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
        public long SessionDate { get; set; }
        public int TimeMinutes { get; set; }
    }
}
