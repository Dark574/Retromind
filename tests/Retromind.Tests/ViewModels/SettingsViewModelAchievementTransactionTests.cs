using System.Net.Http;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SettingsViewModelAchievementTransactionTests
{
    private const string SteamId64 = "76561198000000042";

    [Fact]
    public async Task Save_WhenSecondSecretWriteFails_RestoresBothPreviousKeys()
    {
        var settings = CreateInitialSettings();
        var secretStore = new TransactionalTestSecretStore(
            retroAchievementsKey: "old-retro-key",
            steamKey: "old-steam-key")
        {
            FailSteamValue = "new-steam-key"
        };

        using var viewModel = CreateViewModel(settings, secretStore);
        await viewModel.InitializeRetroAchievementsAsync();
        await viewModel.InitializeSteamAchievementsAsync();

        ConfigureNewAccountValues(viewModel);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSaved);
        Assert.Equal("old-retro-key", secretStore.GetValue("retromind:retroachievements"));
        Assert.Equal("old-steam-key", secretStore.GetValue("retromind:steam"));
        Assert.Equal("OldUser", settings.RetroAchievements.Username);
        Assert.Equal("76561198000000001", settings.SteamAchievements.SteamId64);
    }

    [Fact]
    public async Task RollbackAfterSettingsPersistenceFailure_RestoresKeysAndAccountSettings()
    {
        var settings = CreateInitialSettings();
        var secretStore = new TransactionalTestSecretStore(
            retroAchievementsKey: "old-retro-key",
            steamKey: "old-steam-key");

        using var viewModel = CreateViewModel(settings, secretStore);
        await viewModel.InitializeRetroAchievementsAsync();
        await viewModel.InitializeSteamAchievementsAsync();

        ConfigureNewAccountValues(viewModel);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSaved);
        Assert.Equal("new-retro-key", secretStore.GetValue("retromind:retroachievements"));
        Assert.Equal("new-steam-key", secretStore.GetValue("retromind:steam"));
        Assert.Equal("NewUser", settings.RetroAchievements.Username);
        Assert.Equal(SteamId64, settings.SteamAchievements.SteamId64);

        var restored = await viewModel.RollbackAchievementChangesAsync();

        Assert.True(restored);
        Assert.Equal("old-retro-key", secretStore.GetValue("retromind:retroachievements"));
        Assert.Equal("old-steam-key", secretStore.GetValue("retromind:steam"));
        Assert.Equal("OldUser", settings.RetroAchievements.Username);
        Assert.Equal("01OLDUSER", settings.RetroAchievements.UserUlid);
        Assert.Equal("76561198000000001", settings.SteamAchievements.SteamId64);
    }

    private static AppSettings CreateInitialSettings()
    {
        return new AppSettings
        {
            RetroAchievements = new RetroAchievementsSettings
            {
                Enabled = true,
                ShowInBigMode = true,
                Username = "OldUser",
                UserUlid = "01OLDUSER"
            },
            SteamAchievements = new SteamAchievementsSettings
            {
                Enabled = true,
                ShowInBigMode = true,
                SteamId64 = "76561198000000001"
            }
        };
    }

    private static void ConfigureNewAccountValues(SettingsViewModel viewModel)
    {
        viewModel.RetroAchievementsEnabled = true;
        viewModel.RetroAchievementsUsername = "NewUser";
        viewModel.RetroAchievementsApiKey = "new-retro-key";
        viewModel.SteamAchievementsEnabled = true;
        viewModel.SteamAchievementsSteamId64 = SteamId64;
        viewModel.SteamAchievementsApiKey = "new-steam-key";
    }

    private static SettingsViewModel CreateViewModel(
        AppSettings settings,
        ISecretStore secretStore)
    {
        var httpClient = new HttpClient(new UnusedHandler());
        var retroAchievementsAccountService = new RetroAchievementsAccountService(
            new RetroAchievementsApiClient(httpClient),
            secretStore);
        var steamAchievementsAccountService = new SteamAchievementsAccountService(
            new SteamApiClient(httpClient),
            secretStore);

        return new SettingsViewModel(
            settings,
            new SettingsService(),
            retroAchievementsAccountService,
            steamAchievementsAccountService,
            new SteamAccountDiscoveryService(Array.Empty<string>()),
            new RunnerVersionService(AppPaths.DataRoot));
    }

    private sealed class TransactionalTestSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public TransactionalTestSecretStore(
            string retroAchievementsKey,
            string steamKey)
        {
            _values["retromind:retroachievements"] = retroAchievementsKey;
            _values["retromind:steam"] = steamKey;
        }

        public string? FailSteamValue { get; init; }

        public string? GetValue(string service)
            => _values.GetValueOrDefault(service);

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task SetAsync(
            SecretKey key,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (key.Service == "retromind:steam" &&
                string.Equals(secret, FailSteamValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated Steam secret write failure.");
            }

            _values[key.Service] = secret;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(GetValue(key.Service));
        }

        public Task DeleteAsync(
            SecretKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(key.Service);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No HTTP request is expected in this test.");
        }
    }
}
