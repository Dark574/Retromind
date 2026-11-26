using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Silk.NET.SDL;
using Xunit.Abstractions;
using Direction = Retromind.Services.GamepadService.GamepadDirection;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeAlphabetNavigationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Open_PreselectsCurrentGroup_GridAndCancelDoNotSelectLibraryEntries() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = Create("Zulu", "Alpha", "Apple", "Beta");
            fixture.Session.TrySelect(2);
            var changes = 0;
            fixture.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == "SelectedEntry") changes++; };
            Assert.True(fixture.Vm.OpenAlphabetNavigation());
            Assert.Equal("A", SelectedGroup(fixture));
            var index = fixture.Get<object>("_alphabetIndex");
            fixture.Vm.NavigateAlphabet(Direction.Right);
            Assert.Equal("B", SelectedGroup(fixture));
            fixture.Vm.NavigatePage(1);
            fixture.Vm.SelectNextCommand.Execute(null);
            fixture.Vm.SelectPreviousCommand.Execute(null);
            fixture.Vm.PlayCurrentCommand.Execute(null);
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
            Assert.Same(index, fixture.Get<object>("_alphabetIndex"));
            Assert.Equal(0, changes);
            Assert.True(fixture.Vm.CloseAlphabetNavigation());
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
            Assert.True(fixture.Vm.OpenAlphabetNavigation());
            SelectGroup(fixture, "B");
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(3, fixture.Vm.SelectedEntryIndex);
            Assert.Equal(1, changes);
            Assert.Equal(BigModeInputContext.Library, fixture.Vm.InputContext);
        });

    [Fact]
    public async Task KeyboardContext_ConsumesHeldKeysOnOpenAndCloseAndUsesConfiguredConfirmBack() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = Create("Alpha", "Beta", "Charlie", "Zulu");
            var launches = 0;
            fixture.Vm.RequestPlay += _ => { launches++; return Task.CompletedTask; };
            var keys = fixture.Settings.KeyboardBindings;
            keys.Alphabet = "F8";
            keys.Select = "K";
            keys.Back = "Q";
            Press(fixture, Key.Right);
            Assert.Equal(1, fixture.Vm.SelectedEntryIndex);
            Press(fixture, Key.F8);
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.True(fixture.Vm.HandleKeyboardKeyDown(Key.Z, KeyModifiers.None)); // No underlying list type-ahead.
            Assert.True(fixture.Vm.HandleKeyboardKeyUp(Key.Z));
            var selected = fixture.Vm.SelectedAlphabetGroupIndex;
            Press(fixture, Key.Right); // Old library arrow is still held.
            Press(fixture, Key.F8); // Opening key repeat may not close the overlay.
            Assert.Equal(selected, fixture.Vm.SelectedAlphabetGroupIndex);
            fixture.Vm.HandleKeyboardKeyUp(Key.Right);
            fixture.Vm.HandleKeyboardKeyUp(Key.F8);
            Press(fixture, Key.Right);
            Assert.Equal("C", SelectedGroup(fixture));
            Press(fixture, Key.K);
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
            Press(fixture, Key.Right);
            Press(fixture, Key.K); // Held confirmation must not launch the selected game.
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
            Assert.False(fixture.Get<bool>("_isLaunching"));
            Assert.Equal(0, launches);
            fixture.Vm.HandleKeyboardKeyUp(Key.K);
            fixture.Vm.HandleKeyboardKeyUp(Key.Right);
            Press(fixture, Key.F8);
            fixture.Vm.HandleKeyboardKeyUp(Key.F8);
            Press(fixture, Key.Q);
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
        });

    [Theory]
    [InlineData(ControllerButton.RightStick)]
    [InlineData(ControllerButton.LeftTrigger)]
    public async Task ControllerBinding_ContextSwitchDropsQueuedActionsAndOldDirectionHold(ControllerButton button) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = Create("Alpha", "Beta", "Charlie", "Zulu");
            fixture.Settings.ControllerBindings.Alphabet = button;
            using var service = ConnectController(fixture);
            Button(service, GameControllerButton.DpadRight, true);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal(1, fixture.Vm.SelectedEntryIndex);
            Assert.True(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            if (button == ControllerButton.LeftTrigger) Trigger(service, true);
            else Button(service, GameControllerButton.Rightstick, true);
            Button(service, GameControllerButton.A, true); // Queued before opening is processed.
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal("B", SelectedGroup(fixture));
            BigModeInputLifecycleTests.AssertStopped(fixture);
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
            Button(service, GameControllerButton.DpadRight, true); // Duplicate held source is consumed.
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal("B", SelectedGroup(fixture));
            Button(service, GameControllerButton.DpadRight, false);
            Button(service, GameControllerButton.DpadRight, true);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal("C", SelectedGroup(fixture));
            Button(service, GameControllerButton.A, true); // Old held confirm remains blocked by the SDL edge state.
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            Button(service, GameControllerButton.A, false);
            Button(service, GameControllerButton.A, true);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
            BigModeInputLifecycleTests.AssertStopped(fixture);
        });

    [Fact]
    public async Task SystemMenu_OpensAlphabetWithOneConfirmAndDoesNotCarryItsConfirmKey() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = Create("Alpha", "Zulu");
            fixture.Call("InitializeSystemMenu");
            fixture.Vm.ToggleSystemMenu();
            fixture.Vm.SelectedSystemMenuEntry = fixture.Vm.SystemMenuEntries.Single(e => e.Kind == BigModeSystemMenuEntryKind.Alphabet);
            Press(fixture, Key.Enter);
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.False(fixture.Vm.IsSystemMenuOpen);
            Press(fixture, Key.Enter);
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            fixture.Vm.HandleKeyboardKeyUp(Key.Enter);
            fixture.Vm.ToggleSystemMenu(); // System menu has explicit priority over alphabet.
            Assert.True(fixture.Vm.IsSystemMenuOpen);
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            fixture.Vm.CloseSystemMenu();
            Assert.Equal(BigModeInputContext.Library, fixture.Vm.InputContext);
        });

    [Fact]
    public async Task LiveEdits_RefreshGroupsAndRejectAVanishedTargetWithoutJumpingElsewhere() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
                { Items = [new("Alpha"), new("Beta"), new("Zulu")] }], parentalFilter: true);
            fixture.Session.ActivateSelected();
            fixture.Vm.OpenAlphabetNavigation();
            SelectGroup(fixture, "Z");
            fixture.Vm.Items[2].IsProtected = true;
            Assert.False(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            fixture.Vm.Items[1].Title = "Omega";
            dispatcher.RunJobs();
            Assert.False(fixture.Vm.AlphabetGroups.Single(g => g.Label == "B").IsAvailable);
            Assert.True(fixture.Vm.AlphabetGroups.Single(g => g.Label == "O").IsAvailable);
            fixture.Vm.Items[1].SortTitle = "Gamma";
            dispatcher.RunJobs();
            Assert.True(fixture.Vm.AlphabetGroups.Single(g => g.Label == "G").IsAvailable);
            SelectGroup(fixture, "G");
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.Equal("Omega", fixture.Vm.SelectedItem!.Title);
            fixture.Vm.OpenAlphabetNavigation();
            fixture.Session.Back();
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
        });

    [Fact]
    public async Task RemovedEntryAndReplacementCollection_RevalidateFirstMatchingIndex() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Library") { Items = [new("Alpha"), new("Zulu first"), new("Zulu second")] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            fixture.Vm.OpenAlphabetNavigation();
            SelectGroup(fixture, "Z");
            root.Items.RemoveAt(1);
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.Equal("Zulu second", fixture.Vm.SelectedItem!.Title);
            fixture.Vm.OpenAlphabetNavigation();
            root.Items = [new("Beta"), new("Alpha"), new("Zulu replacement")];
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.Equal("Zulu replacement", fixture.Vm.SelectedItem!.Title);
        });

    [Fact]
    public async Task SortRuleChangeWhileOpen_RebuildsEffectiveGroups() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var previous = MediaSortHelper.IgnoreLeadingArticlesInTitleSort;
            try
            {
                MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(false);
                using var fixture = Create("The Alpha", "Beta");
                fixture.Vm.OpenAlphabetNavigation();
                Assert.Equal("T", SelectedGroup(fixture));
                MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(true);
                dispatcher.RunJobs();
                Assert.False(fixture.Vm.AlphabetGroups.Single(g => g.Label == "T").IsAvailable);
                Assert.True(fixture.Vm.AlphabetGroups.Single(g => g.Label == "A").IsAvailable);
            }
            finally { MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(previous); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedFallback_ContinuesAcrossAlphabetJump(bool pending) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var env = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/fallback.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                Items = [new("Alpha"), new("Zulu")], VideoFallbackEnabled = true,
                Assets = [new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" }]
            }]);
            fixture.Session.ActivateSelected();
            fixture.Vm.CanShowVideo = true;
            fixture.Set("_isViewReady", true);
            fixture.Set("_previewPlayGeneration", 7);
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_presentedPreviewVideoPath", pending ? null : path);
            fixture.Set("_pendingPreviewVideoPath", pending ? path : null);
            fixture.Set("_mainVideoIsPlayingA", !pending);
            fixture.Set("_mainVideoHasFrameA", !pending);
            fixture.Set("_activePreviewIndex", pending ? -1 : 0);
            fixture.Vm.OpenAlphabetNavigation();
            SelectGroup(fixture, "Z");
            fixture.Vm.ConfirmAlphabetNavigation();
            fixture.Call("OnPreviewDebounceTimerTick", null, EventArgs.Empty);
            Assert.Equal(7, fixture.Get<int>("_previewPlayGeneration"));
            Assert.Equal(path, fixture.Get<string?>("_currentPreviewVideoPath"));
            Assert.False(fixture.Get<bool>("_suspendPreviewDuringScroll"));
        });

    [Fact]
    public async Task FocusLossAndControllerRemoval_LeaveOverlayStableAndRequireFreshInput() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = Create("Alpha", "Beta", "Zulu");
            using var service = ConnectController(fixture);
            fixture.Vm.OpenAlphabetNavigation();
            Button(service, GameControllerButton.DpadRight, true);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal("B", SelectedGroup(fixture));
            service.ProcessEvent(new Event { Cdevice = new ControllerDeviceEvent { Type = (uint)EventType.Controllerdeviceremoved, Which = 1 } });
            dispatcher.RunJobs(DispatcherPriority.Input);
            BigModeInputLifecycleTests.AssertStopped(fixture);
            fixture.Vm.NotifyNavigationInputLost();
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            fixture.Call("OnGamepadSelect");
            fixture.Vm.CloseAlphabetNavigation();
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            Assert.False(fixture.Get<bool>("_isLaunching"));
        });

    [Fact]
    public async Task MixedFrame_JumpsBetweenNodeAndMediaWithoutOpeningTheNode() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            var root = new MediaNode("Mixed")
                { Children = [new("Zulu category")], Items = [new("Alpha media"), new("Zulu media")] };
            using var fixture = new BigModeNavigationFixture([root]);
            fixture.Session.ActivateSelected();
            fixture.Vm.OpenAlphabetNavigation();
            SelectGroup(fixture, "A");
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.Equal(1, fixture.Vm.SelectedEntryIndex);
            fixture.Vm.OpenAlphabetNavigation();
            SelectGroup(fixture, "Z");
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            Assert.Same(root, fixture.Vm.CurrentNode);
            Assert.Equal(1, fixture.Session.Depth);
            Assert.IsType<BigModeNodeNavigationEntry>(fixture.Vm.SelectedEntry);
        });

    [Fact]
    public async Task QueuedExit_CannotCrossContexts_AndFreshExitOnlyClosesTheAlphabet() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = Create("Alpha", "Zulu");
            var handled = 0;
            var closes = 0;
            fixture.Vm.RequestClose += () => { closes++; return Task.CompletedTask; };
            fixture.Vm.DispatchControllerExit(() => handled++);
            fixture.Vm.OpenAlphabetNavigation();
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal(0, handled);
            Assert.True(fixture.Vm.IsAlphabetNavigationOpen);
            fixture.Vm.DispatchControllerExit(() => handled++);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal(1, handled);
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            Assert.Equal(0, closes);
        });

    [Fact]
    public async Task ExplicitBigModeClose_EndsAlphabetAndQueuedInput() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = Create("Alpha", "Zulu");
            fixture.Vm.RequestClose += () => Task.CompletedTask;
            fixture.Vm.OpenAlphabetNavigation();
            fixture.Call("OnGamepadSelect");
            var close = fixture.CallAsync("RequestBigModeCloseAsync");
            dispatcher.RunJobs();
            Assert.True(close.IsCompletedSuccessfully);
            Assert.False(fixture.Vm.IsAlphabetNavigationOpen);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            BigModeInputLifecycleTests.AssertStopped(fixture);
        });

    [Fact]
    public async Task PausedPreviewWithoutPlayer_RecoversThroughTheNormalStartAfterCancel() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var env = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var path = temp.CreateFile("Library/video.mp4");
            using var fixture = new BigModeNavigationFixture([new MediaNode("Library")
            {
                Items = [new MediaItem("Alpha") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/video.mp4" }] }]
            }]);
            fixture.Session.ActivateSelected();
            fixture.Vm.CanShowVideo = true;
            fixture.Set("_isViewReady", true);
            fixture.Set("_previewPausedForScroll", true);
            fixture.Set("_suspendPreviewDuringScroll", true);
            fixture.Set("_pausedPreviewIndex", 0);
            fixture.Set("_pausedPreviewVideoPath", path);
            fixture.Set("_currentPreviewVideoPath", path);
            fixture.Set("_mainVideoIsPlayingA", true);
            fixture.Vm.OpenAlphabetNavigation();
            Assert.False(fixture.Get<bool>("_mainVideoIsPlayingA"));
            fixture.Vm.CloseAlphabetNavigation();
            try
            {
                fixture.Call("OnPreviewDebounceTimerTick", null, EventArgs.Empty);
                Assert.Equal(path, fixture.Get<string?>("_pendingPreviewVideoPath"));
            }
            finally { fixture.Call("StopVideo"); }
        });

    [Fact]
    public async Task AttractMode_RemainsIdleWhileAlphabetIsOpenAndRestartsWithFreshIdleTime() =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var fixture = Create("Alpha", "Beta", "Zulu");
            fixture.Set("_theme", new Theme(new Border(), new ThemeSounds(), AppContext.BaseDirectory,
                primaryVideoEnabled: false, secondaryVideoEnabled: false, attractModeEnabled: true,
                attractModeIdleInterval: TimeSpan.FromSeconds(1)));
            fixture.Vm.OpenAlphabetNavigation();
            fixture.Set("_lastUserInputUtc", DateTime.UtcNow.AddMinutes(-1));
            fixture.Call("OnAttractTimerTick", null, EventArgs.Empty);
            Assert.False(fixture.Vm.IsInAttractMode);
            Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
            fixture.Vm.CloseAlphabetNavigation();
            fixture.Call("OnAttractTimerTick", null, EventArgs.Empty);
            Assert.False(fixture.Vm.IsInAttractMode);
            Assert.True(DateTime.UtcNow - fixture.Get<DateTime>("_lastUserInputUtc") < TimeSpan.FromSeconds(1));
            fixture.Set("_lastUserInputUtc", DateTime.UtcNow.AddMinutes(-1));
            fixture.Call("OnAttractTimerTick", null, EventArgs.Empty);
            Assert.True(fixture.Get<int>("_attractStepsExecuted") > 0);
            fixture.Call("StopAttractModeTimer");
        });

    [Fact]
    public async Task EmptyLibraryAndHome_AreHandledWithoutChangingLibrarySelection() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var empty = new BigModeNavigationFixture([]);
            Assert.True(empty.Vm.OpenAlphabetNavigation());
            Assert.Equal(-1, empty.Vm.SelectedAlphabetGroupIndex);
            Assert.All(empty.Vm.AlphabetGroups, group => Assert.False(group.IsAvailable));
            empty.Vm.NavigateAlphabet(Direction.Down);
            Assert.False(empty.Vm.ConfirmAlphabetNavigation());
            empty.Vm.CloseAlphabetNavigation();
            using var home = new BigModeNavigationFixture([new("Library")], home: true,
                settings: new AppSettings { EnableBigModeHomeScreen = true });
            var transition = home.CallAsync("ToggleHomeAsync");
            dispatcher.RunJobs();
            Assert.True(transition.IsCompletedSuccessfully);
            Assert.False(home.Vm.OpenAlphabetNavigation());
            Assert.Equal(BigModeInputContext.Home, home.Vm.InputContext);
            home.Call("InitializeSystemMenu");
            home.Vm.ToggleSystemMenu();
            Assert.False(home.Vm.SystemMenuEntries.Single(e => e.Kind == BigModeSystemMenuEntryKind.Alphabet).IsEnabled);
            home.Vm.NavigateSystemMenu(Direction.Down);
            Assert.Equal(BigModeSystemMenuEntryKind.Search, home.Vm.SelectedSystemMenuEntry!.Kind);
            home.Vm.NavigateSystemMenu(Direction.Down);
            Assert.Equal(BigModeSystemMenuEntryKind.ControllerBindings, home.Vm.SelectedSystemMenuEntry!.Kind);
        });

    [Theory]
    [InlineData(10000)]
    [InlineData(50000)]
    public async Task LargeFrame_CachesGridAndProcessesOnlyConfirmedTarget(int count) =>
        await AvaloniaTestDispatcher.InvokeAsync(_ =>
        {
            using var temp = new TemporaryDirectory();
            using var env = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
            var root = new MediaNode("Large library")
            {
                Children = new(Enumerable.Range(0, 20).Select(i => new MediaNode($"Category {i}"))),
                Items = new(Enumerable.Range(0, count).Select(i => new MediaItem(i == count - 1 ? "Zulu" : $"Alpha {i}")
                    { Files = [new() { Kind = MediaFileKind.LibraryRelative, Path = $"Library/media-{i}.rom" }] }))
            };
            using var fixture = new BigModeNavigationFixture([root]);
            var probes = new List<string>();
            fixture.Vm.PreviewFileExistsOverride = path => { probes.Add(path); return path.EndsWith(".rom", StringComparison.Ordinal); };
            fixture.Vm.CanShowVideo = true;
            fixture.Session.ActivateSelected();
            fixture.Session.TrySelect(20);
            fixture.Vm.CircularLogoPreloadCount = 2;
            Assert.NotEmpty(fixture.Vm.CircularItems);
            probes.Clear();
            var watch = Stopwatch.StartNew();
            fixture.Vm.OpenAlphabetNavigation();
            watch.Stop();
            var buildMs = watch.Elapsed.TotalMilliseconds;
            var index = fixture.Get<object>("_alphabetIndex");
            var selected = new List<int>();
            fixture.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == "SelectedEntry") selected.Add(fixture.Vm.SelectedEntryIndex); };
            watch.Restart();
            for (var i = 0; i < 1000; i++) fixture.Vm.NavigateAlphabet(Direction.Right, playSound: false);
            watch.Stop();
            var moveMs = watch.Elapsed.TotalMilliseconds;
            Assert.Same(index, fixture.Get<object>("_alphabetIndex"));
            Assert.Empty(probes);
            Assert.Empty(selected);
            Assert.Null(fixture.Get<CancellationTokenSource?>("_circularLogoPreloadCts"));
            SelectGroup(fixture, "Z");
            watch.Restart();
            Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
            watch.Stop();
            Assert.Equal([count + 19], selected);
            Assert.Equal(10, probes.Count);
            Assert.Equal(temp.GetPath($"Library/media-{count - 1}.rom"), probes[0]);
            Assert.NotNull(fixture.Get<CancellationTokenSource?>("_circularLogoPreloadCts"));
            output.WriteLine($"{count} media + 20 categories; index={buildMs:F2}ms; 1000 grid moves={moveMs:F2}ms; " +
                $"confirm={watch.Elapsed.TotalMilliseconds:F2}ms; target selections={selected.Count}; video probes={probes.Count}");
        });

    [Theory]
    [InlineData("Default")]
    [InlineData("Cinematic")]
    [InlineData("HorizontalRow")]
    [InlineData("Prism")]
    [InlineData("ArchiveAtlas")]
    [InlineData("Navigator")]
    [InlineData("Wheel")]
    [InlineData("Arcade")]
    [InlineData("LivingRoom")]
    [InlineData("System")]
    [InlineData("external")]
    public async Task HostOverlay_UsesTheSameGridForEveryThemeContract(string themeName) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var fixture = Create("Alpha", "Beta", "Zulu");
            var path = Path.Combine(AppContext.BaseDirectory, "Themes", themeName, "theme.axaml");
            var content = themeName == "external" ? Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Control>(
                "<UserControl xmlns=\"https://github.com/avaloniaui\"><Border /></UserControl>") :
                Avalonia.Markup.Xaml.AvaloniaRuntimeXamlLoader.Parse<Control>(File.ReadAllText(path));
            // Native animation clocks are outside the managed navigation fixture.
            ThemeProperties.SetPrimaryVisualEnterMode(content, "None");
            ThemeProperties.SetSecondaryVisualEnterMode(content, "None");
            ThemeProperties.SetBackgroundVisualEnterMode(content, "None");
            ThemeProperties.SetFadeDurationMs(content, 0);
            ThemeProperties.SetMoveDurationMs(content, 0);
            var theme = new Theme(content, new ThemeSounds(), AppContext.BaseDirectory,
                primaryVideoEnabled: false, secondaryVideoEnabled: false,
                navigationPresentation: ThemeProperties.GetNavigationPresentation(content));
            fixture.Set("_theme", theme);
            ManagedCursorFactory.EnsureRegistered();
            var host = new Retromind.Views.BigModeHostView { DataContext = fixture.Vm };
            try
            {
                host.SetThemeContent(content, theme);
                fixture.Vm.OpenAlphabetNavigation();
                dispatcher.RunJobs();
                var grid = host.FindControl<ListBox>("AlphabetGrid")!;
                Assert.True(host.FindControl<Grid>("AlphabetOverlay")!.IsVisible);
                Assert.Equal(fixture.Vm.AlphabetGroups.Count, grid.ItemCount);
                Assert.Equal(fixture.Vm.SelectedAlphabetGroupIndex, grid.SelectedIndex);
                fixture.Vm.NavigateAlphabet(Direction.Right);
                dispatcher.RunJobs();
                Assert.Equal("B", SelectedGroup(fixture));
                Assert.Equal(0, fixture.Vm.SelectedEntryIndex);
                Assert.True(fixture.Vm.ConfirmAlphabetNavigation());
                Assert.False(host.FindControl<Grid>("AlphabetOverlay")!.IsVisible);
                Assert.Equal(1, fixture.Vm.SelectedEntryIndex);
                Assert.True(fixture.Vm.OpenSearch());
                dispatcher.RunJobs();
                var searchView = host.FindControl<Retromind.Views.BigModeSearchView>("SearchView")!;
                Assert.Same(fixture.Vm.Search, searchView.DataContext);
                Assert.Equal(50, searchView.FindControl<ListBox>("SearchKeyboard")!.ItemCount);
                fixture.Vm.Search!.Navigate(Direction.Right);
                Assert.Equal(1, fixture.Vm.Search.SelectedKeyIndex);
                Assert.Equal(1, fixture.Vm.SelectedEntryIndex);
                fixture.Vm.CloseSearch();
            }
            finally { host.DataContext = null; }
        });

    private static BigModeNavigationFixture Create(params string[] titles)
    {
        var fixture = new BigModeNavigationFixture([new MediaNode("Library") { Items = new(titles.Select(t => new MediaItem(t))) }]);
        fixture.Session.ActivateSelected();
        return fixture;
    }
    private static string SelectedGroup(BigModeNavigationFixture fixture) => fixture.Vm.AlphabetGroups[fixture.Vm.SelectedAlphabetGroupIndex].Label;
    private static void SelectGroup(BigModeNavigationFixture fixture, string label)
        => fixture.Vm.SelectedAlphabetGroupIndex = fixture.Vm.AlphabetGroups.ToList().FindIndex(g => g.Label == label);
    private static void Press(BigModeNavigationFixture fixture, Key key) => fixture.Vm.HandleKeyboardKeyDown(key, KeyModifiers.None);
    private static GamepadService ConnectController(BigModeNavigationFixture fixture)
    {
        var service = new GamepadService(fixture.Settings, loadSdl: false);
        fixture.Set("_gamepadService", service);
        service.OnDirectionStateChanged += (direction, pressed) => fixture.Call("OnGamepadDirectionStateChanged", direction, pressed);
        service.OnPageStateChanged += (direction, pressed) => fixture.Call("OnGamepadPageStateChanged", direction, pressed);
        service.OnAlphabet += () => fixture.Call("OnGamepadAlphabet");
        service.OnSelect += () => fixture.Call("OnGamepadSelect");
        service.OnBack += () => fixture.Call("OnGamepadBack");
        return service;
    }
    private static void Button(GamepadService service, GameControllerButton button, bool down) =>
        service.ProcessEvent(new Event { Cbutton = new ControllerButtonEvent
            { Type = (uint)(down ? EventType.Controllerbuttondown : EventType.Controllerbuttonup), Which = 1, Button = (byte)button } });
    private static void Trigger(GamepadService service, bool down) =>
        service.ProcessEvent(new Event { Caxis = new ControllerAxisEvent
            { Type = (uint)EventType.Controlleraxismotion, Which = 1, Axis = (byte)GameControllerAxis.Triggerleft, Value = (short)(down ? 20000 : 0) } });
}
