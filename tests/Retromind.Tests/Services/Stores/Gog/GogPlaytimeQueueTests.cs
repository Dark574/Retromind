using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Gog.Playtime;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogPlaytimeQueueTests
{
    private const string UserId = "46988829925743611";
    private const string AccessToken = "private-access-token";
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 25, 12, 30, 45, TimeSpan.Zero);

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":2,\"Sessions\":[]}")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"SchemaVersion\":1,\"Sessions\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Sessions\":[null]}")]
    [InlineData("{\"SchemaVersion\":1,\"Sessions\":[{\"AccountIdentifierHash\":null}]}")]
    [InlineData("")]
    public async Task InvalidQueue_IsPreservedByEnqueueAndSync(string content)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        await File.WriteAllTextAsync(path, content);
        var errors = new RecordingErrorService();
        var api = new TestApiClient();
        var credentialsRequested = false;
        var service = new GogPlaytimeSyncService(_ =>
            {
                credentialsRequested = true;
                return Task.FromResult<GogGameplayCredentials?>(new(AccessToken, UserId));
            }, _ => Task.FromResult<string?>(UserId), api, path, errors);

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service));
        await service.TrySyncPendingAsync();

        Assert.Equal(GogPlaytimeQueueFailure.Read, failure.Failure);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal(0, api.Attempts);
        Assert.False(credentialsRequested);
        Assert.Equal(2, errors.Reports.Count);
        Assert.All(errors.Reports, report => Assert.Equal("GogPlaytime.QueueReadFailed", report.Code));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp"));
    }

    [Theory]
    [InlineData(257)]
    [InlineData(2000)]
    public async Task ExcessiveQueue_IsPreserved(int count)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path, count);
        var service = CreateService(path);

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service));

        Assert.Equal(GogPlaytimeQueueFailure.Read, failure.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MissingReadPermission_IsNotTreatedAsMissingFile()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        var errors = new RecordingErrorService();
        var api = new TestApiClient();
        var service = CreateService(path, api, errors,
            openRead: _ => throw new UnauthorizedAccessException("access denied"));

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service));
        await service.TrySyncPendingAsync();

        Assert.Equal(GogPlaytimeQueueFailure.Read, failure.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, api.Attempts);
        Assert.All(errors.Reports, report => Assert.Equal("GogPlaytime.QueueReadFailed", report.Code));
    }

    [Fact]
    public async Task ReplaceFailure_FaultsEnqueueAndPreservesPreviousQueue()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = CreateService(path, errors: errors,
            replace: (_, _) => throw new IOException("disk failure"));

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service, "200"));

        Assert.Equal(GogPlaytimeQueueFailure.Write, failure.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp"));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("GogPlaytime.QueueWriteFailed", report.Code);
        Assert.Equal(ErrorSeverity.Error, report.Severity);
        Assert.Equal("1", report.Context!["PendingSessionCount"]);
    }

    [Fact]
    public async Task FirstWriteFailure_DoesNotClaimSessionWasSaved()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var service = CreateService(path, replace: (_, _) => throw new UnauthorizedAccessException());

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service));

        Assert.Equal(GogPlaytimeQueueFailure.Write, failure.Failure);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(temp.RootPath, "*.tmp"));
    }

    [Fact]
    public async Task DeleteFailure_AfterConfirmedUpload_RetriesOnlyLocalCommit()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        var failDelete = true;
        var api = new TestApiClient();
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var service = CreateService(path, api, errors, delete: target =>
        {
            if (failDelete) throw new IOException("disk failure");
            File.Delete(target);
        });

        await service.TrySyncPendingAsync();
        await service.TrySyncPendingAsync();

        Assert.Equal(1, api.Attempts);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, errors.Reports.Count(report => report.Code == "GogPlaytime.QueueWriteFailed"));
        Assert.All(errors.Reports.Where(report => report.Code == "GogPlaytime.QueueUpdateAfterUploadFailed"), report =>
        {
            Assert.Equal(ErrorSeverity.Error, report.Severity);
            Assert.Equal("201", report.Context!["HttpStatus"]);
            Assert.Equal("True", report.Context["UploadConfirmed"]);
        });
        Assert.Equal(2, errors.Reports.Count(report => report.Code == "GogPlaytime.QueueUpdateAfterUploadFailed"));
        Assert.DoesNotContain(errors.Reports, report => report.Code == "GogPlaytime.UploadFailed");

        failDelete = false;
        await service.TrySyncPendingAsync();

        Assert.Equal(1, api.Attempts);
        Assert.False(File.Exists(path));
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Fact]
    public async Task ReplaceFailure_AfterConfirmedUpload_StopsBatchAndPreservesOtherSessions()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path, 2);
        var api = new TestApiClient();
        var failReplace = true;
        var service = CreateService(path, api, replace: (source, target) =>
        {
            if (failReplace) throw new IOException();
            File.Move(source, target, overwrite: true);
        });

        await service.TrySyncPendingAsync();
        await service.TrySyncPendingAsync();

        Assert.Equal(1, api.Attempts);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        failReplace = false;
        await service.TrySyncPendingAsync();

        Assert.Equal(new[] { "100", "101" }, api.PostedGameIds);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task QueueBecomesUnreadableAfterUpload_IsPreservedAndNotPostedAgain()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        var errors = new RecordingErrorService();
        var api = new TestApiClient { OnPost = _ => File.WriteAllTextAsync(path, "{broken") };
        var service = CreateService(path, api, errors);

        await service.TrySyncPendingAsync();
        await service.TrySyncPendingAsync();

        Assert.Equal("{broken", await File.ReadAllTextAsync(path));
        Assert.Contains(errors.Reports, report => report.Code == "GogPlaytime.QueueReadFailed");
        Assert.Contains(errors.Reports, report => report.Code == "GogPlaytime.QueueUpdateAfterUploadFailed");
        // Only an external repair restores the original bytes; the service never repairs the queue itself.
        await File.WriteAllBytesAsync(path, original);
        await service.TrySyncPendingAsync();
        Assert.Equal(1, api.Attempts);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FullQueue_RejectsNewSessionButAllowsUpdatingExistingSession()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path, 256);
        var errors = new RecordingErrorService();
        var service = CreateService(path, errors: errors);

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service, "999"));

        Assert.Equal(GogPlaytimeQueueFailure.Capacity, failure.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        var report = Assert.Single(errors.Reports);
        Assert.Equal("GogPlaytime.QueueCapacityExceeded", report.Code);
        Assert.Equal("256", report.Context!["PendingSessionCount"]);
        await Enqueue(service, "100", minutes: 6);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var sessions = json.RootElement.GetProperty("Sessions");
        Assert.Equal(256, sessions.GetArrayLength());
        Assert.Equal(6, sessions[0].GetProperty("TimeMinutes").GetInt32());
        Assert.Equal("355", sessions[255].GetProperty("GameId").GetString());
    }

    [Fact]
    public async Task TransientHttpFailure_IsThrottledWithoutChangingRetryBehaviorOrQueue()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        var clock = new TestTimeProvider();
        var errors = new RecordingErrorService();
        var api = new TestApiClient
        {
            OnPost = _ => throw new GogPlaytimeApiException(HttpStatusCode.ServiceUnavailable, "unavailable")
        };
        var service = CreateService(path, api, errors, clock: clock);

        for (var i = 0; i < 5; i++) await service.TrySyncPendingAsync();

        Assert.Single(errors.Reports);
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.TrySyncPendingAsync();

        Assert.Equal(6, api.Attempts);
        Assert.Equal(2, errors.Reports.Count);
        var report = errors.Reports.Last();
        Assert.Equal("GogPlaytime.UploadFailed", report.Code);
        Assert.Equal(ErrorSeverity.Warning, report.Severity);
        Assert.Equal("503", report.Context!["HttpStatus"]);
        Assert.Equal("4", report.Context["SuppressedReports"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(400, 2)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(429, 1)]
    public async Task HttpRejection_PreservesExistingBatchBehavior(int status, int attempts)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path, 2);
        var api = new TestApiClient { OnPost = _ => throw new GogPlaytimeApiException((HttpStatusCode)status, "rejected") };
        await CreateService(path, api).TrySyncPendingAsync();
        Assert.Equal(attempts, api.Attempts);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Diagnostics_ExcludeCredentialsAndAccountIdentifiersEvenFromExceptionPayloads()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        WriteLegacyQueue(path);
        var errors = new RecordingErrorService { ThrowOnReport = true };
        var api = new TestApiClient
        {
            OnPost = _ => throw new HttpRequestException($"https://example/users/{UserId}?access_token={AccessToken}")
        };

        await CreateService(path, api, errors).TrySyncPendingAsync();

        var report = Assert.Single(errors.Reports);
        Assert.Null(report.Exception);
        Assert.Equal(nameof(GogPlaytimeSyncService), report.Component);
        Assert.Equal("100", report.Context!["GameId"]);
        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(UserId, serialized);
        Assert.DoesNotContain(AccessToken, serialized);
        Assert.DoesNotContain(AccountHash, serialized);
        Assert.True(File.Exists(path));
        Assert.All(errors.Notifications, Assert.Null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSchemaOneQueue_RemainsReadable(bool bom)
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var bytes = WriteLegacyQueue(path);
        if (bom) await File.WriteAllBytesAsync(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray());
        var api = new TestApiClient();
        var errors = new RecordingErrorService();

        await CreateService(path, api, errors).TrySyncPendingAsync();

        Assert.Equal(new[] { "100" }, api.PostedGameIds);
        Assert.Empty(errors.Reports);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ConcurrentEnqueueOfSameSessionDuringUpload_DoesNotDiscardChangedDurationOrRepost()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        WriteLegacyQueue(path);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new TestApiClient { OnPost = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var errors = new RecordingErrorService();
        var service = CreateService(path, api, errors);
        var sync = service.TrySyncPendingAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Enqueue(service, minutes: 6).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
        await sync;
        await service.TrySyncPendingAsync();

        Assert.Equal(1, api.Attempts);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(6, json.RootElement.GetProperty("Sessions")[0].GetProperty("TimeMinutes").GetInt32());
        Assert.All(errors.Reports, report =>
        {
            Assert.Equal("GogPlaytime.QueueUpdateAfterUploadFailed", report.Code);
            Assert.Equal("SessionChangedAfterUpload", report.Context!["QueueFailure"]);
        });
        Assert.Equal(2, errors.Reports.Count);
    }

    [Fact]
    public async Task CancellationAfterRemoteConfirmation_RetainsLocalCommitMarker()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        using var cts = new CancellationTokenSource();
        var errors = new RecordingErrorService();
        var api = new TestApiClient { OnPost = _ => { cts.Cancel(); return Task.CompletedTask; } };
        var service = CreateService(path, api, errors);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TrySyncPendingAsync(cts.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal("GogPlaytime.QueueUpdateAfterUploadFailed", Assert.Single(errors.Reports).Code);
        await service.TrySyncPendingAsync();
        Assert.Equal(1, api.Attempts);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CancellationBeforeRemoteConfirmation_DoesNotReportFailureOrRemoveSession()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        using var cts = new CancellationTokenSource();
        var errors = new RecordingErrorService();
        var api = new TestApiClient { OnPost = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(path, api, errors).TrySyncPendingAsync(cts.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task UnavailableAccount_FaultsEligibleEnqueueWithoutChangingQueue()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var original = WriteLegacyQueue(path);
        var service = new GogPlaytimeSyncService(_ => Task.FromResult<GogGameplayCredentials?>(null),
            _ => Task.FromResult<string?>(null), new TestApiClient(), path, new RecordingErrorService());

        var failure = await Assert.ThrowsAsync<GogPlaytimeQueueException>(() => Enqueue(service));

        Assert.Equal(GogPlaytimeQueueFailure.AccountUnavailable, failure.Failure);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InvalidProductId_IsExcludedBeforeAccountLookupOrDiagnostics()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        var errors = new RecordingErrorService();
        var service = new GogPlaytimeSyncService(_ => Task.FromResult<GogGameplayCredentials?>(null),
            _ => throw new InvalidOperationException("Account lookup must not run"), new TestApiClient(), path, errors);

        await Enqueue(service, $"token={AccessToken}");

        Assert.Empty(errors.Reports);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ConfirmedUploadWithFailedLocalCommit_CanBePostedAgainAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        WriteLegacyQueue(path);
        var api = new TestApiClient();
        var firstInstance = CreateService(path, api, delete: _ => throw new IOException());
        await firstInstance.TrySyncPendingAsync();
        await firstInstance.TrySyncPendingAsync();
        Assert.Equal(1, api.Attempts);

        // The in-memory marker deliberately makes no exactly-once promise across service restarts.
        await CreateService(path, api).TrySyncPendingAsync();

        Assert.Equal(2, api.Attempts);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DuplicateIdenticalLegacyRows_AreUploadedOnceAndRemovedTogether()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("pending.json");
        WriteLegacyQueue(path);
        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            var row = json.RootElement.GetProperty("Sessions")[0].GetRawText();
            await File.WriteAllTextAsync(path, $"{{\"SchemaVersion\":1,\"Sessions\":[{row},{row}]}}");
        }
        var api = new TestApiClient();
        var errors = new RecordingErrorService();

        await CreateService(path, api, errors).TrySyncPendingAsync();

        Assert.Equal(1, api.Attempts);
        Assert.False(File.Exists(path));
        Assert.Empty(errors.Reports);
    }

    private static string AccountHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(UserId))).ToLowerInvariant();

    private static byte[] WriteLegacyQueue(string path, int count = 1)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            Sessions = Enumerable.Range(0, count).Select(i => new
            {
                AccountIdentifierHash = AccountHash,
                GameId = (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SessionDate = StartedAt.ToUnixTimeSeconds() + i,
                TimeMinutes = 5
            }).ToArray()
        });
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private static Task Enqueue(GogPlaytimeSyncService service, string gameId = "100", int minutes = 5)
    {
        var item = new MediaItem("GOG game");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        return service.EnqueueSessionAsync(item, StartedAt, TimeSpan.FromMinutes(minutes));
    }

    private static GogPlaytimeSyncService CreateService(string path, TestApiClient? api = null,
        RecordingErrorService? errors = null, Func<string, Stream>? openRead = null,
        Action<string, string>? replace = null, Action<string>? delete = null, TimeProvider? clock = null) =>
        new(_ => Task.FromResult<GogGameplayCredentials?>(new(AccessToken, UserId)),
            _ => Task.FromResult<string?>(UserId), api ?? new TestApiClient(), path,
            errors ?? new RecordingErrorService(), openRead, replace, delete, clock);

    private sealed class TestApiClient : IGogPlaytimeApiClient
    {
        internal int Attempts { get; private set; }
        internal List<string> PostedGameIds { get; } = [];
        internal Func<CancellationToken, Task>? OnPost { get; init; }

        public Task PostSessionAsync(string gameId, string userId, string accessToken, long sessionDate,
            int timeMinutes, CancellationToken cancellationToken = default)
        {
            Attempts++;
            PostedGameIds.Add(gameId);
            return OnPost?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
