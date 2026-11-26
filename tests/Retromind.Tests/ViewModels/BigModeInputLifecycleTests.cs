using System.Reflection;
using Avalonia.Input;
using Avalonia.Threading;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.RetroAchievements;
using Retromind.Tests.TestInfrastructure;
using Silk.NET.SDL;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeInputLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HorizontalRowFallback_AfterNativeStartBeforeFirstPresentedFrame_KeepsTheAttempt(int surface) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/fallback.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Commodore 64")
            {
                ThemePath = "HorizontalRow/theme.axaml",
                VideoFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }],
                Items = [new MediaItem("Commando Libya"), new MediaItem("Computershop"), new MediaItem("Conquestador")]
            }]);
            fixture.Session.ActivateSelected();
            var themeRoot = Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Avalonia.Controls.Control>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Themes", "HorizontalRow", "theme.axaml")));
            var theme = new Theme(themeRoot, new ThemeSounds(), AppContext.BaseDirectory,
                primaryVideoEnabled: true, secondaryVideoEnabled: false,
                navigationPresentation: Retromind.Extensions.NavigationPresentation.MixedEntries);
            fixture.Set("_theme", theme);
            ManagedCursorFactory.EnsureRegistered();
            var host = new Retromind.Views.BigModeHostView { DataContext = fixture.Vm };
            host.SetThemeContent(themeRoot, theme);
            try
            {
                fixture.Set("_isViewReady", true);
                fixture.Set("_currentPreviewVideoPath", path);
                fixture.Set("_previewPlayGeneration", 7);
                fixture.Set("_expectedPreviewFrameGeneration", 7);
                fixture.Set("_expectedPreviewSurfaceIndex", surface);
                fixture.Set("_mainVideoIsPlayingA", surface == 0);
                fixture.Set("_mainVideoIsPlayingB", surface == 1);
                fixture.Set("_activePreviewIndex", -1);
                // StartPlaybackOnUiThread's finally already cleared the pending
                // start, while the native first frame has not reached the UI yet.
                fixture.Set("_pendingPreviewVideoPath", null);
                fixture.Set("_pendingPreviewSurfaceIndex", -1);
                fixture.Set("_presentedPreviewVideoPath", null);

                Direction(fixture, dispatcher, GamepadService.GamepadDirection.Right, true);
                Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
                Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
                Assert.Equal(path, fixture.Get<string?>("_currentPreviewVideoPath"));
                fixture.Call("OnMainVideoFrameReady", surface);
                dispatcher.RunJobs();
                Assert.Equal(surface, fixture.Get<int>("_activePreviewIndex"));
                Assert.True(fixture.Vm.IsVideoVisible);
                fixture.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty);
                Direction(fixture, dispatcher, GamepadService.GamepadDirection.Right, false);
                Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
                Assert.Equal(path, fixture.Get<string?>("_presentedPreviewVideoPath"));
            }
            finally { host.DataContext = null; }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingFromFallbackToOwnVideo_StillInterruptsThePreviousPreview(bool pending) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/fallback.mp4");
            temp.CreateFile("Library/own.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                VideoFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }],
                Items = [new MediaItem("Fallback"), new MediaItem("Own")
                    { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/own.mp4" }] }]
            }]);
            fixture.Session.ActivateSelected();
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_presentedPreviewVideoPath", pending ? null : path);
            fixture.Set("_activePreviewIndex", pending ? -1 : 0);
            fixture.Set("_mainVideoIsPlayingA", !pending);
            fixture.Set("_pendingPreviewVideoPath", pending ? path : null);
            fixture.Set("_expectedPreviewSurfaceIndex", 0);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            Assert.Same(fixture.Vm.Items[1], fixture.Vm.SelectedItem);
            Assert.True(fixture.Get<bool>("_previewPausedForScroll"));
            Assert.Null(fixture.Get<string?>("_pendingPreviewVideoPath"));
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
        });

    [Theory]
    [InlineData(false, "Controller")]
    [InlineData(true, "Controller")]
    [InlineData(false, "Keyboard")]
    [InlineData(true, "Keyboard")]
    [InlineData(false, "Page")]
    [InlineData(true, "Page")]
    public async Task SharedFallbackNavigation_KeepsPlayingOrPendingPreview(bool pending, string input) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/fallback.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                VideoFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }],
                Items = new(Enumerable.Range(0, 30).Select(i => new MediaItem($"Media {i}")))
            }]);
            fixture.Session.ActivateSelected();
            fixture.Vm.CanShowVideo = true;
            fixture.Set("_isViewReady", true);
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_previewPlayGeneration", 7);
            fixture.Set("_expectedPreviewFrameGeneration", 7);
            fixture.Set("_expectedPreviewSurfaceIndex", 0);
            fixture.Set("_activePreviewIndex", pending ? -1 : 0);
            fixture.Set("_mainVideoIsPlayingA", !pending);
            fixture.Set("_mainVideoHasFrameA", !pending);
            fixture.Set("_presentedPreviewVideoPath", pending ? null : path);
            fixture.Set("_pendingPreviewVideoPath", pending ? path : null);
            fixture.Set("_pendingPreviewSurfaceIndex", pending ? 0 : -1);
            fixture.Set("_pendingPreviewGeneration", pending ? 7 : 0);
            fixture.Vm.IsVideoVisible = !pending;

            if (input == "Controller")
            {
                Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
                fixture.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty);
            }
            else if (input == "Keyboard")
            {
                fixture.Vm.HandleKeyboardKeyDown(Key.Down, KeyModifiers.None);
                fixture.Vm.HandleKeyboardKeyDown(Key.Down, KeyModifiers.None);
            }
            else fixture.Vm.NavigatePage(1);

            Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
            Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
            Assert.Equal(path, fixture.Get<string?>("_currentPreviewVideoPath"));
            Assert.Equal(pending ? path : null, fixture.Get<string?>("_pendingPreviewVideoPath"));
            Assert.Equal(pending ? null : path, fixture.Get<string?>("_presentedPreviewVideoPath"));

            if (input == "Controller") Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            if (input == "Keyboard") fixture.Vm.HandleKeyboardKeyUp(Key.Down);
            fixture.Call("OnPreviewDebounceTimerTick", null, EventArgs.Empty);
            Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Fact]
    public async Task HoldBeforeFirstVideoFrame_DoesNotPretendAnUnstartedPlayerWasResumed() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/pending.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                Items = [new MediaItem("Video") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/pending.mp4" }] }]
            }]);
            fixture.Session.ActivateSelected();
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_pendingPreviewVideoPath", path);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            Assert.False(fixture.Vm.IsVideoVisible);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
        });

    [Fact]
    public async Task HoldCancelsPendingPreview_AndDisposedVmRejectsQueuedInput() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            fixture.Call("EnsurePreviewDebounceTimer");
            fixture.Get<DispatcherTimer>("_previewDebounceTimer").Start();
            fixture.Set("_pendingPreviewVideoPath", "/pending.mp4");
            var generation = fixture.Get<int>("_previewPlayGeneration");
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            Assert.False(fixture.Get<DispatcherTimer>("_previewDebounceTimer").IsEnabled);
            Assert.Null(fixture.Get<string?>("_pendingPreviewVideoPath"));
            Assert.True(fixture.Get<int>("_previewPlayGeneration") > generation);
            fixture.Vm.NotifyNavigationInputLost();
            fixture.Set("_disposed", 1);
            var selected = fixture.Vm.SelectedEntryIndex;
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Up, true);
            fixture.Vm.NavigatePage(1);
            fixture.Call("OnGamepadSelect");
            dispatcher.RunJobs(DispatcherPriority.Input);
            AssertStopped(fixture);
            Assert.Equal(selected, fixture.Vm.SelectedEntryIndex);
        });

    [Theory]
    [InlineData("Focus")]
    [InlineData("Remove")]
    [InlineData("Stop")]
    public async Task ServiceLifecycleRelease_EndsRepeatAndRestoresPreview(string interruption) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            using var service = new GamepadService(fixture.Settings, loadSdl: false);
            service.OnDirectionStateChanged += (direction, pressed) => fixture.Call("OnGamepadDirectionStateChanged", direction, pressed);
            service.ProcessEvent(new Event { Caxis = new ControllerAxisEvent
                { Type = (uint)EventType.Controlleraxismotion, Which = 1, Axis = (byte)GameControllerAxis.Lefty, Value = 20000 } });
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.True(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            switch (interruption)
            {
                case "Focus": service.SetUiInputEnabled(false); fixture.Vm.NotifyNavigationInputLost(); break;
                case "Remove": service.ProcessEvent(new Event { Cdevice = new ControllerDeviceEvent
                    { Type = (uint)EventType.Controllerdeviceremoved, Which = 1 } }); break;
                case "Stop": service.StopMonitoring(); break;
            }
            dispatcher.RunJobs(DispatcherPriority.Input);
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
        });

    [Fact]
    public async Task FocusLostBeforeQueuedPress_CannotStartAStaleHold() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            fixture.Call("OnGamepadDirectionStateChanged", GamepadService.GamepadDirection.Down, true);
            fixture.Vm.NotifyNavigationInputLost();
            dispatcher.RunJobs(DispatcherPriority.Input);
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Fact]
    public async Task OpposingDirections_KeepMostRecentRepeatUntilItsRelease() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Up, true);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            Assert.Equal(GamepadService.GamepadDirection.Up, fixture.Get<GamepadService.GamepadDirection?>("_gamepadRepeatDirection"));
            Assert.True(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            fixture.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty);
            Assert.Equal(TimeSpan.FromMilliseconds(80), fixture.Get<DispatcherTimer>("_gamepadRepeatTimer").Interval);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Up, false);
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Fact]
    public async Task KeyboardAndController_OnlyLastActiveSourceResumesPreview() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            fixture.Vm.HandleKeyboardKeyDown(Key.Down, KeyModifiers.None);
            fixture.Vm.HandleKeyboardKeyDown(Key.Right, KeyModifiers.None);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            fixture.Vm.HandleKeyboardKeyUp(Key.Down);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            Assert.True(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            fixture.Vm.HandleKeyboardKeyUp(Key.Right);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));

            fixture.Vm.HandleKeyboardKeyDown(Key.Down, KeyModifiers.None);
            fixture.Vm.NotifyNavigationInputLost();
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Fact]
    public async Task HomeTransition_ConsumesHoldAndRestoresLibraryWithoutSuspension() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")], home: true,
                settings: new AppSettings { EnableBigModeHomeScreen = true });
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            var transition = fixture.CallAsync("ToggleHomeAsync");
            dispatcher.RunJobs();
            Assert.True(transition.IsCompletedSuccessfully);
            Assert.True(fixture.Vm.IsHomeActive);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            transition = fixture.CallAsync("ToggleHomeAsync");
            dispatcher.RunJobs();
            Assert.True(transition.IsCompletedSuccessfully);
            Assert.False(fixture.Vm.IsHomeActive);
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Fact]
    public async Task AchievementOverlay_ConsumesHoldAndRestoresPreviewOnClose() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            var progress = fixture.Vm.RetroAchievementsProgress;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Retromind.ViewModels.RetroAchievementsProgressViewModel).GetField("_isEligible", flags)!.SetValue(progress, true);
            typeof(Retromind.ViewModels.RetroAchievementsProgressViewModel).GetField("_snapshot", flags)!.SetValue(progress,
                new RetroAchievementsProgressSnapshot(new() { AchievementCount = 1 }, DateTimeOffset.UtcNow, false));
            // Use the same provider state as a completed RetroAchievements selection.
            var provider = typeof(Retromind.ViewModels.RetroAchievementsProgressViewModel).GetField("_activeProvider", flags)!;
            provider.SetValue(progress, Enum.Parse(provider.FieldType, "RetroAchievements"));
            typeof(Retromind.ViewModels.RetroAchievementsProgressViewModel).GetField("_achievementItems", flags)!.SetValue(progress,
                new[] { new Retromind.ViewModels.RetroAchievementsAchievementItemViewModel(new RetroAchievementsAchievement()) });
            Assert.True(fixture.Vm.CanOpenAchievementsOverlay);
            fixture.Vm.OpenAchievementsOverlay();
            Assert.True(fixture.Vm.IsAchievementsOverlayOpen);
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            fixture.Vm.CloseAchievementsOverlay();
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaSession_ConsumesHoldAndRestoresPreviewAfterReturnOrFailure(bool fail)
    {
        Task? play = null;
        BigModeNavigationFixture? fixture = null;
        var session = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            fixture = CreateFixture();
            fixture.Session.ActivateSelected();
            fixture.Vm.RequestPlay += _ => session.Task;
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            play = fixture.CallAsync("PlayCurrent");
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
        });
        try
        {
            if (fail) session.SetException(new InvalidOperationException("Simulated launch failure"));
            else session.SetResult();
            if (fail) await Assert.ThrowsAsync<InvalidOperationException>(() => play!);
            else await play!.WaitAsync(TimeSpan.FromSeconds(5));
            await AvaloniaTestDispatcher.InvokeAsync(_ =>
            {
                Assert.False(fixture!.Get<bool>("_isLaunching"));
                Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
                AssertStopped(fixture);
            });
        }
        finally { await AvaloniaTestDispatcher.InvokeAsync(_ => fixture!.Dispose()); }
    }

    [Fact]
    public async Task CloseInProgress_IgnoresQueuedDirectionsAndLeavesNoHold() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Vm.RequestClose += () => close.Task;
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            var closing = fixture.CallAsync("RequestBigModeCloseAsync");
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Up, true);
            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            close.SetResult();
            dispatcher.RunJobs();
        });

    [Fact]
    public async Task SystemMenuInterruptsHold_ReleaseAndCloseLeavePreviewUsable() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture();
            fixture.Call("InitializeSystemMenu");
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, true);
            Assert.True(fixture.Get<bool>("_suspendPreviewDuringScroll"));

            fixture.Vm.ToggleSystemMenu();
            Direction(fixture, dispatcher, GamepadService.GamepadDirection.Down, false);
            fixture.Vm.CloseSystemMenu();

            AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
        });

    internal static BigModeNavigationFixture CreateFixture() => new(
        [new MediaNode("Library") { Items = new(Enumerable.Range(0, 20).Select(i => new MediaItem($"Media {i}"))) }]);

    internal static void Direction(BigModeNavigationFixture fixture, Dispatcher dispatcher,
        GamepadService.GamepadDirection direction, bool pressed)
    {
        fixture.Call("OnGamepadDirectionStateChanged", direction, pressed);
        dispatcher.RunJobs(DispatcherPriority.Input);
    }

    internal static void AssertStopped(BigModeNavigationFixture fixture)
    {
        Assert.Null(fixture.Get<GamepadService.GamepadDirection?>("_gamepadRepeatDirection"));
        Assert.False(fixture.Get<DispatcherTimer?>("_gamepadRepeatTimer")?.IsEnabled ?? false);
    }
}
