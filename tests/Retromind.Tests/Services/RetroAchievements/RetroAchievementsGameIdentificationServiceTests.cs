using System.Net;
using System.Text;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsGameIdentificationServiceTests
{
    private const string KnownHash = "25f9e794323b453885f5181f1b624d0b";

    [Fact]
    public async Task IdentifyAsync_HashesFileAndResolvesCatalogMatch()
    {
        using var temp = new TemporaryDirectory();
        var hashService = new RecordingHashService(
            new RetroAchievementsGameHash("nintendo.game-boy", 4, KnownHash));
        var service = await CreateServiceAsync(
            hashService,
            temp.GetPath("cache"),
            CatalogResponse(KnownHash));

        var result = await service.IdentifyAsync(
            "nintendo.game-boy",
            "/games/test.gb");

        Assert.True(result.IsMatch);
        Assert.Equal(KnownHash, result.GameHash.Hash);
        Assert.Equal(123, result.Game?.GameId);
        Assert.Equal("Test Game", result.Game?.Title);
        Assert.Equal("nintendo.game-boy", hashService.LastGameSystemId);
        Assert.Equal("/games/test.gb", hashService.LastFilePath);
    }

    [Fact]
    public async Task IdentifyAsync_UnknownHashReturnsNormalNoMatchResult()
    {
        using var temp = new TemporaryDirectory();
        var hashService = new RecordingHashService(
            new RetroAchievementsGameHash("nintendo.game-boy", 4, KnownHash));
        var service = await CreateServiceAsync(
            hashService,
            temp.GetPath("cache"),
            CatalogResponse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        var result = await service.IdentifyAsync(
            "nintendo.game-boy",
            "/games/unknown.gb");

        Assert.False(result.IsMatch);
        Assert.Null(result.Game);
        Assert.Equal(KnownHash, result.GameHash.Hash);
    }

    [Fact]
    public async Task IdentifyAsync_MissingApiKeyFailsBeforeHashing()
    {
        using var temp = new TemporaryDirectory();
        var hashService = new RecordingHashService(
            new RetroAchievementsGameHash("nintendo.game-boy", 4, KnownHash));
        using var httpClient = new HttpClient(new StubHandler(
            _ => throw new InvalidOperationException("The API must not be called.")));
        var apiClient = new RetroAchievementsApiClient(httpClient);
        var service = new RetroAchievementsGameIdentificationService(
            hashService,
            new RetroAchievementsGameCatalogService(
                apiClient,
                temp.GetPath("cache"),
                TimeProvider.System),
            new RetroAchievementsAccountService(apiClient, new InMemorySecretStore()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IdentifyAsync("nintendo.game-boy", "/games/test.gb"));

        Assert.Equal("No RetroAchievements API key is configured.", exception.Message);
        Assert.Equal(0, hashService.CallCount);
    }

    private static async Task<RetroAchievementsGameIdentificationService> CreateServiceAsync(
        IRetroAchievementsHashService hashService,
        string cacheDirectory,
        HttpResponseMessage response)
    {
        var httpClient = new HttpClient(new StubHandler(_ => response));
        var apiClient = new RetroAchievementsApiClient(httpClient);
        var accountService = new RetroAchievementsAccountService(
            apiClient,
            new InMemorySecretStore());
        await accountService.StoreApiKeyAsync("secret-key");

        return new RetroAchievementsGameIdentificationService(
            hashService,
            new RetroAchievementsGameCatalogService(
                apiClient,
                cacheDirectory,
                TimeProvider.System),
            accountService);
    }

    private static HttpResponseMessage CatalogResponse(string hash)
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
                    "Hashes": ["{{hash}}"]
                  }
                ]
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class RecordingHashService(RetroAchievementsGameHash result)
        : IRetroAchievementsHashService
    {
        public int CallCount { get; private set; }
        public string? LastGameSystemId { get; private set; }
        public string? LastFilePath { get; private set; }

        public Task<RetroAchievementsGameHash> CalculateAsync(
            string gameSystemId,
            string filePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastGameSystemId = gameSystemId;
            LastFilePath = filePath;
            return Task.FromResult(result);
        }
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
