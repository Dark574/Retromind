using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Input;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Tests.TestInfrastructure;
using Retromind.ViewModels;
using Direction = Retromind.Services.GamepadService.GamepadDirection;

namespace Retromind.Tests.ViewModels;

public sealed class BigModeReviewRegressionTests
{
    private static ObservableCollection<MediaNode> Library() =>
        [new("Games") { Items = [new("Alpha"), new("Beta"), new("Gamma")] }];
    private static async Task OnUi(Func<Task> test)
    { AvaloniaTestDispatcher.EnsureStarted(); await UiThreadHelper.InvokeAsync(test); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowFocusLossDoesNotRequireAMissingKeyUpBeforeTheNextPress(bool nativeText) => await OnUi(async () =>
    {
        using var fixture = new BigModeNavigationFixture(Library()); fixture.Session.ActivateSelected();
        using var controller = new GamepadService(fixture.Settings, loadSdl: false);
        ManagedCursorFactory.EnsureRegistered();
        var session = new DesktopBigModeSession(fixture.Settings, () => null, _ => Task.FromResult<LaunchResult?>(null), () => Task.CompletedTask);
        session.Open(_ => fixture.Vm);
        var shell = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        SetShell(shell, "_gamepadService", controller); SetShell(shell, "_bigModeSession", session);
        if (nativeText)
        {
            fixture.Vm.OpenSearch(); Assert.False(fixture.Vm.HandleSearchTextKeyDown(Key.I, 0));
        }
        else fixture.Vm.HandleKeyboardKeyDown(Key.Right, 0);
        shell.SetGamepadUiInputEnabled(false);
        var selectedBeforeBlurInput = fixture.Vm.SelectedEntryIndex;
        Assert.True(nativeText ? fixture.Vm.HandleSearchTextKeyDown(Key.I, 0) : fixture.Vm.HandleKeyboardKeyDown(Key.Right, 0));
        Assert.Equal(selectedBeforeBlurInput, fixture.Vm.SelectedEntryIndex); // Late key events during deactivation are consumed.
        // The physical release occurred in another application: Retromind receives no KeyUp.
        shell.SetGamepadUiInputEnabled(true);
        if (nativeText) Assert.False(fixture.Vm.HandleSearchTextKeyDown(Key.I, 0));
        else
        {
            fixture.Vm.HandleKeyboardKeyDown(Key.Right, 0);
            Assert.Equal(2, fixture.Vm.SelectedEntryIndex);
        }
        await Task.CompletedTask;
    });

    [Fact]
    public async Task OverlayTransitionsStillConsumeHeldKeysUntilRelease() => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected();
        f.Vm.HandleKeyboardKeyDown(Key.Right, 0); f.Vm.OpenSearch();
        var selected = f.Vm.Search!.SelectedKeyIndex;
        f.Vm.NotifyWindowFocusChanged(true); // Duplicate activation must not clear overlay hold protection.
        f.Vm.HandleKeyboardKeyDown(Key.Right, 0); Assert.Equal(selected, f.Vm.Search.SelectedKeyIndex);
        f.Vm.HandleKeyboardKeyUp(Key.Right); f.Vm.HandleKeyboardKeyDown(Key.Right, 0);
        Assert.Equal(selected+1, f.Vm.Search.SelectedKeyIndex);
        f.Vm.CloseSearch(); f.Vm.HandleKeyboardKeyDown(Key.Right, 0); Assert.Equal(1, f.Vm.SelectedEntryIndex);
        f.Vm.HandleKeyboardKeyUp(Key.Right); f.Vm.HandleKeyboardKeyDown(Key.Right, 0); Assert.Equal(2, f.Vm.SelectedEntryIndex);
        await Task.CompletedTask;
    });

    [Fact]
    public async Task WindowReactivationDoesNotConsumeAnAlreadyReceivedFreshControllerPress() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected();
            using var controller = new GamepadService(f.Settings, loadSdl: false); f.Set("_gamepadService", controller);
            controller.OnDirectionStateChanged += (direction, pressed) => f.Call("OnGamepadDirectionStateChanged", direction, pressed);
            controller.SetUiInputEnabled(false); f.Vm.NotifyWindowFocusChanged(false);
            controller.SetUiInputEnabled(true);
            // Same order as the main window bridge; SDL can deliver between enabling it and notifying the VM.
            controller.ProcessEvent(new Silk.NET.SDL.Event { Cbutton = new Silk.NET.SDL.ControllerButtonEvent
                { Type = (uint)Silk.NET.SDL.EventType.Controllerbuttondown, Which = 1, Button = (byte)Silk.NET.SDL.GameControllerButton.DpadRight } });
            f.Vm.NotifyWindowFocusChanged(true);
            dispatcher.RunJobs(DispatcherPriority.Input);
            Assert.Equal(1, f.Vm.SelectedEntryIndex);
            controller.ProcessEvent(new Silk.NET.SDL.Event { Cbutton = new Silk.NET.SDL.ControllerButtonEvent
                { Type = (uint)Silk.NET.SDL.EventType.Controllerbuttonup, Which = 1, Button = (byte)Silk.NET.SDL.GameControllerButton.DpadRight } });
            dispatcher.RunJobs(DispatcherPriority.Input); BigModeInputLifecycleTests.AssertStopped(f);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedAlphabetCategoryCannotStartOrContinueLibraryRepeat(bool alreadyRepeating) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            var roots = Library(); roots.Add(new("Other")); roots.Add(new("Third"));
            using var f = new BigModeNavigationFixture(roots); f.Session.ActivateSelected(); f.Vm.OpenAlphabetNavigation();
            if (alreadyRepeating)
            {
                f.Call("OnGamepadDirectionStateChanged", Direction.Right, true); dispatcher.RunJobs(DispatcherPriority.Input);
            }
            roots.RemoveAt(0);
            if (alreadyRepeating) f.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty);
            else { f.Call("OnGamepadDirectionStateChanged", Direction.Right, true); dispatcher.RunJobs(DispatcherPriority.Input); }
            Assert.False(f.Vm.IsAlphabetNavigationOpen);
            BigModeInputLifecycleTests.AssertStopped(f);
            var selected = f.Vm.SelectedEntryIndex;
            f.Call("OnGamepadRepeatTimerTick", null, EventArgs.Empty); Assert.Equal(selected, f.Vm.SelectedEntryIndex);
        });

    [Theory]
    [InlineData(false, "keyboard")]
    [InlineData(true, "keyboard")]
    [InlineData(true, "controller")]
    [InlineData(true, "page")]
    [InlineData(true, "selection")]
    [InlineData(true, "command")]
    [InlineData(true, "menu")]
    [InlineData(true, "new_search")]
    [InlineData(true, "focus")]
    [InlineData(true, "window_focus")]
    [InlineData(true, "controller_page")]
    [InlineData(true, "round_trip")]
    public async Task NewerIntentDuringHomeReturnInvalidatesTheOldResultActivation(bool launch, string input) => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library(), home: true,
            settings: new AppSettings { EnableBigModeHomeScreen = true });
        f.Session.ActivateSelected(); await f.CallAsync("ToggleHomeAsync"); f.Call("InitializeSystemMenu");
        f.Vm.OpenSearch(); f.Vm.Search!.SearchText = "Gamma"; await f.Vm.Search.PendingSearch;
        MediaItem? launched = null; f.Vm.RequestPlay += item => { launched = item; return Task.CompletedTask; };
        MediaItem? expected = null;
        var interrupted = false;
        f.Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BigModeViewModel.IsHomeActive) || f.Vm.IsHomeActive || interrupted) return;
            interrupted = true;
            switch (input)
            {
                case "keyboard": f.Vm.HandleKeyboardKeyDown(Key.Right, 0); break;
                case "controller": f.Call("OnGamepadDirectionStateChanged", Direction.Right, true); break;
                case "page": f.Vm.NavigatePage(1); break;
                case "selection": f.Vm.SelectedItem = f.Vm.Items[1]; break;
                case "command": f.Vm.SelectNextCommand.Execute(null); break;
                case "menu": f.Vm.ToggleSystemMenu(); f.Vm.CloseSystemMenu(); break;
                case "new_search": f.Vm.OpenSearch(); f.Vm.CloseSearch(); break;
                case "focus": f.Vm.NotifyNavigationInputLost(); break;
                case "window_focus": f.Vm.NotifyWindowFocusChanged(false); f.Vm.NotifyWindowFocusChanged(true); break;
                case "controller_page": f.Call("OnGamepadPageStateChanged", 1, true); break;
                case "round_trip": f.Vm.HandleKeyboardKeyDown(Key.Right, 0); f.Vm.HandleKeyboardKeyDown(Key.Left, 0); break;
            }
            expected = (f.Session.Current.SelectedEntry as BigModeMediaNavigationEntry)?.Item;
        };
        Assert.False(await f.Vm.ActivateSearchResultAsync(launch)); Assert.True(interrupted); Assert.Null(launched);
        if (input is "controller" or "controller_page")
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
            Assert.Equal(input == "controller" ? "Beta" : "Gamma", f.Vm.SelectedItem!.Title);
        }
        else Assert.Same(expected, f.Vm.SelectedItem);
    });

    [Theory]
    [InlineData(false, "keyboard")]
    [InlineData(true, "keyboard")]
    [InlineData(false, "controller")]
    [InlineData(true, "controller")]
    [InlineData(false, "menu")]
    [InlineData(true, "menu")]
    public async Task InputBeforeHomeRestorationCancelsTheOwnedHomeExit(bool launch, string input) => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library(), home: true,
            settings: new AppSettings { EnableBigModeHomeScreen = true });
        f.Session.ActivateSelected(); await f.CallAsync("ToggleHomeAsync"); f.Call("InitializeSystemMenu");
        f.Vm.OpenSearch(); f.Vm.Search!.SearchText = "Gamma"; await f.Vm.Search.PendingSearch;
        MediaItem? launched = null; f.Vm.RequestPlay += item => { launched = item; return Task.CompletedTask; };
        var activation = f.Vm.ActivateSearchResultAsync(launch);
        Assert.True(f.Vm.IsHomeActive); Assert.False(activation.IsCompleted);
        if (input == "keyboard") f.Vm.HandleKeyboardKeyDown(Key.Right, 0);
        else if (input == "controller") f.Call("OnGamepadDirectionStateChanged", Direction.Right, true);
        else f.Vm.ToggleSystemMenu();
        Assert.False(await activation);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Assert.True(f.Vm.IsHomeActive); Assert.Null(launched);
        if (input == "menu") Assert.True(f.Vm.IsSystemMenuOpen);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PointerHomeSelectionSupersedesAPendingResultActivation(bool launch) => await OnUi(async () =>
    {
        var roots = Library(); roots.Add(new("Other") { Items = [new("Omega")] });
        using var f = new BigModeNavigationFixture(roots, home: true,
            settings: new AppSettings { EnableBigModeHomeScreen = true });
        f.Session.ActivateSelected(); await f.CallAsync("ToggleHomeAsync"); f.Vm.OpenSearch();
        f.Vm.Search!.SearchText = "Gamma"; await f.Vm.Search.PendingSearch;
        MediaItem? launched = null; f.Vm.RequestPlay += item => { launched = item; return Task.CompletedTask; };
        var activation = f.Vm.ActivateSearchResultAsync(launch);
        var section = f.Vm.HomeSections.Single(s => s.IsLibraryNavigation);
        var target = section.Entries.Single(e => ReferenceEquals(e.Node, roots[1]));
        section.SelectedEntry = target; // Native Home list selection binding, without a keyboard/controller action.
        Assert.False(await activation); Assert.True(f.Vm.IsHomeActive);
        Assert.Same(target, f.Vm.SelectedHomeEntry); Assert.Null(launched);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActivationOwnHomeTransitionRemainsValid(bool launch, bool nestedOwner) => await OnUi(async () =>
    {
        var roots = Library();
        if (nestedOwner) roots[0].Children.Add(new("Subcategory") { Items = [new("Nested")] });
        using var f = new BigModeNavigationFixture(roots, home: true,
            settings: new AppSettings { EnableBigModeHomeScreen = true });
        f.Session.ActivateSelected(); await f.CallAsync("ToggleHomeAsync"); f.Vm.OpenSearch();
        f.Vm.Search!.SearchText = nestedOwner ? "Nested" : "Gamma"; await f.Vm.Search.PendingSearch;
        MediaItem? launched = null; f.Vm.RequestPlay += item => { launched = item; return Task.CompletedTask; };
        f.Call("OnGamepadDirectionStateChanged", Direction.Right, true); // Queued in the old search context, consumed by closing it.
        var activation = f.Vm.ActivateSearchResultAsync(launch);
        f.Vm.HandleKeyboardKeyUp(Key.Enter); // Releasing confirmation is not a new intent.
        f.Call("OnGamepadDirectionStateChanged", Direction.Right, false);
        Assert.True(await activation); Assert.False(f.Vm.IsHomeActive);
        Assert.Same(nestedOwner ? roots[0].Children[0] : roots[0], f.Vm.CurrentNode);
        Assert.Equal(nestedOwner ? "Nested" : "Gamma", f.Vm.SelectedItem!.Title); Assert.Equal(launch, launched != null);
    });

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    public async Task NativeEnterAndTabIgnoreCustomSelectAndScopeBindings(Key key) => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected();
        f.Settings.KeyboardBindings.Select = "K"; f.Settings.KeyboardBindings.NextPage = "Enter";
        f.Vm.OpenSearch(); Assert.True(f.Vm.HandleSearchTextKeyDown(key, 0));
        Assert.True(f.Vm.Search!.IsResultsFocused); Assert.False(f.Vm.Search.CurrentCategoryOnly);
        f.Vm.HandleKeyboardKeyUp(key);
        f.Vm.HandleKeyboardKeyDown(Key.Enter, 0); Assert.True(f.Vm.Search.CurrentCategoryOnly); // Outside native text, configured scope action.
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, "scope")]
    [InlineData(true, "scope")]
    [InlineData(false, "layer")]
    [InlineData(true, "layer")]
    [InlineData(false, "confirm_scope")]
    [InlineData(false, "confirm_layer")]
    [InlineData(false, "confirm_case")]
    public async Task RepeatedKeyboardDownTogglesOnlyOncePerPhysicalHold(bool nativeText, string action) => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected(); f.Vm.OpenSearch();
        f.Settings.KeyboardBindings.NextPage = "F6"; f.Settings.KeyboardBindings.Details = "F7";
        var key = action == "scope" ? Key.F6 : action == "layer" ? Key.F7 : Key.Enter;
        if (action == "confirm_scope") f.Vm.Search!.FocusArea = BigModeSearchFocus.Scope;
        if (action is "confirm_layer" or "confirm_case")
            f.Vm.Search!.SelectedKeyIndex = f.Vm.Search.Keys.ToList().FindIndex(k => k.Kind == (action == "confirm_layer" ? BigModeSearchKeyKind.Layer : BigModeSearchKeyKind.Case));
        var isScope = action is "scope" or "confirm_scope";
        void Down() { if (nativeText) f.Vm.HandleSearchTextKeyDown(key, 0); else f.Vm.HandleKeyboardKeyDown(key, 0); }
        Down();
        Assert.Equal(isScope, f.Vm.Search!.CurrentCategoryOnly);
        var firstKey = f.Vm.Search.Keys[0].Text;
        for (var i=0;i<3;i++) Down();
        Assert.Equal(isScope, f.Vm.Search.CurrentCategoryOnly); Assert.Equal(firstKey, f.Vm.Search.Keys[0].Text);
        f.Vm.HandleKeyboardKeyUp(key); Down();
        if (!isScope) Assert.NotEqual(firstKey, f.Vm.Search.Keys[0].Text);
        else Assert.False(f.Vm.Search.CurrentCategoryOnly);
        await Task.CompletedTask;
    });

    [Fact]
    public async Task NavigationDeletionAndNativeTypingRemainRepeatable() => await OnUi(async () =>
    {
        using var f = new BigModeNavigationFixture(Library()); f.Session.ActivateSelected(); f.Vm.OpenSearch();
        f.Vm.Search!.Insert("abcdef");
        for(var i=0;i<3;i++) f.Vm.HandleKeyboardKeyDown(Key.PageUp, 0);
        Assert.Equal("abc", f.Vm.Search.SearchText);
        for(var i=0;i<3;i++) f.Vm.HandleKeyboardKeyDown(Key.Right, 0);
        Assert.Equal(3, f.Vm.Search.SelectedKeyIndex);
        for(var i=0;i<3;i++) Assert.False(f.Vm.HandleSearchTextKeyDown(Key.A, 0));
        await Task.CompletedTask;
    });

    private static void SetShell(MainWindowViewModel shell, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(shell, value);
}
