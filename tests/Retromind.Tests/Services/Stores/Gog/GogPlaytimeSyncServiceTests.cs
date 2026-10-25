using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.Stores.Gog.Auth;
using Retromind.Services.Stores.Gog.Playtime;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Stores.Gog;

public sealed class GogPlaytimeSyncServiceTests
{
    [Fact]
    public async Task ReportSessionAsync_FloorsMinutesUploadsAndClearsQueue()
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

        await service.ReportSessionAsync(
            CreateGogItem("1994983163"),
            startedAt,
            TimeSpan.FromSeconds(179));

        var post = Assert.Single(apiClient.Posts);
        Assert.Equal("1994983163", post.GameId);
        Assert.Equal("46988829925743611", post.UserId);
        Assert.Equal("access-token", post.AccessToken);
        Assert.Equal(startedAt.ToUnixTimeSeconds(), post.SessionDate);
        Assert.Equal(2, post.TimeMinutes);
        Assert.False(File.Exists(queuePath));
    }

    [Fact]
    public async Task ReportSessionAsync_SkipsSessionsShorterThanOneMinute()
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
            temp.GetPath("pending.json"));

        await service.ReportSessionAsync(
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
            temp.GetPath("missing-pending.json"));

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

        await firstService.ReportSessionAsync(
            CreateGogItem("1994983163"),
            startedAt,
            TimeSpan.FromMinutes(12));

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
        await firstService.ReportSessionAsync(
            CreateGogItem("1994983163"),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));

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
            queuePath);

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

    private sealed record PostCall(
        string GameId,
        string UserId,
        string AccessToken,
        long SessionDate,
        int TimeMinutes);
}
