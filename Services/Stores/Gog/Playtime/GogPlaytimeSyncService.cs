using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    internal GogPlaytimeSyncService(
        GogAuthService authService,
        IGogPlaytimeApiClient apiClient,
        string queuePath)
    {
        ArgumentNullException.ThrowIfNull(authService);
        _getCredentialsAsync = authService.GetGameplayCredentialsAsync;
        _getUserIdAsync = authService.GetGameplayUserIdAsync;
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(queuePath);
        _queuePath = Path.GetFullPath(queuePath);
    }

    internal GogPlaytimeSyncService(
        Func<CancellationToken, Task<GogGameplayCredentials?>> getCredentialsAsync,
        Func<CancellationToken, Task<string?>> getUserIdAsync,
        IGogPlaytimeApiClient apiClient,
        string queuePath)
    {
        _getCredentialsAsync = getCredentialsAsync ??
                               throw new ArgumentNullException(nameof(getCredentialsAsync));
        _getUserIdAsync = getUserIdAsync ?? throw new ArgumentNullException(nameof(getUserIdAsync));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(queuePath);
        _queuePath = Path.GetFullPath(queuePath);
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

    public async Task ReportSessionAsync(
        MediaItem item,
        DateTimeOffset startedAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var gameId = GogMediaItemStateHelper.TryGetGameId(item)?.Trim();
        var timeMinutes = GetWholeMinutes(duration);
        if (gameId == null || timeMinutes < 1)
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
            Debug.WriteLine($"[GOG] Could not identify the account for playtime sync: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            Debug.WriteLine("[GOG] Playtime session was not queued because no gameplay account is available.");
            return;
        }

        var pendingSession = new PendingSession
        {
            AccountIdentifierHash = HashAccountIdentifier(userId.Trim()),
            GameId = gameId,
            SessionDate = startedAtUtc.ToUniversalTime().ToUnixTimeSeconds(),
            TimeMinutes = timeMinutes
        };
        if (!IsValidSession(pendingSession))
            return;

        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pendingSessions = await LoadQueueAsync(cancellationToken).ConfigureAwait(false);
            AddOrUpdateSession(pendingSessions, pendingSession);
            await SaveQueueAsync(pendingSessions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _queueGate.Release();
        }

        await TrySyncPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task TrySyncPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_queuePath))
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
            Debug.WriteLine($"[GOG] Playtime sync credentials are unavailable: {ex.Message}");
            return;
        }

        if (credentials == null)
            return;

        var accountIdentifierHash = HashAccountIdentifier(credentials.UserId.Trim());
        await _queueGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pendingSessions = await LoadQueueAsync(cancellationToken).ConfigureAwait(false);
            var matchingSessions = pendingSessions
                .Where(session => string.Equals(
                    session.AccountIdentifierHash,
                    accountIdentifierHash,
                    StringComparison.Ordinal))
                .OrderBy(session => session.SessionDate)
                .Take(MaximumSyncAttemptsPerRun)
                .ToArray();
            if (matchingSessions.Length == 0)
                return;

            var changed = false;
            foreach (var session in matchingSessions)
            {
                try
                {
                    using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestCts.CancelAfter(RequestTimeout);
                    await _apiClient
                        .PostSessionAsync(
                            session.GameId,
                            credentials.UserId,
                            credentials.AccessToken,
                            session.SessionDate,
                            session.TimeMinutes,
                            requestCts.Token)
                        .ConfigureAwait(false);
                    pendingSessions.Remove(session);
                    changed = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (GogPlaytimeApiException ex) when (ex.StatusCode is not null and
                                                         not System.Net.HttpStatusCode.Unauthorized and
                                                         not System.Net.HttpStatusCode.Forbidden and
                                                         not System.Net.HttpStatusCode.TooManyRequests)
                {
                    // A rejection tied to one game/session must not block later valid entries.
                    Debug.WriteLine(
                        $"[GOG] A pending playtime session was rejected (HTTP {(int)ex.StatusCode.Value}).");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[GOG] Pending playtime session could not be synchronized ({ex.GetType().Name}).");
                    break;
                }
            }

            if (changed)
                await SaveQueueAsync(pendingSessions, cancellationToken).ConfigureAwait(false);
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

        sessions.Add(pendingSession);
        if (sessions.Count <= MaximumPendingSessions)
            return;

        sessions.Sort((left, right) => left.SessionDate.CompareTo(right.SessionDate));
        sessions.RemoveRange(0, sessions.Count - MaximumPendingSessions);
    }

    private async Task<List<PendingSession>> LoadQueueAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_queuePath))
            return [];

        try
        {
            var fileInfo = new FileInfo(_queuePath);
            if (fileInfo.Length is <= 0 or > MaximumQueueBytes)
                throw new InvalidDataException("The GOG playtime queue has an invalid size.");

            await using var stream = new FileStream(
                _queuePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer
                .DeserializeAsync<QueueDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
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
            Debug.WriteLine($"[GOG] Ignoring invalid playtime queue: {ex.Message}");
            return [];
        }
    }

    private async Task SaveQueueAsync(
        IReadOnlyCollection<PendingSession> sessions,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            TryDeleteQueueFile();
            return;
        }

        var targetDirectory = Path.GetDirectoryName(_queuePath)!;
        var tempPath = _queuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
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
            File.Move(tempPath, _queuePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFile(tempPath);
            Debug.WriteLine($"[GOG] Could not persist the playtime queue: {ex.Message}");
        }
    }

    private void TryDeleteQueueFile() => TryDeleteFile(_queuePath);

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
        session.AccountIdentifierHash.Length == 64 &&
        session.AccountIdentifierHash.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        IsPositiveNumericIdentifier(session.GameId) &&
        session.SessionDate > 0 &&
        session.TimeMinutes is >= 1 and <= MaximumSessionMinutes;

    private static bool IsPositiveNumericIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 32 &&
        value.All(character => character is >= '0' and <= '9') &&
        value.Any(character => character != '0');

    private static string HashAccountIdentifier(string userId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class QueueDocument
    {
        public int SchemaVersion { get; set; }
        public List<PendingSession>? Sessions { get; set; } = [];
    }

    private sealed class PendingSession
    {
        public string AccountIdentifierHash { get; set; } = string.Empty;
        public string GameId { get; set; } = string.Empty;
        public long SessionDate { get; set; }
        public int TimeMinutes { get; set; }
    }
}
