using System.Net;
using Avalonia.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Stores.Security;
using Retromind.ViewModels;

namespace Retromind.Tests.ViewModels;

public sealed class SettingsViewModelControllerTests
{
    [Fact]
    public async Task CapturingUsedActionButton_SwapsBindingsAndPersistsOnSave()
    {
        var targetSettings = new AppSettings();
        using var viewModel = CreateViewModel(targetSettings);
        var select = viewModel.ControllerActionBindings[0];
        var back = viewModel.ControllerActionBindings[1];

        viewModel.CompleteControllerCapture(select, ControllerButton.East);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ControllerButton.East, targetSettings.ControllerBindings.Select);
        Assert.Equal(ControllerButton.South, targetSettings.ControllerBindings.Back);
        Assert.Equal(ControllerButton.East, select.Button);
        Assert.Equal(ControllerButton.South, back.Button);
    }

    [Fact]
    public void CapturingUsedSessionStopButton_SwapsTheTwoButtonsWithoutSavingEarly()
    {
        var targetSettings = new AppSettings();
        using var viewModel = CreateViewModel(targetSettings);
        var first = viewModel.ControllerSessionStopBindings[0];
        var second = viewModel.ControllerSessionStopBindings[1];

        viewModel.CompleteControllerCapture(first, ControllerButton.RightShoulder);

        Assert.Equal(ControllerButton.RightShoulder, first.Button);
        Assert.Equal(ControllerButton.LeftShoulder, second.Button);
        Assert.Equal(ControllerButton.LeftShoulder, targetSettings.ControllerBindings.SessionStopFirst);
        Assert.Equal(ControllerButton.RightShoulder, targetSettings.ControllerBindings.SessionStopSecond);
    }

    [Fact]
    public async Task Reset_RestoresDefaultBindingsAndPersistsOnlyOnSave()
    {
        var targetSettings = new AppSettings
        {
            ControllerBindings = new ControllerBindingSettings
            {
                Select = ControllerButton.RightStick,
                SessionStopFirst = ControllerButton.Start,
                SessionStopSecond = ControllerButton.Back
            }
        };
        using var viewModel = CreateViewModel(targetSettings);

        viewModel.ResetControllerBindingsCommand.Execute(null);

        Assert.Equal(ControllerButton.RightStick, targetSettings.ControllerBindings.Select);
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal(ControllerButton.South, targetSettings.ControllerBindings.Select);
        Assert.Equal(ControllerButton.Start, targetSettings.ControllerBindings.SystemMenu);
        Assert.Equal(ControllerButton.LeftShoulder, targetSettings.ControllerBindings.PreviousPage);
        Assert.Equal(ControllerButton.RightShoulder, targetSettings.ControllerBindings.NextPage);
        Assert.Equal(ControllerButton.LeftShoulder, targetSettings.ControllerBindings.SessionStopFirst);
        Assert.Equal(ControllerButton.RightShoulder, targetSettings.ControllerBindings.SessionStopSecond);
    }

    [Fact]
    public async Task CapturedSystemMenuButton_IsCommittedWithOtherControllerBindings()
    {
        var targetSettings = new AppSettings();
        using var viewModel = CreateViewModel(targetSettings);
        var systemMenu = Assert.Single(viewModel.ControllerActionBindings, row =>
            row.Target == ControllerBindingTarget.SystemMenu);

        viewModel.CompleteControllerCapture(systemMenu, ControllerButton.RightStick);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ControllerButton.RightStick, targetSettings.ControllerBindings.SystemMenu);
    }

    [Fact]
    public async Task CapturedPageButton_IsCommittedWithOtherControllerBindings()
    {
        var targetSettings = new AppSettings();
        using var viewModel = CreateViewModel(targetSettings);
        var previousPage = Assert.Single(viewModel.ControllerActionBindings, row =>
            row.Target == ControllerBindingTarget.PreviousPage);

        viewModel.CompleteControllerCapture(previousPage, ControllerButton.LeftTrigger);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ControllerButton.LeftTrigger, targetSettings.ControllerBindings.PreviousPage);
        Assert.Equal(ControllerButton.RightShoulder, targetSettings.ControllerBindings.NextPage);
    }

    [Fact]
    public async Task CapturingUsedKeyboardKey_SwapsBindingsAndPersistsOnSave()
    {
        var targetSettings = new AppSettings();
        using var viewModel = CreateViewModel(targetSettings);
        var systemMenu = Assert.Single(viewModel.KeyboardBindings, row =>
            row.Target == KeyboardBindingTarget.SystemMenu);
        var nextPage = Assert.Single(viewModel.KeyboardBindings, row =>
            row.Target == KeyboardBindingTarget.NextPage);

        viewModel.CompleteKeyboardCapture(systemMenu, Key.PageDown);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Key.PageDown, systemMenu.Key);
        Assert.Equal(Key.F10, nextPage.Key);
        Assert.Equal("PageDown", targetSettings.KeyboardBindings.SystemMenu);
        Assert.Equal("F10", targetSettings.KeyboardBindings.NextPage);
    }

    [Fact]
    public async Task ResetKeyboardBindings_RestoresDefaultsOnlyOnSave()
    {
        var targetSettings = new AppSettings
        {
            KeyboardBindings = new KeyboardBindingSettings { SystemMenu = "F9" }
        };
        using var viewModel = CreateViewModel(targetSettings);

        viewModel.ResetKeyboardBindingsCommand.Execute(null);

        Assert.Equal("F9", targetSettings.KeyboardBindings.SystemMenu);
        await viewModel.SaveCommand.ExecuteAsync(null);
        Assert.Equal("F10", targetSettings.KeyboardBindings.SystemMenu);
    }

    [Fact]
    public void KeyboardCapture_IgnoresModifierCombinationsAndWaitsForAPlainKey()
    {
        using var viewModel = CreateViewModel(new AppSettings());
        var details = Assert.Single(viewModel.KeyboardBindings, row =>
            row.Target == KeyboardBindingTarget.Details);
        details.CaptureCommand.Execute(null);

        Assert.True(viewModel.TryCompleteKeyboardCapture(Key.K, KeyModifiers.Control));
        Assert.True(details.IsCapturing);
        Assert.Equal(Key.I, details.Key);

        Assert.True(viewModel.TryCompleteKeyboardCapture(Key.K, KeyModifiers.None));
        Assert.False(details.IsCapturing);
        Assert.Equal(Key.K, details.Key);
    }

    private static SettingsViewModel CreateViewModel(AppSettings targetSettings)
    {
        var httpClient = new HttpClient(new StubHandler());
        var accountService = new RetroAchievementsAccountService(
            new RetroAchievementsApiClient(httpClient),
            new InMemorySecretStore());
        return new SettingsViewModel(
            targetSettings,
            new SettingsService(),
            accountService,
            new RunnerVersionService(AppPaths.DataRoot));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
