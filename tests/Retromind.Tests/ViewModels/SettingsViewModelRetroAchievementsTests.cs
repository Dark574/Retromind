using System.Net;
using System.Net.Http;
using System.Text;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Stores.Security;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SettingsViewModelRetroAchievementsTests
{
    [Fact]
    public async Task TestThenCancel_DoesNotPersistKeyOrWorkingSettings()
    {
        var targetSettings = new AppSettings();
        var secretStore = new RecordingSecretStore();
        using var httpClient = CreateProfileClient();
        using var viewModel = CreateViewModel(targetSettings, secretStore, httpClient);
        await viewModel.InitializeRetroAchievementsAsync();
        viewModel.RetroAchievementsEnabled = true;
        viewModel.RetroAchievementsUsername = "TestUser";
        viewModel.RetroAchievementsApiKey = "pending-key";

        await viewModel.TestRetroAchievementsConnectionCommand.ExecuteAsync(null);
        viewModel.CancelCommand.Execute(null);

        Assert.Equal(0, secretStore.SetCalls);
        Assert.False(targetSettings.RetroAchievements.Enabled);
        Assert.Null(targetSettings.RetroAchievements.Username);
        Assert.Null(targetSettings.RetroAchievements.UserUlid);
    }

    [Fact]
    public async Task SaveAfterSuccessfulTest_PersistsKeyAndStableUserIdentity()
    {
        var targetSettings = new AppSettings();
        var secretStore = new RecordingSecretStore();
        using var httpClient = CreateProfileClient();
        using var viewModel = CreateViewModel(targetSettings, secretStore, httpClient);
        await viewModel.InitializeRetroAchievementsAsync();
        viewModel.RetroAchievementsEnabled = true;
        viewModel.RetroAchievementsUsername = "TestUser";
        viewModel.RetroAchievementsApiKey = "pending-key";

        await viewModel.TestRetroAchievementsConnectionCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, secretStore.SetCalls);
        Assert.Equal("pending-key", secretStore.StoredValue);
        Assert.True(targetSettings.RetroAchievements.Enabled);
        Assert.Equal("TestUser", targetSettings.RetroAchievements.Username);
        Assert.Equal("01TESTULID", targetSettings.RetroAchievements.UserUlid);
    }

    [Fact]
    public async Task RemoveThenCancel_LeavesStoredKeyUntouched()
    {
        var targetSettings = new AppSettings
        {
            RetroAchievements = new RetroAchievementsSettings
            {
                Enabled = true,
                Username = "TestUser",
                UserUlid = "01TESTULID"
            }
        };
        var secretStore = new RecordingSecretStore { StoredValue = "stored-key" };
        using var httpClient = CreateProfileClient();
        using var viewModel = CreateViewModel(targetSettings, secretStore, httpClient);
        await viewModel.InitializeRetroAchievementsAsync();

        viewModel.RemoveRetroAchievementsApiKeyCommand.Execute(null);
        viewModel.CancelCommand.Execute(null);

        Assert.Equal(0, secretStore.DeleteCalls);
        Assert.Equal("stored-key", secretStore.StoredValue);
        Assert.True(targetSettings.RetroAchievements.Enabled);
    }

    [Fact]
    public async Task RemoveThenSave_DisablesIntegrationAndDeletesStoredKey()
    {
        var targetSettings = new AppSettings
        {
            RetroAchievements = new RetroAchievementsSettings
            {
                Enabled = true,
                Username = "TestUser",
                UserUlid = "01TESTULID"
            }
        };
        var secretStore = new RecordingSecretStore { StoredValue = "stored-key" };
        using var httpClient = CreateProfileClient();
        using var viewModel = CreateViewModel(targetSettings, secretStore, httpClient);
        await viewModel.InitializeRetroAchievementsAsync();

        viewModel.RemoveRetroAchievementsApiKeyCommand.Execute(null);

        Assert.False(viewModel.RetroAchievementsEnabled);
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, secretStore.DeleteCalls);
        Assert.Null(secretStore.StoredValue);
        Assert.False(targetSettings.RetroAchievements.Enabled);
        Assert.Equal("TestUser", targetSettings.RetroAchievements.Username);
    }

    private static SettingsViewModel CreateViewModel(
        AppSettings targetSettings,
        ISecretStore secretStore,
        HttpClient httpClient)
    {
        var accountService = new RetroAchievementsAccountService(
            new RetroAchievementsApiClient(httpClient),
            secretStore);
        return new SettingsViewModel(targetSettings, new SettingsService(), accountService);
    }

    private static HttpClient CreateProfileClient()
    {
        return new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"User":"TestUser","ULID":"01TESTULID","TotalPoints":10}""",
                Encoding.UTF8,
                "application/json")
        }));
    }

    private sealed class RecordingSecretStore : ISecretStore
    {
        public string? StoredValue { get; set; }
        public int SetCalls { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public Task SetAsync(SecretKey key, string secret, CancellationToken ct = default)
        {
            SetCalls++;
            StoredValue = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default)
            => Task.FromResult(StoredValue);

        public Task DeleteAsync(SecretKey key, CancellationToken ct = default)
        {
            DeleteCalls++;
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
