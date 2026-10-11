using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SettingsViewModelSteamAchievementsTests
{
    private const string SteamId64 = "76561198000000042";

    [Fact]
    public async Task TestThenCancel_DoesNotPersistSteamKeyOrSettings()
    {
        var targetSettings = new AppSettings();
        var secretStore = new RecordingSecretStore();

        using var httpClient = CreateSteamProfileClient();
        using var viewModel = CreateViewModel(
            targetSettings,
            secretStore,
            httpClient);

        await viewModel.InitializeSteamAchievementsAsync();

        viewModel.SteamAchievementsEnabled = true;
        viewModel.SteamAchievementsSteamId64 = SteamId64;
        viewModel.SteamAchievementsApiKey = "pending-key";

        await viewModel.TestSteamConnectionCommand
            .ExecuteAsync(null);

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(0, secretStore.SetCalls);
        Assert.False(
            targetSettings.SteamAchievements.Enabled);
        Assert.Null(
            targetSettings.SteamAchievements.SteamId64);
    }

    [Fact]
    public async Task SaveAfterSuccessfulTest_PersistsSteamSettingsAndKey()
    {
        var targetSettings = new AppSettings();
        var secretStore = new RecordingSecretStore();

        using var httpClient = CreateSteamProfileClient();
        using var viewModel = CreateViewModel(
            targetSettings,
            secretStore,
            httpClient);

        await viewModel.InitializeSteamAchievementsAsync();

        viewModel.SteamAchievementsEnabled = true;
        viewModel.SteamAchievementsShowInBigMode = false;
        viewModel.SteamAchievementsSteamId64 = SteamId64;
        viewModel.SteamAchievementsApiKey = "pending-key";

        await viewModel.TestSteamConnectionCommand
            .ExecuteAsync(null);

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, secretStore.SetCalls);
        Assert.Equal("pending-key", secretStore.StoredValue);
        Assert.True(
            targetSettings.SteamAchievements.Enabled);
        Assert.False(
            targetSettings.SteamAchievements.ShowInBigMode);
        Assert.Equal(
            SteamId64,
            targetSettings.SteamAchievements.SteamId64);
    }

    private static SettingsViewModel CreateViewModel(
        AppSettings settings,
        ISecretStore secretStore,
        HttpClient httpClient)
    {
        var retroAccountService =
            new RetroAchievementsAccountService(
                new RetroAchievementsApiClient(httpClient),
                secretStore);

        var steamAccountService =
            new SteamAchievementsAccountService(
                new SteamApiClient(httpClient),
                secretStore);

        var steamDiscoveryService =
            new SteamAccountDiscoveryService(
                Array.Empty<string>());

        return new SettingsViewModel(
            settings,
            new SettingsService(),
            retroAccountService,
            steamAccountService,
            steamDiscoveryService,
            new RunnerVersionService(AppPaths.DataRoot));
    }

    private static HttpClient CreateSteamProfileClient()
    {
        return new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
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
            }));
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string? StoredValue { get; set; }

        public int SetCalls { get; private set; }

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
            return Task.FromResult(StoredValue);
        }

        public Task DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
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