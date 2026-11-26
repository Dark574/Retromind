using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Retromind.Extensions;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Retromind.Tests.ViewModels;
using Silk.NET.SDL;

namespace Retromind.Tests.Services;

public sealed class GamepadPageStateTests
{
    [Theory]
    [InlineData(ControllerButton.LeftShoulder, -1)]
    [InlineData(ControllerButton.RightShoulder, 1)]
    public async Task HeldShoulderButton_RepeatsThroughTheRealDispatcherTimer(ControllerButton button, int sign)
    {
        BigModeNavigationFixture? fixture = null;
        GamepadService? service = null;
        Retromind.Views.BigModeHostView? host = null;
        var repeated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
            {
                fixture = new BigModeNavigationFixture([new MediaNode("PC Games")
                    { Items = new(Enumerable.Range(0, 760).Select(i => new MediaItem($"Media {i}"))) }]);
                fixture.Session.ActivateSelected();
                fixture.Session.TrySelect(500);
                var themePath = Path.Combine(AppContext.BaseDirectory, "Themes", "Cinematic");
                var root = AvaloniaRuntimeXamlLoader.Parse<Control>(File.ReadAllText(Path.Combine(themePath, "theme.axaml")));
                var theme = new Theme(root, new ThemeSounds(), themePath, primaryVideoEnabled: true,
                    secondaryVideoEnabled: false, navigationPresentation: NavigationPresentation.MixedEntries);
                fixture.Set("_theme", theme);
                ManagedCursorFactory.EnsureRegistered();
                host = new Retromind.Views.BigModeHostView { DataContext = fixture.Vm };
                host.SetThemeContent(root, theme);
                service = new GamepadService(fixture.Settings, loadSdl: false);
                service.OnPageStateChanged += (direction, pressed) => fixture.Call("OnGamepadPageStateChanged", direction, pressed);
                fixture.Vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "SelectedEntry" && fixture.Vm.SelectedEntryIndex == 500 + sign * 20)
                        repeated.TrySetResult();
                };
                Input(service, 1, button, true);
                dispatcher.RunJobs(DispatcherPriority.Input);
                Assert.Equal(500 + sign * 10, fixture.Vm.SelectedEntryIndex);
                Assert.True(fixture.Get<DispatcherTimer>("_gamepadRepeatTimer").IsEnabled);
            });
            // The existing stage tests invoke ticks directly; this test also
            // verifies that a held SDL button actually reaches a scheduled tick.
            await repeated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
            {
                if (service != null)
                {
                    Input(service, 1, button, false);
                    dispatcher.RunJobs(DispatcherPriority.Input);
                    service.Dispose();
                }
                if (host != null) host.DataContext = null;
                fixture?.Dispose();
            });
        }
    }

    [Theory]
    [InlineData(ControllerButton.RightShoulder)]
    [InlineData(ControllerButton.Paddle1)]
    [InlineData(ControllerButton.LeftTrigger)]
    public async Task ConfiguredPageButton_NavigatesOnceThenRepeatsAndReleases(ControllerButton button) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var settings = new AppSettings { ControllerBindings = new() { NextPage = button } };
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
                { Items = new(Enumerable.Range(0, 1000).Select(i => new MediaItem($"Media {i}"))) }], settings: settings);
            fixture.Session.ActivateSelected();
            using var service = new GamepadService(settings, loadSdl: false);
            service.OnPageStateChanged += (direction, pressed) => fixture.Call("OnGamepadPageStateChanged", direction, pressed);
            var oneShotEvents = 0;
            service.OnNextPage += () => oneShotEvents++;

            Input(service, 1, button, true);
            Input(service, 1, button, true);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal(10, fixture.Vm.SelectedEntryIndex);
            Assert.Equal(1, oneShotEvents);
            fixture.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty);
            Assert.Equal(20, fixture.Vm.SelectedEntryIndex);
            Input(service, 1, button, false);
            dispatcher.RunJobs(DispatcherPriority.Input);
            BigModeInputLifecycleTests.AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Assert.Equal(1, oneShotEvents);
        });

    [Theory]
    [InlineData(ControllerButton.RightShoulder)]
    [InlineData(ControllerButton.LeftTrigger)]
    public void MultipleControllers_ReleaseOnlyAfterLastPageSourceEnds(ControllerButton button)
    {
        using var service = new GamepadService(new AppSettings { ControllerBindings = new() { NextPage = button } }, loadSdl: false);
        var events = new List<(int, bool)>();
        service.OnPageStateChanged += (direction, pressed) => events.Add((direction, pressed));
        Input(service, 1, button, true);
        Input(service, 2, button, true);
        Input(service, 1, button, false);
        Assert.Equal([(1, true)], events);
        service.ProcessEvent(new Event { Cdevice = new ControllerDeviceEvent
            { Type = (uint)EventType.Controllerdeviceremoved, Which = 2 } });
        Assert.Equal([(1, true), (1, false)], events);
    }

    [Fact]
    public void BindingChangedDuringHold_ReleaseUsesTheOriginalSemanticAction()
    {
        var settings = new AppSettings();
        using var service = new GamepadService(settings, loadSdl: false);
        var events = new List<(int, bool)>();
        service.OnPageStateChanged += (direction, pressed) => events.Add((direction, pressed));
        Input(service, 1, ControllerButton.RightShoulder, true);
        settings.ControllerBindings.NextPage = ControllerButton.Paddle1;
        Input(service, 1, ControllerButton.RightShoulder, false);
        Input(service, 1, ControllerButton.Paddle1, true);
        Input(service, 1, ControllerButton.Paddle1, false);
        Assert.Equal([(1, true), (1, false), (1, true), (1, false)], events);
    }

    [Fact]
    public void ReentrantFocusLoss_DeliversReleaseAndDropsQueuedPageActions()
    {
        using var service = new GamepadService(new AppSettings(), loadSdl: false);
        var events = new List<(int, bool)>();
        var oneShotEvents = 0;
        service.OnNextPage += () => oneShotEvents++;
        service.OnPageStateChanged += (direction, pressed) =>
        {
            events.Add((direction, pressed));
            if (pressed) service.SetUiInputEnabled(false);
        };
        Input(service, 1, ControllerButton.RightShoulder, true);
        Assert.Equal([(1, true), (1, false)], events);
        Assert.Equal(0, oneShotEvents);
        Input(service, 1, ControllerButton.RightShoulder, true);
        service.SetUiInputEnabled(true);
        Assert.Equal(2, events.Count);
        Input(service, 1, ControllerButton.RightShoulder, false);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void StopAndFocusTransitions_ClearBothPageDirectionsWithoutReplayingHolds()
    {
        using var service = new GamepadService(new AppSettings(), loadSdl: false);
        var events = new List<(int, bool)>();
        service.OnPageStateChanged += (direction, pressed) => events.Add((direction, pressed));
        Input(service, 1, ControllerButton.LeftShoulder, true);
        Input(service, 1, ControllerButton.RightShoulder, true);
        service.StopMonitoring();
        Assert.Equal([(-1, true), (1, true), (-1, false), (1, false)], events);
        service.StartMonitoring();
        Input(service, 1, ControllerButton.RightShoulder, true);
        service.SetUiInputEnabled(false);
        Input(service, 1, ControllerButton.RightShoulder, true);
        service.SetUiInputEnabled(true);
        Assert.Equal((1, false), events[^1]);
        Assert.Equal(6, events.Count);
    }

    private static void Input(GamepadService service, int instance, ControllerButton button, bool pressed)
    {
        if (button == ControllerButton.LeftTrigger)
        {
            service.ProcessEvent(new Event { Caxis = new ControllerAxisEvent
            {
                Type = (uint)EventType.Controlleraxismotion, Which = instance,
                Axis = (byte)GameControllerAxis.Triggerleft, Value = (short)(pressed ? 20000 : 0)
            } });
            return;
        }
        var sdlButton = button switch
        {
            ControllerButton.LeftShoulder => GameControllerButton.Leftshoulder,
            ControllerButton.RightShoulder => GameControllerButton.Rightshoulder,
            ControllerButton.Paddle1 => GameControllerButton.Paddle1,
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        service.ProcessEvent(new Event { Cbutton = new ControllerButtonEvent
        {
            Type = (uint)(pressed ? EventType.Controllerbuttondown : EventType.Controllerbuttonup),
            Which = instance, Button = (byte)sdlButton
        } });
    }
}
