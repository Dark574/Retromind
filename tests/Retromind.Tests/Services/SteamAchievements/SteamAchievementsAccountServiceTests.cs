using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.SteamAchievements;

public sealed class SteamAchievementsAccountServiceTests
{
    private const string SteamId64 = "76561198000000042";

    [Fact]
    public async Task VerifyAsync_UsesPendingKeyWithoutPersistingIt()
    {
        string? sentApiKey = null;

        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.Headers.TryGetValues(
                    "x-webapi-key",
                    out var values))
            {
                sentApiKey = Assert.Single(values);
            }

            return ProfileResponse();
        }));

        var secretStore = new RecordingSecretStore();
        var service = new SteamAchievementsAccountService(
            new SteamApiClient(httpClient),
            secretStore);

        var profile = await service.VerifyAsync(
            SteamId64,
            "pending-key");

        Assert.Equal(SteamId64, profile.SteamId64);
        Assert.Equal("pending-key", sentApiKey);
        Assert.Equal(0, secretStore.SetCalls);
        Assert.Equal(0, secretStore.GetCalls);
    }

    [Fact]
    public async Task VerifyAsync_UsesStoredKeyWhenNoPendingKeyWasEntered()
    {
        string? sentApiKey = null;

        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            if (request.Headers.TryGetValues(
                    "x-webapi-key",
                    out var values))
            {
                sentApiKey = Assert.Single(values);
            }

            return ProfileResponse();
        }));

        var secretStore = new RecordingSecretStore
        {
            StoredValue = "stored-key"
        };

        var service = new SteamAchievementsAccountService(
            new SteamApiClient(httpClient),
            secretStore);

        await service.VerifyAsync(SteamId64, null);

        Assert.Equal("stored-key", sentApiKey);
        Assert.Equal(1, secretStore.GetCalls);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task StoreAndDeleteApiKeyAsync_UseSecretStore()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            ProfileResponse()));

        var secretStore = new RecordingSecretStore();
        var service = new SteamAchievementsAccountService(
            new SteamApiClient(httpClient),
            secretStore);

        await service.StoreApiKeyAsync("  new-key  ");

        Assert.Equal("new-key", secretStore.StoredValue);
        Assert.Equal(1, secretStore.SetCalls);

        await service.DeleteApiKeyAsync();

        Assert.Null(secretStore.StoredValue);
        Assert.Equal(1, secretStore.DeleteCalls);
    }

    private static HttpResponseMessage ProfileResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "response": {
                    "players": [
                      {
                        "steamid": "{{SteamId64}}",
                        "personaname": "Test player"
                      }
                    ]
                  }
                }
                """,
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string? StoredValue { get; set; }

        public int GetCalls { get; private set; }

        public int SetCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public Task<bool> IsAvailableAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task SetAsync(
            SecretKey key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            SetCalls++;
            StoredValue = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(StoredValue);
        }

        public Task DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            StoredValue = null;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
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