using System.Net;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Security;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SettingsViewModelThemeTests
{
    [Theory]
    [InlineData("Navigator/theme.axaml", "Navigator/theme.axaml")]
    [InlineData("Default/theme.axaml", "Default/theme.axaml")]
    [InlineData("Prism/theme.axaml", "Prism/theme.axaml")]
    [InlineData("Missing/theme.axaml", "Navigator/theme.axaml")]
    public async Task SavingUnrelatedSettings_PreservesConfiguredRootTheme(string configured, string displayed)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        foreach (var theme in new[] { "Navigator", "Default", "Prism" }) temp.CreateFile($"Themes/{theme}/theme.axaml", string.Empty);
        var settings = new AppSettings { RootBigModeThemePath = configured };
        using var vm = CreateViewModel(settings);

        Assert.Equal(displayed, vm.SelectedRootBigModeTheme);
        Assert.Equal(configured, settings.RootBigModeThemePath);
        vm.PreferPortableLaunchPaths = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsSaved);
        Assert.True(settings.PreferPortableLaunchPaths);
        Assert.Equal(configured, settings.RootBigModeThemePath);
    }

    [Fact]
    public async Task SelectingAnotherTheme_ExplicitlyChangesTheSavedAssignment()
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        temp.CreateFile("Themes/Navigator/theme.axaml", string.Empty);
        temp.CreateFile("Themes/Prism/theme.axaml", string.Empty);
        var settings = new AppSettings { RootBigModeThemePath = "Missing/theme.axaml" };
        using var vm = CreateViewModel(settings);

        Assert.Equal("Navigator/theme.axaml", vm.SelectedRootBigModeTheme);
        vm.SelectedRootBigModeTheme = "Prism/theme.axaml";
        Assert.Equal("Missing/theme.axaml", settings.RootBigModeThemePath);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Prism/theme.axaml", settings.RootBigModeThemePath);
    }

    [Theory]
    [InlineData(true, "Default/theme.axaml")]
    [InlineData(false, null)]
    public async Task MissingNavigator_UsesAvailableCompatibilityFallbackWithoutChangingSettings(bool legacyInstalled, string? displayed)
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")));
        if (legacyInstalled) temp.CreateFile("Themes/Default/theme.axaml", string.Empty);
        var settings = new AppSettings();
        using var vm = CreateViewModel(settings);

        Assert.Equal(displayed, vm.SelectedRootBigModeTheme);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Navigator/theme.axaml", settings.RootBigModeThemePath);
    }

    private static SettingsViewModel CreateViewModel(AppSettings settings)
    {
        var http = new HttpClient(new StubHandler());
        var secrets = new InMemorySecretStore();
        return new SettingsViewModel(settings, new SettingsService(),
            new RetroAchievementsAccountService(new RetroAchievementsApiClient(http), secrets),
            new SteamAchievementsAccountService(new SteamApiClient(http), secrets),
            new SteamAccountDiscoveryService(Array.Empty<string>()), new RunnerVersionService(AppPaths.DataRoot));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
