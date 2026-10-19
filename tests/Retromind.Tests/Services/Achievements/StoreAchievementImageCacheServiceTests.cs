using System.Net;
using Retromind.Services.Achievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.Achievements;

public sealed class StoreAchievementImageCacheServiceTests
{
    private static readonly byte[] PngData =
        [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4];

    [Fact]
    public async Task GetImagePathAsync_DownloadsOnceAndReusesPersistentCache()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler((request, cancellationToken) =>
        {
            requestCount++;
            return Task.FromResult(PngResponse());
        }));
        var cachePaths = new StoreAchievementCachePathProvider(temp.GetPath("cache"));
        var service = new StoreAchievementImageCacheService(httpClient, cachePaths);

        var firstPath = await service.GetImagePathAsync(
            StoreAchievementProvider.Steam,
            "https://cdn.example/achievement.png");
        var secondPath = await service.GetImagePathAsync(
            StoreAchievementProvider.Steam,
            "https://cdn.example/achievement.png");

        Assert.NotNull(firstPath);
        Assert.Equal(firstPath, secondPath);
        Assert.Equal(1, requestCount);
        Assert.StartsWith(
            cachePaths.GetImageCacheDirectory(StoreAchievementProvider.Steam),
            firstPath,
            StringComparison.Ordinal);
        Assert.Equal(PngData, await File.ReadAllBytesAsync(firstPath));
    }

    [Fact]
    public async Task GetImagePathAsync_SeparatesProvidersAndDoesNotExposeUrlInPath()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler((request, cancellationToken) =>
        {
            requestCount++;
            return Task.FromResult(PngResponse());
        }));
        var cachePaths = new StoreAchievementCachePathProvider(temp.GetPath("cache"));
        var service = new StoreAchievementImageCacheService(httpClient, cachePaths);
        const string imageUrl = "https://cdn.example/private-name.png?account=123";

        var steamPath = await service.GetImagePathAsync(
            StoreAchievementProvider.Steam,
            imageUrl);
        var gogPath = await service.GetImagePathAsync(
            StoreAchievementProvider.Gog,
            imageUrl);

        Assert.NotNull(steamPath);
        Assert.NotNull(gogPath);
        Assert.NotEqual(steamPath, gogPath);
        Assert.Equal(2, requestCount);
        Assert.DoesNotContain("private-name", steamPath, StringComparison.Ordinal);
        Assert.DoesNotContain("account=123", steamPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetImagePathAsync_RejectsInsecureUrlsAndInvalidOrOversizedContent()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler((request, cancellationToken) =>
        {
            requestCount++;
            if (request.RequestUri!.AbsolutePath.Contains("large", StringComparison.Ordinal))
            {
                var response = PngResponse();
                response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 8])
            });
        }));
        var cachePaths = new StoreAchievementCachePathProvider(temp.GetPath("cache"));
        var service = new StoreAchievementImageCacheService(httpClient, cachePaths);

        var insecure = await service.GetImagePathAsync(
            StoreAchievementProvider.Gog,
            "http://cdn.example/image.png");
        var invalid = await service.GetImagePathAsync(
            StoreAchievementProvider.Gog,
            "https://cdn.example/invalid.png");
        var oversized = await service.GetImagePathAsync(
            StoreAchievementProvider.Gog,
            "https://cdn.example/large.png");

        Assert.Null(insecure);
        Assert.Null(invalid);
        Assert.Null(oversized);
        Assert.Equal(2, requestCount);
        var imageDirectory = cachePaths.GetImageCacheDirectory(StoreAchievementProvider.Gog);
        Assert.False(Directory.Exists(imageDirectory) &&
                     Directory.EnumerateFiles(imageDirectory).Any());
    }

    [Fact]
    public async Task GetImagePathAsync_RemovesImagesUnusedForMoreThan180Days()
    {
        using var temp = new TemporaryDirectory();
        var cachePaths = new StoreAchievementCachePathProvider(temp.GetPath("cache"));
        var imageDirectory = cachePaths.GetImageCacheDirectory(StoreAchievementProvider.Steam);
        Directory.CreateDirectory(imageDirectory);
        var unusedImage = Path.Combine(imageDirectory, "unused.image");
        await File.WriteAllBytesAsync(unusedImage, PngData);
        File.SetLastWriteTimeUtc(unusedImage, DateTime.UtcNow.AddDays(-181));
        using var httpClient = new HttpClient(new StubHandler((request, cancellationToken) =>
            Task.FromResult(PngResponse())));
        var service = new StoreAchievementImageCacheService(httpClient, cachePaths);

        var currentImage = await service.GetImagePathAsync(
            StoreAchievementProvider.Steam,
            "https://cdn.example/current.png");

        Assert.NotNull(currentImage);
        Assert.False(File.Exists(unusedImage));
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
