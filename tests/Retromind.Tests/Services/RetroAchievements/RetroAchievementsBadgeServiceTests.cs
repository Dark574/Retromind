using System.Net;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsBadgeServiceTests
{
    private static readonly byte[] PngData = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

    [Fact]
    public async Task GetBadgePathAsync_DownloadsUnlockedBadgeOnceAndReusesCache()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        Uri? requestedUri = null;
        var service = CreateService(
            (request, cancellationToken) =>
            {
                requestCount++;
                requestedUri = request.RequestUri;
                return Task.FromResult(PngResponse());
            },
            temp.GetPath("badges"));

        var firstPath = await service.GetBadgePathAsync("250336", isUnlocked: true);
        var secondPath = await service.GetBadgePathAsync("250336", isUnlocked: true);

        Assert.NotNull(firstPath);
        Assert.Equal(firstPath, secondPath);
        Assert.Equal(1, requestCount);
        Assert.Equal("https://i.retroachievements.org/Badge/250336.png", requestedUri?.AbsoluteUri);
        Assert.Equal(PngData, await File.ReadAllBytesAsync(firstPath));
    }

    [Fact]
    public async Task GetBadgePathAsync_UsesLockedBadgeVariant()
    {
        using var temp = new TemporaryDirectory();
        Uri? requestedUri = null;
        var service = CreateService(
            (request, cancellationToken) =>
            {
                requestedUri = request.RequestUri;
                return Task.FromResult(PngResponse());
            },
            temp.GetPath("badges"));

        var path = await service.GetBadgePathAsync("250336", isUnlocked: false);

        Assert.NotNull(path);
        Assert.EndsWith("250336_lock.png", path, StringComparison.Ordinal);
        Assert.Equal("https://i.retroachievements.org/Badge/250336_lock.png", requestedUri?.AbsoluteUri);
    }

    [Fact]
    public async Task GetBadgePathAsync_CoalescesConcurrentDownloads()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        var responseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            async (request, cancellationToken) =>
            {
                Interlocked.Increment(ref requestCount);
                await responseGate.Task.WaitAsync(cancellationToken);
                return PngResponse();
            },
            temp.GetPath("badges"));

        var first = service.GetBadgePathAsync("250336", isUnlocked: true);
        var second = service.GetBadgePathAsync("250336", isUnlocked: true);
        responseGate.SetResult(true);
        var paths = await Task.WhenAll(first, second);

        Assert.Equal(1, requestCount);
        Assert.NotNull(paths[0]);
        Assert.Equal(paths[0], paths[1]);
    }

    [Fact]
    public async Task GetBadgePathAsync_RejectsInvalidNamesAndInvalidDownloads()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        var service = CreateService(
            (request, cancellationToken) =>
            {
                requestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4])
                });
            },
            temp.GetPath("badges"));

        var unsafePath = await service.GetBadgePathAsync("../../secret", isUnlocked: true);
        var invalidDownload = await service.GetBadgePathAsync("250336", isUnlocked: true);

        Assert.Null(unsafePath);
        Assert.Null(invalidDownload);
        Assert.Equal(1, requestCount);
        Assert.False(Directory.Exists(temp.GetPath("badges")) &&
                     Directory.EnumerateFiles(temp.GetPath("badges")).Any());
    }

    [Fact]
    public async Task GetBadgePathAsync_ReturnsNullWhenCallerCancels()
    {
        using var temp = new TemporaryDirectory();
        var service = CreateService(
            async (request, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PngResponse();
            },
            temp.GetPath("badges"));
        using var cancellation = new CancellationTokenSource();

        var download = service.GetBadgePathAsync(
            "250336",
            isUnlocked: true,
            cancellation.Token);
        cancellation.Cancel();

        Assert.Null(await download);
    }

    [Fact]
    public async Task GetBadgePathAsync_FollowsChangedSharedCacheRoot()
    {
        using var temp = new TemporaryDirectory();
        var firstRoot = temp.GetPath("first-cache");
        var secondRoot = temp.GetPath("second-cache");
        var cachePaths = new RetroAchievementsCachePathProvider(firstRoot);
        var requestCount = 0;
        var httpClient = new HttpClient(new StubHandler((request, cancellationToken) =>
        {
            requestCount++;
            return Task.FromResult(PngResponse());
        }));
        var service = new RetroAchievementsBadgeService(httpClient, cachePaths);

        var firstPath = await service.GetBadgePathAsync("250336", isUnlocked: true);
        cachePaths.SetCacheDirectory(secondRoot);
        var secondPath = await service.GetBadgePathAsync("250336", isUnlocked: true);

        Assert.Equal(Path.Combine(firstRoot, "Badges", "250336.png"), firstPath);
        Assert.Equal(Path.Combine(secondRoot, "Badges", "250336.png"), secondPath);
        Assert.Equal(2, requestCount);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    private static RetroAchievementsBadgeService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory,
        string cacheDirectory)
    {
        var httpClient = new HttpClient(new StubHandler(responseFactory));
        return new RetroAchievementsBadgeService(httpClient, cacheDirectory);
    }

    private static HttpResponseMessage PngResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PngData)
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responseFactory(request, cancellationToken);
    }
}
