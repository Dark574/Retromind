using System.Diagnostics;
using Avalonia.Threading;
using Retromind.Extensions;
using Retromind.Models;
using Retromind.Services;
using Retromind.Tests.TestInfrastructure;
using Xunit.Abstractions;
using Direction = Retromind.Services.GamepadService.GamepadDirection;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeScrollAccelerationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(Direction.Down, 1)]
    [InlineData(Direction.Right, 1)]
    [InlineData(Direction.Up, -1)]
    [InlineData(Direction.Left, -1)]
    public async Task DirectionHold_UsesEveryStageAndRetainsTimerCadence(Direction direction, int sign) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture(10000);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            fixture.Session.TrySelect(5000);
            PressDirection(fixture, dispatcher, direction, true);
            var expected = 5000 + sign;
            Assert.Equal(expected, fixture.Vm.SelectedEntryIndex);
            Assert.Equal(TimeSpan.FromMilliseconds(350), fixture.Get<DispatcherTimer>("_gamepadRepeatTimer").Interval);
            foreach (var (milliseconds, step) in new[] { (350, 1), (1000, 2), (2000, 5), (3000, 10), (5000, 20), (8000, 40) })
            {
                clock.SetMilliseconds(milliseconds);
                Tick(fixture);
                expected += sign * step;
                Assert.Equal(expected, fixture.Vm.SelectedEntryIndex);
                Assert.Equal(TimeSpan.FromMilliseconds(80), fixture.Get<DispatcherTimer>("_gamepadRepeatTimer").Interval);
            }
            PressDirection(fixture, dispatcher, direction, false);
            AssertStopped(fixture);
        });

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task PageHold_HasSingleImmediateActionAndSeparateStages(int sign) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture(10000);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            fixture.Session.TrySelect(5000);
            Page(fixture, dispatcher, sign, true);
            var expected = 5000 + sign * 10;
            Assert.Equal(expected, fixture.Vm.SelectedEntryIndex);
            var timer = fixture.Get<DispatcherTimer>("_gamepadRepeatTimer");
            Assert.Equal(TimeSpan.FromMilliseconds(350), timer.Interval);
            Page(fixture, dispatcher, sign, true); // Duplicate edges must not navigate or restart the clock.
            Assert.Equal(expected, fixture.Vm.SelectedEntryIndex);
            foreach (var (milliseconds, step) in new[] { (350, 10), (2000, 20), (3000, 30), (5000, 40), (7000, 50) })
            {
                clock.SetMilliseconds(milliseconds);
                Tick(fixture);
                expected += sign * step;
                Assert.Equal(expected, fixture.Vm.SelectedEntryIndex);
                Assert.Same(timer, fixture.Get<DispatcherTimer>("_gamepadRepeatTimer"));
                Assert.Equal(TimeSpan.FromMilliseconds(80), timer.Interval);
            }
            Page(fixture, dispatcher, sign, false);
            AssertStopped(fixture);
        });

    [Fact]
    public async Task MostRecentInput_OwnsOneTimerAndNeverResumesAnOlderHold() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture(10000);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            fixture.Session.TrySelect(5000);
            PressDirection(fixture, dispatcher, Direction.Down, true);
            clock.SetMilliseconds(8000);
            Tick(fixture);
            Assert.Equal(5041, fixture.Vm.SelectedEntryIndex);
            PressDirection(fixture, dispatcher, Direction.Up, true);
            var timer = fixture.Get<DispatcherTimer>("_gamepadRepeatTimer");
            PressDirection(fixture, dispatcher, Direction.Down, false);
            Tick(fixture);
            Assert.Equal(5039, fixture.Vm.SelectedEntryIndex); // Reversal starts at one again.
            Page(fixture, dispatcher, 1, true);
            Assert.Equal(5049, fixture.Vm.SelectedEntryIndex);
            clock.SetMilliseconds(8350);
            Tick(fixture);
            Assert.Equal(5059, fixture.Vm.SelectedEntryIndex);
            PressDirection(fixture, dispatcher, Direction.Up, false);
            Assert.True(timer.IsEnabled);
            PressDirection(fixture, dispatcher, Direction.Left, true);
            Assert.Equal(5058, fixture.Vm.SelectedEntryIndex);
            Page(fixture, dispatcher, 1, false);
            Assert.True(timer.IsEnabled);
            clock.SetMilliseconds(8700);
            Tick(fixture);
            Assert.Equal(5057, fixture.Vm.SelectedEntryIndex);
            Page(fixture, dispatcher, -1, true);
            Page(fixture, dispatcher, 1, true);
            Page(fixture, dispatcher, -1, false);
            Assert.True(timer.IsEnabled);
            Page(fixture, dispatcher, 1, false);
            AssertStopped(fixture);
            Assert.Same(timer, fixture.Get<DispatcherTimer>("_gamepadRepeatTimer"));
            var selected = fixture.Vm.SelectedEntryIndex;
            Tick(fixture);
            Assert.Equal(selected, fixture.Vm.SelectedEntryIndex);
        });

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(15, false)]
    [InlineData(10000, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(15, true)]
    [InlineData(10000, true)]
    public async Task Hold_ReachesBothEdgesWithoutWrapButFreshPressStillWraps(int count, bool page) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture(count);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            void Input(int sign, bool pressed)
            {
                if (page) Page(fixture, dispatcher, sign, pressed);
                else PressDirection(fixture, dispatcher, sign < 0 ? Direction.Up : Direction.Down, pressed);
            }
            fixture.Session.TrySelect(Math.Max(0, count - 3));
            Input(1, true);
            clock.SetMilliseconds(9000);
            for (var i = 0; i < 30; i++) Tick(fixture);
            Assert.Equal(count - 1, fixture.Vm.SelectedEntryIndex);
            Input(1, false);
            Input(1, true);
            Assert.Equal(count == 0 ? -1 : 0, fixture.Vm.SelectedEntryIndex);
            Input(1, false);
            fixture.Session.TrySelect(Math.Min(2, count - 1));
            Input(-1, true);
            clock.SetMilliseconds(18000);
            for (var i = 0; i < 30; i++) Tick(fixture);
            Assert.Equal(count == 0 ? -1 : 0, fixture.Vm.SelectedEntryIndex);
            Input(-1, false);
            Input(-1, true);
            Assert.Equal(count - 1, fixture.Vm.SelectedEntryIndex);
            Input(-1, false);
        });

    [Theory]
    [InlineData(NavigationPresentation.MixedEntries)]
    [InlineData(NavigationPresentation.MediaItemsOnly)]
    [InlineData(NavigationPresentation.NodeBrowser)]
    [InlineData(NavigationPresentation.LegacySeparate)]
    public async Task MixedAndFilteredFrames_SelectOnlyFinalVisibleTargetAcrossThemeContracts(NavigationPresentation presentation) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var root = new MediaNode("Library")
            {
                Children = new(Enumerable.Range(0, 5).Select(i => new MediaNode($"Category {i}"))),
                Items = new(Enumerable.Range(0, 1000).Select(i => new MediaItem($"Media {i}") { IsProtected = i % 2 == 0 }))
            };
            using var fixture = new BigModeNavigationFixture([root], presentation, parentalFilter: true);
            fixture.Session.ActivateSelected();
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            PressDirection(fixture, dispatcher, Direction.Down, true);
            var changes = new List<int>();
            fixture.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == "SelectedEntry") changes.Add(fixture.Vm.SelectedEntryIndex); };
            clock.SetMilliseconds(8000);
            Tick(fixture);
            Assert.Equal([41], changes);
            Assert.False(fixture.Vm.SelectedItem!.IsProtected);
            Assert.Equal(505, fixture.Vm.NavigationEntries.Count);
            Assert.Same(root, fixture.Vm.ThemeContextNode);
            PressDirection(fixture, dispatcher, Direction.Down, false);
        });

    [Fact]
    public async Task LiveEdits_ResetAccelerationAndRevalidateBoundsAndProtection() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var root = new MediaNode("Library")
                { Items = new(Enumerable.Range(0, 1000).Select(i => new MediaItem($"Media {i}"))) };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            PressDirection(fixture, dispatcher, Direction.Down, true);
            clock.SetMilliseconds(8000);
            Tick(fixture);
            Assert.Equal(41, fixture.Vm.SelectedEntryIndex);
            root.Items[42].IsProtected = true;
            fixture.Vm.SetParentalFilterActive(true);
            Tick(fixture);
            Assert.Same(root.Items[43], fixture.Vm.SelectedItem);
            root.Items.RemoveAt(500);
            clock.SetMilliseconds(16000);
            Tick(fixture);
            Assert.Same(root.Items[44], fixture.Vm.SelectedItem);
            root.Items = new(root.Items.Take(5));
            clock.SetMilliseconds(16000);
            Tick(fixture); // Flush pending collection replacement before computing the target.
            Assert.InRange(fixture.Vm.SelectedEntryIndex, 0, 4);
            root.Items.Clear();
            Tick(fixture);
            Assert.Equal(-1, fixture.Vm.SelectedEntryIndex);
            root.Items.Add(new MediaItem("New"));
            Tick(fixture);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            PressDirection(fixture, dispatcher, Direction.Down, false);
            AssertStopped(fixture);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextMenuFocusAndQueuedInput_EndEitherKindOfHold(bool page) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture(1000);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            void Press()
            {
                if (page) Page(fixture, dispatcher, 1, true);
                else PressDirection(fixture, dispatcher, Direction.Down, true);
            }
            Press();
            clock.SetMilliseconds(8000);
            Tick(fixture);
            fixture.Session.Back();
            AssertStopped(fixture);
            fixture.Session.ActivateSelected();
            var restored = fixture.Vm.SelectedEntryIndex;
            Press();
            Tick(fixture);
            Assert.Equal(restored + (page ? 20 : 2), fixture.Vm.SelectedEntryIndex);
            fixture.Call("InitializeSystemMenu");
            fixture.Vm.ToggleSystemMenu();
            AssertStopped(fixture);
            fixture.Vm.CloseSystemMenu();
            Press();
            fixture.Vm.NotifyNavigationInputLost();
            AssertStopped(fixture);
            // Input posted before focus/context loss may not revive either repeat owner.
            fixture.Call("OnGamepadPageStateChanged", 1, true);
            fixture.Call("OnGamepadDirectionStateChanged", Direction.Down, true);
            fixture.Vm.NotifyNavigationInputLost();
            dispatcher.RunJobs(DispatcherPriority.Input);
            AssertStopped(fixture);
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AcceleratedFallbackJump_KeepsPlayingOrStartingVideo(bool pending, bool page) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/fallback.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                VideoFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }],
                Items = new(Enumerable.Range(0, 1000).Select(i => new MediaItem($"Media {i}")))
            }]);
            fixture.Session.ActivateSelected();
            fixture.Vm.CanShowVideo = true;
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            fixture.Set("_isViewReady", true);
            fixture.Set("_previewPlayGeneration", 7);
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_presentedPreviewVideoPath", pending ? null : path);
            fixture.Set("_pendingPreviewVideoPath", pending ? path : null);
            fixture.Set("_activePreviewIndex", pending ? -1 : 0);
            fixture.Set("_mainVideoIsPlayingA", !pending);
            fixture.Set("_mainVideoHasFrameA", !pending);
            if (page) Page(fixture, dispatcher, 1, true);
            else PressDirection(fixture, dispatcher, Direction.Down, true);
            clock.SetMilliseconds(8000);
            Tick(fixture);
            Assert.Equal(page ? 60 : 41, fixture.Vm.SelectedEntryIndex);
            Assert.False(fixture.Get<bool>("_previewPausedForScroll"));
            Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
            if (page) Page(fixture, dispatcher, 1, false);
            else PressDirection(fixture, dispatcher, Direction.Down, false);
            fixture.Call("OnPreviewDebounceTimerTick", null, EventArgs.Empty);
            Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
            Assert.Equal(path, fixture.Get<string?>("_currentPreviewVideoPath"));
            AssertStopped(fixture);
        });

    [Fact]
    public async Task OpeningNodeWithSamePausedVideo_CannotLeaveStalePlaybackFlags() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/resume.mp4");
            var child = new MediaNode("Child")
            {
                Items = [new MediaItem("Video")
                    { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/resume.mp4" }] }]
            };
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library") { Children = [child] }]);
            fixture.Session.ActivateSelected();
            Page(fixture, dispatcher, 1, true);
            fixture.Set("_previewPausedForScroll", true);
            fixture.Set("_pausedPreviewIndex", 0);
            fixture.Set("_pausedPreviewVideoPath", path);
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_presentedPreviewVideoPath", path);
            fixture.Set("_mainVideoIsPlayingA", true);
            fixture.Vm.CanShowVideo = true;
            fixture.Session.ActivateSelected();
            Assert.Same(child, fixture.Vm.CurrentNode);
            AssertStopped(fixture);
            // There is no native player in this fixture. Resuming must detect
            // that and clear all stale state, rather than abandoning the pause.
            Assert.False(fixture.Get<bool>("_mainVideoIsPlayingA"));
            Assert.Null(fixture.Get<string?>("_currentPreviewVideoPath"));
        });

    [Fact]
    public async Task Home_KeepsNativePagesAndSingleStepDirectionalRepeats() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                Items = new(Enumerable.Range(0, 100).Select(i => new MediaItem($"Media {i}") { IsFavorite = true }))
            }], home: true, settings: new AppSettings { EnableBigModeHomeScreen = true });
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            var transition = fixture.CallAsync("ToggleHomeAsync");
            dispatcher.RunJobs();
            Assert.True(transition.IsCompletedSuccessfully);
            var section = fixture.Vm.HomeSections.First(section => section.IsPaged);
            fixture.Vm.SelectedHomeSection = section;
            Page(fixture, dispatcher, 1, true);
            Assert.Equal(1, section.CurrentPageIndex);
            clock.SetMilliseconds(8000);
            Tick(fixture);
            Assert.Equal(1, section.CurrentPageIndex);
            Page(fixture, dispatcher, 1, false);
            PressDirection(fixture, dispatcher, Direction.Right, true);
            clock.SetMilliseconds(16000);
            Tick(fixture);
            Assert.Same(section.Entries[2], section.SelectedEntry);
            PressDirection(fixture, dispatcher, Direction.Right, false);
            AssertStopped(fixture);
        });

    [Fact]
    public async Task Achievements_KeepExistingGridStepsAndOneShotPages() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = CreateFixture(1000);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            var achievements = Enumerable.Range(0, 100).Select(_ =>
                new Retromind.ViewModels.RetroAchievementsAchievementItemViewModel(new Retromind.Services.RetroAchievements.RetroAchievementsAchievement())).ToArray();
            typeof(Retromind.ViewModels.RetroAchievementsProgressViewModel)
                .GetField("_achievementItems", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(fixture.Vm.RetroAchievementsProgress, achievements);
            fixture.Set("_isAchievementsOverlayOpen", true);
            fixture.Vm.SelectedAchievement = achievements[0];
            PressDirection(fixture, dispatcher, Direction.Right, true);
            clock.SetMilliseconds(8000);
            Tick(fixture);
            Assert.Same(achievements[2], fixture.Vm.SelectedAchievement);
            PressDirection(fixture, dispatcher, Direction.Right, false);
            fixture.Vm.NavigatePage(1);
            var expected = fixture.Vm.SelectedAchievement;
            fixture.Vm.SelectedAchievement = achievements[2];
            Page(fixture, dispatcher, 1, true);
            Assert.Same(expected, fixture.Vm.SelectedAchievement);
            Tick(fixture);
            Assert.Same(expected, fixture.Vm.SelectedAchievement);
            Page(fixture, dispatcher, 1, false);
            AssertStopped(fixture);
        });

    [Theory]
    [InlineData(1, 0)]
    [InlineData(40, 0)]
    [InlineData(1, 1)]
    [InlineData(40, 1)]
    public async Task JumpWorkload_ProcessesOnlyTargetsAndCancelsObsoletePreloads(int step, int probeDelayMilliseconds) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var temp = new TemporaryDirectory();
            using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var root = new MediaNode("Large library")
            {
                Items = new(Enumerable.Range(0, 10000).Select(i => new MediaItem($"Media {i}")
                    { Files = [new() { Kind = MediaFileKind.LibraryRelative, Path = $"Library/media-{i}.rom" }] }))
            };
            using var fixture = new BigModeNavigationFixture([root]);
            var clock = new Clock();
            fixture.Vm.NavigationTimeProvider = clock;
            var probes = new List<string>();
            fixture.Vm.PreviewFileExistsOverride = path =>
            {
                probes.Add(path);
                if (probeDelayMilliseconds > 0) Thread.Sleep(probeDelayMilliseconds);
                return path.EndsWith(".rom", StringComparison.Ordinal);
            };
            fixture.Vm.CanShowVideo = true;
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(1000);
            fixture.Vm.CircularLogoPreloadCount = 2;
            _ = fixture.Vm.CircularItems;
            PressDirection(fixture, dispatcher, Direction.Down, true);
            var previousPreload = fixture.Get<CancellationTokenSource>("_circularLogoPreloadCts").Token;
            var entries = fixture.Vm.NavigationEntries;
            var items = fixture.Vm.Items;
            var targets = new List<int>();
            fixture.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == "SelectedEntry") targets.Add(fixture.Vm.SelectedEntryIndex); };
            probes.Clear();
            const int ticks = 100;
            clock.SetMilliseconds(step == 1 ? 350 : 8000);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < ticks; i++) Tick(fixture);
            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Assert.Equal(Enumerable.Range(1, ticks).Select(i => 1001 + i * step), targets);
            Assert.Equal(ticks * 10, probes.Count);
            var targetPaths = targets.Select(i => temp.GetPath($"Library/media-{i}.rom")).ToHashSet();
            Assert.All(probes.Where(path => path.EndsWith(".rom", StringComparison.Ordinal)), path => Assert.Contains(path, targetPaths));
            Assert.True(previousPreload.IsCancellationRequested);
            Assert.Same(entries, fixture.Vm.NavigationEntries);
            Assert.Same(items, fixture.Vm.Items);
            output.WriteLine($"10,000 entries; {ticks} ticks; step={step}; simulated probe={probeDelayMilliseconds}ms; " +
                $"targets={targets.Count}; probes={probes.Count}; elapsed={watch.Elapsed.TotalMilliseconds:F1}ms; allocated={bytes}B");
            PressDirection(fixture, dispatcher, Direction.Down, false);
            AssertStopped(fixture);
        });

    private static BigModeNavigationFixture CreateFixture(int count)
    {
        var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            { Items = new(Enumerable.Range(0, count).Select(i => new MediaItem($"Media {i}"))) }]);
        fixture.Session.ActivateSelected();
        return fixture;
    }

    private static void PressDirection(BigModeNavigationFixture fixture, Dispatcher dispatcher, Direction direction, bool pressed)
        => BigModeInputLifecycleTests.Direction(fixture, dispatcher, direction, pressed);

    internal static void Page(BigModeNavigationFixture fixture, Dispatcher dispatcher, int direction, bool pressed)
    {
        fixture.Call("OnGamepadPageStateChanged", direction, pressed);
        dispatcher.RunJobs(DispatcherPriority.Input);
    }

    private static void Tick(BigModeNavigationFixture fixture) => fixture.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty);

    private static void AssertStopped(BigModeNavigationFixture fixture)
    {
        BigModeInputLifecycleTests.AssertStopped(fixture);
        Assert.Equal(0, fixture.Get<int>("_gamepadRepeatPageDirection"));
        Assert.Equal(0, fixture.Get<long>("_gamepadRepeatStartedTimestamp"));
        Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        // Wall time deliberately stays fixed: all hold durations use the monotonic timestamp.
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
        internal void SetMilliseconds(int milliseconds) => _timestamp = TimeSpan.FromMilliseconds(milliseconds).Ticks;
    }
}
