using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Stores.Security;

namespace Retromind.Tests.Services.RetroAchievements;

public sealed class RetroAchievementsAccountServiceTests
{
    [Fact]
    public async Task VerifyAsync_UsesPendingKeyWithoutPersistingIt()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return ProfileResponse();
        }));
        var secretStore = new RecordingSecretStore();
        var service = new RetroAchievementsAccountService(
            new RetroAchievementsApiClient(httpClient),
            secretStore);

        var profile = await service.VerifyAsync("TestUser", "pending-key");

        Assert.Equal("01TESTULID", profile.UserUlid);
        Assert.Contains("y=pending-key", requestedUri?.Query, StringComparison.Ordinal);
        Assert.Equal(0, secretStore.SetCalls);
    }

    [Fact]
    public async Task VerifyAsync_UsesStoredKeyWhenNoPendingKeyWasEntered()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return ProfileResponse();
        }));
        var secretStore = new RecordingSecretStore { StoredValue = "stored-key" };
        var service = new RetroAchievementsAccountService(
            new RetroAchievementsApiClient(httpClient),
            secretStore);

        await service.VerifyAsync("TestUser", null);

        Assert.Contains("y=stored-key", requestedUri?.Query, StringComparison.Ordinal);
        Assert.Equal(1, secretStore.GetCalls);
    }

    private static HttpResponseMessage ProfileResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"User":"TestUser","ULID":"01TESTULID","TotalPoints":10}""",
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string? StoredValue { get; set; }
        public int GetCalls { get; private set; }
        public int SetCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
        {
            SetCalls++;
            StoredValue = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(StoredValue);
        }

        public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
        {
            StoredValue = null;
            return Task.CompletedTask;
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
