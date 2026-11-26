using System.Collections.ObjectModel;
using System.Globalization;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.Search;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Direction = Retromind.Services.GamepadService.GamepadDirection;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeSearchTests
{
    private static async Task OnUi(Func<Task> test) { AvaloniaTestDispatcher.EnsureStarted(); await UiThreadHelper.InvokeAsync(test); }
    private static ObservableCollection<MediaNode> Library()
    {
        var root = new MediaNode("Games"); var child = new MediaNode("Child"); root.Children.Add(child);
        for (var i = 0; i < 18; i++) (i < 10 ? root : child).Items.Add(new MediaItem($"Game {i}") { MediaType = (MediaType)(i % 3) });
        return [root];
    }
    private static async Task Search(BigModeSearchViewModel vm, ManualSearchTimeProvider clock, string text)
    { vm.SearchText = text; clock.Advance(250); await vm.PendingSearch; }

    [Fact]
    public async Task DebounceScopesTypesAndScrollingUseTheSharedEngine() => await OnUi(async () =>
    {
        var roots = Library(); var clock = new ManualSearchTimeProvider();
        using var vm = new BigModeSearchViewModel(roots, roots[0].Children[0], false) { SearchTimeProvider = clock };
        Assert.False(vm.IsObserving);
        foreach (var text in new[] { "G", "Ga", "Gam", "Game" }) vm.SearchText = text;
        clock.Advance(249); Assert.Empty(vm.Results); Assert.False(vm.IsObserving);
        clock.Advance(1); await vm.PendingSearch;
        Assert.Equal(18, vm.Results.Count); Assert.True(vm.IsObserving);
        Assert.Equal(3, vm.Results.Select(row => row.MediaType).Distinct().Count());
        vm.FocusResults();
        for (var i = 0; i < 11; i++) vm.Navigate(Direction.Down);
        Assert.Equal(11, vm.SelectedResultIndex); Assert.Equal(7, vm.FirstVisibleResultIndex);
        vm.ToggleScope(); clock.Advance(250); await vm.PendingSearch;
        Assert.Equal(8, vm.Results.Count); Assert.All(vm.Results, row => Assert.Contains("Child", row.Category));
        await Search(vm, clock, "nonexistent"); Assert.Empty(vm.Results);
        vm.SearchText = " "; Assert.Empty(vm.Results);
    });

    [Theory]
    [InlineData("de", "qwertz", "ö")]
    [InlineData("en", "qwerty", "é")]
    public async Task KeyboardLayersSupportLocalizedLettersSyntaxAndGraphemeDeletion(string language, string prefix, string accent) => await OnUi(async () =>
    {
        var original = Strings.Culture;
        try
        {
            Strings.Culture = CultureInfo.GetCultureInfo(language);
            using var vm = new BigModeSearchViewModel(Library(), null, false);
            Assert.Equal(50, vm.Keys.Count);
            Assert.Equal(prefix, string.Concat(vm.Keys.Take(prefix.Length).Select(key => key.Text)));
            Assert.Contains(vm.Keys, key => key.Text == accent);
            vm.Insert("Grüße 😀e\u0301"); vm.Backspace(); Assert.EndsWith("😀", vm.SearchText);
            vm.Backspace(); Assert.Equal("Grüße ", vm.SearchText);
            vm.PressKey(vm.Keys.Single(key => key.Kind == BigModeSearchKeyKind.Clear)); Assert.Empty(vm.SearchText);
            vm.ToggleLayer(); Assert.Equal(50, vm.Keys.Count);
            foreach (var text in new[] { "0", "9", ":", "\"", "'", "(", ")", "=", "<", ">", "-", ".", "_", "&", "|", "*", "\\" })
                Assert.Contains(vm.Keys, key => key.Text == text);
            vm.Insert("tag:space"); vm.SelectionStart = 4; vm.SelectionEnd = 9; vm.Insert("rpg"); Assert.Equal("tag:rpg", vm.SearchText);
        }
        finally { Strings.Culture = original; }
        await Task.CompletedTask;
    });

    [Fact]
    public async Task ProtectionEditsInvalidateImmediatelyAndPreserveStableSelectionAfterRefresh() => await OnUi(async () =>
    {
        var roots = Library(); var clock = new ManualSearchTimeProvider();
        using var vm = new BigModeSearchViewModel(roots, roots[0], true) { SearchTimeProvider = clock };
        await Search(vm, clock, "Game"); vm.SelectedResultIndex = 4; var id = vm.SelectedResult!.ItemId;
        roots[0].Items[0].Description = "edited";
        Assert.Empty(vm.Results); clock.Advance(250); await vm.PendingSearch;
        Assert.Equal(id, vm.SelectedResult!.ItemId);
        var target = vm.SelectedResult.Item;
        target.IsProtected = true; Assert.Empty(vm.Results); Assert.False(vm.TryResolveSelected(out _, out _));
        clock.Advance(250); await vm.PendingSearch; Assert.DoesNotContain(vm.Results, row => row.ItemId == id);
        vm.SetProtection(false); Assert.Empty(vm.Results); clock.Advance(250); await vm.PendingSearch;
        Assert.Contains(vm.Results, row => row.ItemId == id);
    });

    [Fact]
    public async Task ObsoleteWorkersCannotPublishAndObserverIsRemovedOnDispose() => await OnUi(async () =>
    {
        var roots = Library(); var clock = new ManualSearchTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = new BigModeSearchViewModel(roots, roots[0], false)
        {
            SearchTimeProvider = clock,
            EvaluateAsync = async (snapshot, query, token) =>
            {
                var old = LibrarySearchEvaluator.Evaluate(snapshot, query);
                started.TrySetResult(); await release.Task; return old;
            }
        };
        vm.SearchText = "Game"; clock.Advance(250); var pending = vm.PendingSearch;
        await started.Task; vm.Dispose(); release.TrySetResult(); await pending;
        Assert.Empty(vm.Results); Assert.False(vm.IsObserving);
        roots[0].Items[0].Title = "Changed after disposal";
        Assert.Same(Task.CompletedTask, vm.PendingSearch);
    });

    [Fact]
    public async Task SearchOpenCancelAndRevealDoNotProcessIntermediateLibrarySelections() => await OnUi(async () =>
    {
        var roots = Library(); using var f = new BigModeNavigationFixture(roots);
        f.Session.ActivateSelected(); var previous = f.Vm.SelectedItem;
        Assert.True(f.Vm.OpenSearch()); var context = f.Vm.Search!;
        context.SearchText = "Game 17"; await context.PendingSearch;
        Assert.Same(previous, f.Vm.SelectedItem); Assert.Single(context.Results);
        context.FocusResults(); Assert.True(await f.Vm.ActivateSearchResultAsync(false));
        Assert.False(f.Vm.IsSearchOpen); Assert.Equal("Game 17", f.Vm.SelectedItem!.Title);
        Assert.Same(roots[0].Children[0], f.Vm.CurrentNode);
        Assert.False(context.IsObserving);
        Assert.True(f.Vm.OpenSearch()); f.Vm.CloseSearch(); Assert.Equal("Game 17", f.Vm.SelectedItem.Title);
    });

    [Fact]
    public async Task SystemMenuAndCustomKeyboardBindingConsumeContextKeysAndNativeTextDoesNotTriggerActions() => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected();
        f.Settings.KeyboardBindings.Search = "F8";
        Assert.True(f.Vm.HandleKeyboardKeyDown(Avalonia.Input.Key.F8, 0)); Assert.True(f.Vm.IsSearchOpen);
        f.Vm.HandleSearchTextKeyDown(Avalonia.Input.Key.F8, 0); Assert.True(f.Vm.IsSearchOpen); // Held opener consumed.
        f.Vm.HandleKeyboardKeyUp(Avalonia.Input.Key.F8);
        Assert.False(f.Vm.HandleSearchTextKeyDown(Avalonia.Input.Key.I, 0));
        Assert.False(f.Vm.HandleSearchTextKeyDown(Avalonia.Input.Key.Space, 0));
        Assert.False(f.Vm.HandleSearchTextKeyDown(Avalonia.Input.Key.Back, 0));
        f.Call("InitializeSystemMenu");
        f.Vm.ToggleSystemMenu(); Assert.False(f.Vm.IsSearchOpen); Assert.True(f.Vm.IsSystemMenuOpen);
        f.Vm.SelectedSystemMenuEntry = f.Vm.SystemMenuEntries.Single(e => e.Kind == BigModeSystemMenuEntryKind.Search);
        f.Vm.ActivateSystemMenuSelection(); Assert.True(f.Vm.IsSearchOpen); Assert.False(f.Vm.IsSystemMenuOpen);
        f.Vm.Search!.FocusArea = BigModeSearchFocus.Scope;
        f.Vm.HandleSearchTextKeyDown(Avalonia.Input.Key.Enter, 0);
        Assert.True(f.Vm.Search.CurrentCategoryOnly); Assert.True(f.Vm.Search.IsScopeFocused);
        f.Vm.HandleKeyboardKeyUp(Avalonia.Input.Key.Enter);
        await Task.CompletedTask;
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateTitlesRevealActualOwnerAndLaunchThroughRegularRequest(bool launch) => await OnUi(async () =>
    {
        var roots = Library(); var target = new MediaItem("Duplicate") { MediaType = MediaType.Command };
        roots[0].Items.Add(new("Duplicate")); roots[0].Children[0].Items.Add(target);
        using var f = new BigModeNavigationFixture(roots); f.Session.ActivateSelected();
        MediaItem? launched = null;
        f.Vm.RequestPlay += item => { Assert.False(f.Vm.IsSearchOpen); launched = item; return Task.CompletedTask; };
        f.Vm.OpenSearch(); var vm = f.Vm.Search!; vm.SearchText = "Duplicate"; await vm.PendingSearch;
        Assert.Equal(2, vm.Results.Count); Assert.Equal(2, vm.Results.Select(r => r.Category).Distinct().Count());
        vm.SelectedResultIndex = vm.Results.ToList().FindIndex(r => r.ItemId == target.Id);
        Assert.True(await f.Vm.ActivateSearchResultAsync(launch));
        Assert.Same(target, f.Vm.SelectedItem); Assert.Same(roots[0].Children[0], f.Vm.CurrentNode);
        Assert.Equal(launch, ReferenceEquals(target, launched)); Assert.False(vm.IsObserving);
    });

    [Theory]
    [InlineData("move")]
    [InlineData("remove")]
    [InlineData("protect")]
    [InlineData("replace")]
    public async Task CapturedRowIsRevalidatedAgainstLiveIdentityAndOwner(string change) => await OnUi(async () =>
    {
        var roots = Library(); var clock = new ManualSearchTimeProvider();
        using var vm = new BigModeSearchViewModel(roots, roots[0], true) { SearchTimeProvider = clock };
        await Search(vm, clock, "id:" + roots[0].Items[0].Id); var row = Assert.Single(vm.Results); var target = row.Item;
        switch (change)
        {
            case "move": roots[0].Items.Remove(target); roots[0].Children[0].Items.Add(target); break;
            case "remove": roots[0].Items.Remove(target); break;
            case "protect": target.IsProtected = true; break;
            case "replace": roots[0].Items.Remove(target); roots[0].Items.Add(new("Replacement") { Id = target.Id }); break;
        }
        Assert.Empty(vm.Results);
        var valid = BigModeSearchViewModel.TryResolveTarget(roots, true, row, out var item, out var owner);
        Assert.Equal(change == "move", valid);
        if (valid) { Assert.Same(target, item); Assert.Same(roots[0].Children[0], owner); }
        clock.Advance(250); await vm.PendingSearch;
        Assert.Equal(change is "move" or "replace" ? 1 : 0, vm.Results.Count);
        roots[0].Children[0].Name = "Renamed"; clock.Advance(250); await vm.PendingSearch;
        if (valid) Assert.Contains("Renamed", vm.Results[0].Category);
    });

    [Fact]
    public async Task NewQueryWinsEvenWhenPreviousWorkerIgnoresCancellation() => await OnUi(async () =>
    {
        var clock = new ManualSearchTimeProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0; var oldCanceled = false;
        using var vm = new BigModeSearchViewModel(Library(), null, false)
        {
            SearchTimeProvider = clock,
            EvaluateAsync = async (snapshot, query, token) =>
            {
                if (++count == 1) { started.SetResult(); await release.Task; oldCanceled = token.IsCancellationRequested; }
                return LibrarySearchEvaluator.Evaluate(snapshot, query);
            }
        };
        vm.SearchText = "Game"; clock.Advance(250); var old = vm.PendingSearch; await started.Task;
        await Search(vm, clock, "Game 17"); Assert.Equal("Game 17", Assert.Single(vm.Results).Title);
        release.SetResult(); await old; Assert.True(oldCanceled);
        Assert.Equal("Game 17", Assert.Single(vm.Results).Title);
    });

    [Fact]
    public async Task DesktopCountersScopeAndQueryStayIndependent() => await OnUi(async () =>
    {
        var roots = Library(); var clock = new ManualSearchTimeProvider();
        using var desktop = new SearchAreaViewModel(roots, new RecordingErrorService(), false);
        desktop.SearchText = "Game 17";
        // SearchArea owns its own debounce, counters and results.
        await Task.Delay(350);
        var count = desktop.SearchResults.Count;
        using var big = new BigModeSearchViewModel(roots, roots[0], false) { SearchTimeProvider = clock };
        await Search(big, clock, "Game"); big.ToggleScope(); clock.Advance(250); await big.PendingSearch;
        Assert.Equal("Game 17", desktop.SearchText); Assert.Equal(count, desktop.SearchResults.Count);
        desktop.SearchText = "absent"; Assert.Equal("Game", big.SearchText); Assert.Equal(18, big.Results.Count);
    });

    [Fact]
    public async Task ContextTransitionsConsumeHeldControllerActionsAndRepeatDeletionWithoutMovingLibrary() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected();
            f.Settings.ControllerBindings.Search = ControllerButton.RightStick;
            using var service = Connect(f);
            Button(service, Silk.NET.SDL.GameControllerButton.DpadRight, true); dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input);
            var selected = f.Vm.SelectedEntryIndex;
            Button(service, Silk.NET.SDL.GameControllerButton.Rightstick, true);
            Button(service, Silk.NET.SDL.GameControllerButton.A, true); // Queued in previous context.
            dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input);
            Assert.True(f.Vm.IsSearchOpen); var vm = f.Vm.Search!; Assert.Empty(vm.SearchText);
            BigModeInputLifecycleTests.AssertStopped(f);
            Button(service, Silk.NET.SDL.GameControllerButton.DpadRight, true); dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input);
            Assert.Equal(0, vm.SelectedKeyIndex);
            Button(service, Silk.NET.SDL.GameControllerButton.DpadRight, false);
            Button(service, Silk.NET.SDL.GameControllerButton.DpadRight, true); dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input);
            Assert.Equal(1, vm.SelectedKeyIndex);
            vm.Insert("abcdef");
            Button(service, Silk.NET.SDL.GameControllerButton.Leftshoulder, true); dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input);
            Assert.Equal("abcde", vm.SearchText);
            f.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty); Assert.Equal("abcd", vm.SearchText);
            f.Vm.NotifyNavigationInputLost(); BigModeInputLifecycleTests.AssertStopped(f);
            f.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty); Assert.Equal("abcd", vm.SearchText);
            Button(service, Silk.NET.SDL.GameControllerButton.Leftshoulder, false);
            Button(service, Silk.NET.SDL.GameControllerButton.DpadRight, false);
            Button(service, Silk.NET.SDL.GameControllerButton.DpadRight, true); dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input);
            Assert.Equal(2, vm.SelectedKeyIndex);
            service.ProcessEvent(new Silk.NET.SDL.Event { Cdevice = new Silk.NET.SDL.ControllerDeviceEvent
                { Type = (uint)Silk.NET.SDL.EventType.Controllerdeviceremoved, Which = 1 } });
            dispatcher.RunJobs(Avalonia.Threading.DispatcherPriority.Input); BigModeInputLifecycleTests.AssertStopped(f);
            vm.FocusResults(); vm.Back(); Assert.True(vm.IsKeyboardFocused);
            f.Vm.CloseSearch(); Assert.Equal(selected, f.Vm.SelectedEntryIndex);
            Assert.Empty(vm.Results); Assert.Empty(vm.Keys); Assert.False(vm.IsObserving);
        });

    [Fact]
    public async Task SearchPausesAttractAndPreservesTheAlreadyPlayingFallback() => await AvaloniaTestDispatcher.InvokeAsync(_ =>
    {
        using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected();
        f.Set("_theme", new Theme(new Avalonia.Controls.Border(), new ThemeSounds(), AppContext.BaseDirectory,
            primaryVideoEnabled: false, secondaryVideoEnabled: false, attractModeEnabled: true, attractModeIdleInterval: TimeSpan.FromSeconds(1)));
        f.Set("_currentPreviewVideoPath", "/fallback.mp4"); f.Set("_presentedPreviewVideoPath", "/fallback.mp4");
        f.Set("_mainVideoIsPlayingA", true); f.Set("_activePreviewIndex", 0); f.Set("_previewPlayGeneration", 7);
        f.Vm.OpenSearch(); f.Set("_lastUserInputUtc", DateTime.UtcNow.AddMinutes(-1));
        f.Call("OnAttractTimerTick", null, EventArgs.Empty);
        Assert.False(f.Vm.IsInAttractMode); Assert.Equal(7, f.Get<int>("_previewPlayGeneration"));
        Assert.Equal("/fallback.mp4", f.Get<string>("_currentPreviewVideoPath"));
        f.Vm.CloseSearch(); Assert.Equal(7, f.Get<int>("_previewPlayGeneration"));
        Assert.True(DateTime.UtcNow - f.Get<DateTime>("_lastUserInputUtc") < TimeSpan.FromSeconds(1));
    });


    [Theory]
    [InlineData("protect")]
    [InlineData("new_search")]
    public async Task ResultActivationRevalidatesAfterTheAsynchronousHomeTransition(string interruption) => await OnUi(async () =>
    {
        var roots = Library(); using var f = new BigModeNavigationFixture(roots, parentalFilter: true, home: true,
            settings: new AppSettings { EnableBigModeHomeScreen = true });
        f.Session.ActivateSelected(); await f.CallAsync("ToggleHomeAsync"); Assert.True(f.Vm.IsHomeActive);
        MediaItem? launched = null; f.Vm.RequestPlay += item => { launched = item; return Task.CompletedTask; };
        Assert.True(f.Vm.OpenSearch()); var context = f.Vm.Search!; Assert.False(context.CanUseCategory);
        context.SearchText = "Game 17"; await context.PendingSearch; var target = Assert.Single(context.Results).Item;
        var activating = f.Vm.ActivateSearchResultAsync(true);
        Assert.False(activating.IsCompleted);
        if (interruption == "protect") target.IsProtected = true; else Assert.True(f.Vm.OpenSearch());
        Assert.False(await activating); Assert.Null(launched);
        if (interruption == "new_search") Assert.True(f.Vm.IsSearchOpen);
    });

    [Fact]
    public async Task ScopeDirectionsStayStableAndAllFocusRegionsAreReachable() => await OnUi(async () =>
    {
        using var vm = new BigModeSearchViewModel(Library(), new MediaNode("Scope"), false);
        vm.Navigate(Direction.Up); Assert.True(vm.IsScopeFocused);
        vm.Navigate(Direction.Right); vm.Navigate(Direction.Right); Assert.True(vm.CurrentCategoryOnly);
        vm.Navigate(Direction.Left); Assert.False(vm.CurrentCategoryOnly);
        vm.Navigate(Direction.Down); Assert.True(vm.IsKeyboardFocused);
        vm.SelectedKeyIndex = 49; vm.Navigate(Direction.Down); Assert.True(vm.IsResultsButtonFocused);
        vm.Confirm(); Assert.True(vm.IsResultsFocused); vm.Back(); Assert.True(vm.IsKeyboardFocused);
        var closed = false; vm.RequestClose += () => closed = true; vm.Back(); Assert.True(closed);
        vm.Dispose(); vm.Insert("ignored"); vm.ToggleLayer(); vm.Confirm(); Assert.Empty(vm.SearchText); Assert.Empty(vm.Keys);
        await Task.CompletedTask;
    });


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockingThePlayingLibraryItemStopsObsoletePreviewButKeepsSharedFallback(bool sharedFallback) => await AvaloniaTestDispatcher.InvokeAsync(_ =>
    {
        using var temp = new TemporaryDirectory();
        using var environment = new EnvironmentVariableScope(("APPIMAGE", temp.GetPath("Retromind.AppImage")), ("APPDIR", null));
        var oldPath = temp.CreateFile("Library/old.mp4"); temp.CreateFile("Library/new.mp4");
        var hidden = new MediaItem("Alpha") { IsProtected = true, Assets = [new() { Type = AssetType.Video, RelativePath = "Library/old.mp4" }] };
        var visible = new MediaItem("Beta") { Assets = [new() { Type = AssetType.Video, RelativePath = "Library/new.mp4" }] };
        var node = new MediaNode("Library") { Items = [hidden, visible] };
        if (sharedFallback)
        {
            oldPath = temp.CreateFile("Library/fallback.mp4");
            hidden.Assets.Clear(); visible.Assets.Clear(); node.VideoFallbackEnabled = true;
            node.Assets.Add(new() { Type = AssetType.Video, RelativePath = "Library/fallback.mp4" });
        }
        using var f = new BigModeNavigationFixture([node]);
        f.Session.ActivateSelected(); Assert.Same(hidden, f.Vm.SelectedItem);
        f.Set("_currentPreviewVideoPath", oldPath); f.Set("_presentedPreviewVideoPath", oldPath);
        f.Set("_mainVideoIsPlayingA", true); f.Set("_activePreviewIndex", 0);
        f.Vm.OpenSearch(); Assert.Equal(oldPath, f.Get<string>("_currentPreviewVideoPath"));
        f.Vm.SetParentalFilterActive(true);
        Assert.Same(visible, f.Vm.SelectedItem); Assert.True(f.Vm.IsSearchOpen);
        Assert.Equal(sharedFallback ? oldPath : null, f.Get<string?>("_currentPreviewVideoPath"));
        Assert.Equal(sharedFallback, f.Get<bool>("_mainVideoIsPlayingA"));
    });

    private static GamepadService Connect(BigModeNavigationFixture fixture)
    {
        var service = new GamepadService(fixture.Settings, loadSdl: false); fixture.Set("_gamepadService", service);
        service.OnDirectionStateChanged += (d, p) => fixture.Call("OnGamepadDirectionStateChanged", d, p);
        service.OnPageStateChanged += (d, p) => fixture.Call("OnGamepadPageStateChanged", d, p);
        service.OnSearch += () => fixture.Call("OnGamepadSearch");
        service.OnSelect += () => fixture.Call("OnGamepadSelect");
        return service;
    }
    private static void Button(GamepadService service, Silk.NET.SDL.GameControllerButton button, bool down) =>
        service.ProcessEvent(new Silk.NET.SDL.Event { Cbutton = new Silk.NET.SDL.ControllerButtonEvent
            { Type = (uint)(down ? Silk.NET.SDL.EventType.Controllerbuttondown : Silk.NET.SDL.EventType.Controllerbuttonup), Which = 1, Button = (byte)button } });

}
