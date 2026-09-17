using System.Net;
using System.Text;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsGameCatalogServiceTests
{
    private const string KnownHash = "25f9e794323b453885f5181f1b624d0b";

    [Fact]
    public async Task FindByHashAsync_PersistsPortableCacheWithoutApiKey()
    {
        using var temp = new TemporaryDirectory();
        var cacheDirectory = temp.GetPath("cache");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero));
        var requestCount = 0;
        var firstService = CreateService(
            _ =>
            {
                requestCount++;
                return CatalogResponse();
            },
            cacheDirectory,
            time);

        var firstMatch = await firstService.FindByHashAsync(4, KnownHash, "secret-key");

        Assert.NotNull(firstMatch);
        Assert.Equal(123, firstMatch.GameId);
        Assert.Equal(1, requestCount);

        var cachePath = Path.Combine(cacheDirectory, "games-4.json");
        Assert.True(File.Exists(cachePath));
        Assert.DoesNotContain("secret-key", await File.ReadAllTextAsync(cachePath), StringComparison.Ordinal);

        var secondService = CreateService(
            _ => throw new InvalidOperationException("Fresh disk cache should prevent an API call."),
            cacheDirectory,
            time);

        var secondMatch = await secondService.FindByHashAsync(4, KnownHash, "another-key");

        Assert.NotNull(secondMatch);
        Assert.Equal(123, secondMatch.GameId);
    }

    [Fact]
    public async Task FindByHashAsync_UsesStaleValidCacheWhenRefreshFails()
    {
        using var temp = new TemporaryDirectory();
        var cacheDirectory = temp.GetPath("cache");
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero));
        var initialService = CreateService(_ => CatalogResponse(), cacheDirectory, time);
        _ = await initialService.FindByHashAsync(4, KnownHash, "key");
        time.Advance(TimeSpan.FromDays(8));
        var refreshAttempts = 0;

        var offlineService = CreateService(
            _ =>
            {
                refreshAttempts++;
                throw new HttpRequestException("offline");
            },
            cacheDirectory,
            time);

        var match = await offlineService.FindByHashAsync(4, KnownHash, "key");
        var repeatedMatch = await offlineService.FindByHashAsync(4, KnownHash, "key");

        Assert.NotNull(match);
        Assert.NotNull(repeatedMatch);
        Assert.Equal(123, match.GameId);
        Assert.Equal(1, refreshAttempts);
    }

    [Fact]
    public async Task FindByHashAsync_InvalidCacheIsRefreshed()
    {
        using var temp = new TemporaryDirectory();
        var cacheDirectory = temp.CreateDirectory("cache");
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "games-4.json"), "not json");
        var requestCount = 0;
        var service = CreateService(
            _ =>
            {
                requestCount++;
                return CatalogResponse();
            },
            cacheDirectory,
            new MutableTimeProvider(DateTimeOffset.UtcNow));

        var match = await service.FindByHashAsync(4, KnownHash, "key");

        Assert.NotNull(match);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task FindByHashAsync_ConcurrentRequestsDownloadCatalogOnce()
    {
        using var temp = new TemporaryDirectory();
        var requestCount = 0;
        var service = CreateService(
            _ =>
            {
                Interlocked.Increment(ref requestCount);
                return CatalogResponse();
            },
            temp.GetPath("cache"),
            new MutableTimeProvider(DateTimeOffset.UtcNow));

        var first = service.FindByHashAsync(4, KnownHash, "key");
        var second = service.FindByHashAsync(4, KnownHash, "key");
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, requestCount);
        Assert.All(results, result => Assert.Equal(123, result?.GameId));
    }

    [Fact]
    public async Task MigrateCacheLocationAsync_MovesCacheInBothDirections()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var standardDirectory = RetroAchievementsGameCatalogService.GetCacheDirectory(
            usePortableHome: false);
        var portableDirectory = RetroAchievementsGameCatalogService.GetCacheDirectory(
            usePortableHome: true);
        var standardPath = Path.Combine(standardDirectory, "games-4.json");
        var portablePath = Path.Combine(portableDirectory, "games-4.json");
        var standardProgressPath = Path.Combine(
            standardDirectory,
            "Progress",
            "user-hash",
            "game-123.json");
        var portableProgressPath = Path.Combine(
            portableDirectory,
            "Progress",
            "user-hash",
            "game-123.json");
        var standardBadgePath = Path.Combine(standardDirectory, "Badges", "250336.png");
        var portableBadgePath = Path.Combine(portableDirectory, "Badges", "250336.png");
        Directory.CreateDirectory(standardDirectory);
        await File.WriteAllTextAsync(standardPath, "standard-cache");
        Directory.CreateDirectory(Path.GetDirectoryName(standardProgressPath)!);
        await File.WriteAllTextAsync(standardProgressPath, "standard-progress");
        Directory.CreateDirectory(Path.GetDirectoryName(standardBadgePath)!);
        await File.WriteAllBytesAsync(standardBadgePath, [1, 2, 3]);

        await RetroAchievementsGameCatalogService.MigrateCacheLocationAsync(
            wasPortableHomeEnabled: false,
            isPortableHomeEnabled: true);

        Assert.False(File.Exists(standardPath));
        Assert.Equal("standard-cache", await File.ReadAllTextAsync(portablePath));
        Assert.False(File.Exists(standardProgressPath));
        Assert.Equal("standard-progress", await File.ReadAllTextAsync(portableProgressPath));
        Assert.False(File.Exists(standardBadgePath));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(portableBadgePath));

        await File.WriteAllTextAsync(portablePath, "portable-cache");
        await File.WriteAllTextAsync(portableProgressPath, "portable-progress");
        await File.WriteAllBytesAsync(portableBadgePath, [4, 5, 6]);
        await RetroAchievementsGameCatalogService.MigrateCacheLocationAsync(
            wasPortableHomeEnabled: true,
            isPortableHomeEnabled: false);

        Assert.False(File.Exists(portablePath));
        Assert.Equal("portable-cache", await File.ReadAllTextAsync(standardPath));
        Assert.False(File.Exists(portableProgressPath));
        Assert.Equal("portable-progress", await File.ReadAllTextAsync(standardProgressPath));
        Assert.False(File.Exists(portableBadgePath));
        Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(standardBadgePath));
    }

    [Fact]
    public async Task MigrateCacheLocationAsync_KeepsNewerDestinationFile()
    {
        using var temp = new TemporaryDirectory();
        using var environment = UseDataRoot(temp.RootPath);
        var standardDirectory = RetroAchievementsGameCatalogService.GetCacheDirectory(
            usePortableHome: false);
        var portableDirectory = RetroAchievementsGameCatalogService.GetCacheDirectory(
            usePortableHome: true);
        var standardPath = Path.Combine(standardDirectory, "games-4.json");
        var portablePath = Path.Combine(portableDirectory, "games-4.json");
        Directory.CreateDirectory(standardDirectory);
        Directory.CreateDirectory(portableDirectory);
        await File.WriteAllTextAsync(standardPath, "older-source");
        await File.WriteAllTextAsync(portablePath, "newer-destination");
        File.SetLastWriteTimeUtc(standardPath, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(portablePath, new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));

        await RetroAchievementsGameCatalogService.MigrateCacheLocationAsync(
            wasPortableHomeEnabled: false,
            isPortableHomeEnabled: true);

        Assert.False(File.Exists(standardPath));
        Assert.Equal("newer-destination", await File.ReadAllTextAsync(portablePath));
    }

    private static RetroAchievementsGameCatalogService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        string cacheDirectory,
        TimeProvider timeProvider)
    {
        var httpClient = new HttpClient(new StubHandler(responseFactory));
        var apiClient = new RetroAchievementsApiClient(httpClient);
        return new RetroAchievementsGameCatalogService(apiClient, cacheDirectory, timeProvider);
    }

    private static EnvironmentVariableScope UseDataRoot(string rootPath)
        => new(
            ("APPIMAGE", Path.Combine(rootPath, "Retromind.AppImage")),
            ("APPDIR", null));

    private static HttpResponseMessage CatalogResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                [
                  {
                    "ID": 123,
                    "Title": "Test Game",
                    "ConsoleID": 4,
                    "ConsoleName": "Game Boy",
                    "ImageIcon": "/Images/123.png",
                    "NumAchievements": 10,
                    "Hashes": ["{{KnownHash}}"]
                  }
                ]
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
