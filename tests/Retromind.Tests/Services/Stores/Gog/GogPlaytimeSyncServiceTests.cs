using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Gog.Playtime;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogPlaytimeSyncServiceTests
{
    [Fact]
    public async Task EnqueueSessionAsync_PersistsBeforeExplicitUpload()
    {
        using var temp = new TemporaryDirectory();
        var queuePath = temp.GetPath("cache", "pending-playtime-sessions.json");
        var apiClient = new StubApiClient();
        var service = CreateService(
            apiClient,
            queuePath,
            userId: "46988829925743611",
            accessToken: "access-token");
        var startedAt = new DateTimeOffset(2026, 10, 25, 12, 30, 45, TimeSpan.Zero);

        await service.EnqueueSessionAsync(
            CreateGogItem("1994983163"),
            startedAt,
            TimeSpan.FromSeconds(179));

        Assert.Empty(apiClient.Posts);
        Assert.True(File.Exists(queuePath));

        await service.TrySyncPendingAsync();

        var post = Assert.Single(apiClient.Posts);
        Assert.Equal("1994983163", post.GameId);
        Assert.Equal("46988829925743611", post.UserId);
        Assert.Equal("access-token", post.AccessToken);
        Assert.Equal(startedAt.ToUnixTimeSeconds(), post.SessionDate);
        Assert.Equal(2, post.TimeMinutes);
        Assert.False(File.Exists(queuePath));
    }

    [Fact]
    public async Task EnqueueSessionAsync_SkipsSessionsShorterThanOneMinute()
    {
        using var temp = new TemporaryDirectory();
        var userIdCalls = 0;
        var apiClient = new StubApiClient();
        var service = new GogPlaytimeSyncService(
            _ => Task.FromResult<GogGameplayCredentials?>(null),
            _ =>
            {
                userIdCalls++;
                return Task.FromResult<string?>("46988829925743611");
            },
            apiClient,
            temp.GetPath("pending.json"), new RecordingErrorService());

        await service.EnqueueSessionAsync(
            CreateGogItem("1994983163"),
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(59));

        Assert.Equal(0, userIdCalls);
        Assert.Empty(apiClient.Posts);
        Assert.False(File.Exists(temp.GetPath("pending.json")));
    }

    [Fact]
    public async Task TrySyncPendingAsync_DoesNotRequestCredentialsWithoutQueue()
    {
        using var temp = new TemporaryDirectory();
        var credentialCalls = 0;
        var service = new GogPlaytimeSyncService(
            _ =>
            {
                credentialCalls++;
                return Task.FromResult<GogGameplayCredentials?>(null);
            },
            _ => Task.FromResult<string?>(null),
            new StubApiClient(),
            temp.GetPath("missing-pending.json"), new RecordingErrorService());

        await service.TrySyncPendingAsync();

        Assert.Equal(0, credentialCalls);
    }

    [Fact]
    public async Task FailedUploadRemainsQueuedAndIsRetriedAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        var queuePath = temp.GetPath("cache", "pending-playtime-sessions.json");
        var failingClient = new StubApiClient { Failure = new HttpRequestException("offline") };
        var firstService = CreateService(
            failingClient,
            queuePath,
            userId: "46988829925743611",
            accessToken: "first-token");
        var startedAt = new DateTimeOffset(2026, 10, 25, 12, 30, 45, TimeSpan.Zero);

        await firstService.EnqueueSessionAsync(
            CreateGogItem("1994983163"),
            startedAt,
            TimeSpan.FromMinutes(12));
        await firstService.TrySyncPendingAsync();

        Assert.True(File.Exists(queuePath));
        var persistedQueue = await File.ReadAllTextAsync(queuePath);
        Assert.DoesNotContain("46988829925743611", persistedQueue, StringComparison.Ordinal);
        Assert.DoesNotContain("first-token", persistedQueue, StringComparison.Ordinal);

        var retryClient = new StubApiClient();
        var restartedService = CreateService(
            retryClient,
            queuePath,
            userId: "46988829925743611",
            accessToken: "refreshed-token");

        await restartedService.TrySyncPendingAsync();

        var retriedPost = Assert.Single(retryClient.Posts);
        Assert.Equal("refreshed-token", retriedPost.AccessToken);
        Assert.Equal(startedAt.ToUnixTimeSeconds(), retriedPost.SessionDate);
        Assert.Equal(12, retriedPost.TimeMinutes);
        Assert.False(File.Exists(queuePath));
    }

    [Fact]
    public async Task PendingSessionsAreNotSentThroughAnotherAccount()
    {
        using var temp = new TemporaryDirectory();
        var queuePath = temp.GetPath("cache", "pending-playtime-sessions.json");
        var firstService = CreateService(
            new StubApiClient { Failure = new HttpRequestException("offline") },
            queuePath,
            userId: "46988829925743611",
            accessToken: "first-token");
        await firstService.EnqueueSessionAsync(
            CreateGogItem("1994983163"),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        await firstService.TrySyncPendingAsync();

        var otherAccountClient = new StubApiClient();
        var otherAccountService = CreateService(
            otherAccountClient,
            queuePath,
            userId: "11111111111111111",
            accessToken: "other-token");

        await otherAccountService.TrySyncPendingAsync();

        Assert.Empty(otherAccountClient.Posts);
        Assert.True(File.Exists(queuePath));
    }

    [Fact]
    public async Task RunningUploadDoesNotBlockEnqueueingAnotherSession()
    {
        using var temp = new TemporaryDirectory();
        var queuePath = temp.GetPath("cache", "pending-playtime-sessions.json");
        var apiClient = new BlockingApiClient();
        var service = CreateService(
            apiClient,
            queuePath,
            userId: "46988829925743611",
            accessToken: "access-token");
        var firstStart = new DateTimeOffset(2026, 10, 25, 12, 30, 45, TimeSpan.Zero);

        await service.EnqueueSessionAsync(
            CreateGogItem("1994983163"),
            firstStart,
            TimeSpan.FromMinutes(5));
        var syncTask = service.TrySyncPendingAsync();
        await apiClient.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await service.EnqueueSessionAsync(
                    CreateGogItem("1207658680"),
                    firstStart.AddHours(1),
                    TimeSpan.FromMinutes(6))
                .WaitAsync(TimeSpan.FromSeconds(2));

            var persistedQueue = await File.ReadAllTextAsync(queuePath);
            Assert.Contains("1994983163", persistedQueue, StringComparison.Ordinal);
            Assert.Contains("1207658680", persistedQueue, StringComparison.Ordinal);
        }
        finally
        {
            apiClient.ReleaseRequest.TrySetResult();
        }

        await syncTask;
        var remainingQueue = await File.ReadAllTextAsync(queuePath);
        Assert.DoesNotContain("1994983163", remainingQueue, StringComparison.Ordinal);
        Assert.Contains("1207658680", remainingQueue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestPendingSync_UploadsInBackgroundAfterReturning()
    {
        using var temp = new TemporaryDirectory();
        var queuePath = temp.GetPath("cache", "pending-playtime-sessions.json");
        var apiClient = new BlockingApiClient();
        var service = CreateService(
            apiClient,
            queuePath,
            userId: "46988829925743611",
            accessToken: "access-token");

        await service.EnqueueSessionAsync(
            CreateGogItem("1994983163"),
            new DateTimeOffset(2026, 10, 25, 12, 30, 45, TimeSpan.Zero),
            TimeSpan.FromMinutes(5));

        service.RequestPendingSync();
        try
        {
            await apiClient.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(File.Exists(queuePath));
        }
        finally
        {
            apiClient.ReleaseRequest.TrySetResult();
        }

        await WaitUntilAsync(() => !File.Exists(queuePath), TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (!condition() && DateTimeOffset.UtcNow - startedAt < timeout)
            await Task.Delay(10);

        Assert.True(condition(), "The expected background synchronization did not complete in time.");
    }

    private static GogPlaytimeSyncService CreateService(
        IGogPlaytimeApiClient apiClient,
        string queuePath,
        string userId,
        string accessToken) =>
        new(
            _ => Task.FromResult<GogGameplayCredentials?>(
                new GogGameplayCredentials(accessToken, userId)),
            _ => Task.FromResult<string?>(userId),
            apiClient,
            queuePath, new RecordingErrorService());

    private static MediaItem CreateGogItem(string gameId)
    {
        var item = new MediaItem("GOG game");
        item.CustomFields[CustomFieldKeyHelper.StoreProviderId] = "gog";
        item.CustomFields[CustomFieldKeyHelper.StoreGameId] = gameId;
        return item;
    }

    private sealed class StubApiClient : IGogPlaytimeApiClient
    {
        internal List<PostCall> Posts { get; } = [];
        internal Exception? Failure { get; init; }

        public Task PostSessionAsync(
            string gameId,
            string userId,
            string accessToken,
            long sessionDate,
            int timeMinutes,
            CancellationToken cancellationToken = default)
        {
            if (Failure != null)
                throw Failure;

            Posts.Add(new PostCall(
                gameId,
                userId,
                accessToken,
                sessionDate,
                timeMinutes));
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingApiClient : IGogPlaytimeApiClient
    {
        internal TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PostSessionAsync(
            string gameId,
            string userId,
            string accessToken,
            long sessionDate,
            int timeMinutes,
            CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult();
            await ReleaseRequest.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed record PostCall(
        string GameId,
        string UserId,
        string AccessToken,
        long SessionDate,
        int TimeMinutes);
}
