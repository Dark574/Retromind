using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services.BigMode;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private long _searchResultActivationGeneration;
    private int _searchResultHomeTransitionGeneration;
    private void InvalidateSearchResultActivation()
    {
        Interlocked.Increment(ref _searchResultActivationGeneration);
        var ownedHomeTransition = Volatile.Read(ref _searchResultHomeTransitionGeneration);
        // Until Home has actually been left, a newer intent also cancels the
        // activation's owned exit. Never cancel a subsequent, unrelated transition.
        if (ownedHomeTransition != 0 && IsHomeActive)
            Interlocked.CompareExchange(ref _homeTransitionGeneration, unchecked(ownedHomeTransition + 1), ownedHomeTransition);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchOpen))]
    private BigModeSearchViewModel? _search;
    public bool IsSearchOpen => Search != null;
    public string SearchMenuTitle => T("BigModeSearch_MenuTitle", "Search");
    private void OnGamepadSearch() => DispatchGamepadInput(() => { if (IsSearchOpen) CloseSearch(); else OpenSearch(); });

    public bool OpenSearch()
    {
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress) return false;
        if (IsSearchOpen) return true;
        Interlocked.Increment(ref _homeTransitionGeneration);
        FlushNavigationChanges();
        CloseSystemMenu(); CloseAlphabetNavigation(resumePreview: false); CloseAchievementsOverlay();
        EndNavigationScroll(resumePreview: true); CancelPreviewDebounce(); ResetAttractIdleTimer();
        Search = new BigModeSearchViewModel(_rootNodes, IsHomeActive ? null : CurrentNode, _parentalFilterActive, _errorService);
        Search.RequestClose += OnSearchClose;
        Search.RequestResultAction += OnSearchResultAction;
        Search.PropertyChanged += OnSearchPropertyChanged;
        UpdateSearchControls();
        return true;
    }
    private void OnSearchClose() => CloseSearch();
    public bool CloseSearch(bool resumePreview = true)
    {
        if (Search is not { } search) return false;
        Search = null;
        search.PropertyChanged -= OnSearchPropertyChanged;
        search.RequestClose -= OnSearchClose;
        search.RequestResultAction -= OnSearchResultAction;
        search.Dispose();
        EndNavigationScroll(resumePreview: true); ResetAttractIdleTimer();
        if (resumePreview && _disposed == 0) TriggerPreviewPlaybackWithDebounce();
        return true;
    }
    private void OnSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BigModeSearchViewModel.FocusArea))
        {
            EndNavigationScroll(resumePreview: true);
            UpdateSearchControls();
        }
        if (e.PropertyName is nameof(BigModeSearchViewModel.SearchText) or nameof(BigModeSearchViewModel.CurrentCategoryOnly)) ResetAttractIdleTimer();
    }
    private void UpdateSearchControls()
    {
        if (Search is not { } search) return;
        string Button(ControllerButton value) => ControllerButtonDisplayHelper.FormatCompact(value);
        var c = _settings.ControllerBindings; var k = _settings.KeyboardBindings;
        var primary = search.IsResultsFocused ? T("BigModeSearch_Show", "Show medium") : T("BigModeSearch_EnterKey", "Enter / select");
        var details = search.IsResultsFocused ? T("BigModeSearch_Launch", "Start medium") : T("BigModeSearch_Layer", "Letters / symbols");
        search.ControlsText = string.Format(T("BigModeSearch_Controls", "D-pad / {14} · Navigate    {0} / {1} · {2}    {3} / {4} · {5}    {6} / {7} · Back\n{8} / {9} · Keyboard / results    {10} / {11} · Delete character (hold)    {12} / {13} · Search scope"),
            Button(c.Select), string.Join(" / ", new[] { KeyboardBindingHelper.Format(k.Select, Key.Enter), KeyboardBindingHelper.Format(k.AlternateSelect, Key.Space) }), primary,
            Button(c.Details), KeyboardBindingHelper.Format(k.Details, Key.I), details,
            Button(c.Back), KeyboardBindingHelper.Format(k.Back, Key.Back),
            Button(c.Home), KeyboardBindingHelper.Format(k.Home, Key.Home),
            Button(c.PreviousPage), KeyboardBindingHelper.Format(k.PreviousPage, Key.PageUp),
            Button(c.NextPage), KeyboardBindingHelper.Format(k.NextPage, Key.PageDown),
            string.Join(" / ", KeyboardBindingHelper.Format(k.NavigateUp, Key.Up), KeyboardBindingHelper.Format(k.NavigateDown, Key.Down),
                KeyboardBindingHelper.Format(k.NavigateLeft, Key.Left), KeyboardBindingHelper.Format(k.NavigateRight, Key.Right)));
        if (search.IsKeyboardFocused) search.ControlsText += "\n" + T("BigModeSearch_PhysicalHint", "Physical keyboard: type directly · Enter / Tab: results");
    }
    private void OnSearchResultAction(bool launch) => _ = ActivateSearchResultAsync(launch);
    public async Task<bool> ActivateSearchResultAsync(bool launch)
    {
        if (Search is not { } search || !search.TryResolveSelected(out var item, out _)) return false;
        var row = search.SelectedResult!;
        CloseSearch(resumePreview: false);
        try
        {
            // ToggleHomeAsync consumes input synchronously before its first yield.
            // Capture after that owned boundary, before newer UI input can overtake us.
            var homeTransition = IsHomeActive ? ToggleHomeAsync() : Task.CompletedTask;
            var ownedHomeTransition = !homeTransition.IsCompleted && IsHomeActive
                ? Volatile.Read(ref _homeTransitionGeneration) : 0;
            if (ownedHomeTransition != 0)
                Volatile.Write(ref _searchResultHomeTransitionGeneration, ownedHomeTransition);
            var generation = Volatile.Read(ref _searchResultActivationGeneration);
            try { await homeTransition; }
            finally
            {
                if (ownedHomeTransition != 0)
                    Interlocked.CompareExchange(ref _searchResultHomeTransitionGeneration, 0, ownedHomeTransition);
            }
            if (generation != Volatile.Read(ref _searchResultActivationGeneration) ||
                _disposed != 0 || _isLaunching || _isCloseRequestInProgress || InputContext != BigModeInputContext.Library ||
                !BigModeSearchViewModel.TryResolveTarget(_rootNodes, _parentalFilterActive, row, out item, out _)) return false;
            _navigation.Refresh(_parentalFilterActive);
            if (!_navigation.TryReveal(new(BigModeNavigationEntryKind.MediaItem, row.ItemId))) return false;
            if (launch) await PlayCurrent();
            return true;
        }
        catch (Exception ex)
        {
            try { _errorService?.Report(new ErrorReport("BigMode.SearchActionFailed", ErrorSeverity.Error, nameof(BigModeViewModel),
                "The selected search result could not be activated.", Context: new Dictionary<string,string> { ["FailureType"] = ex.GetType().Name })); }
            catch { }
            return false;
        }
        finally { if (_disposed == 0 && !_isLaunching) TriggerPreviewPlaybackWithDebounce(); }
    }

    private bool HandleSearchAction(BigModeKeyboardAction action, bool isRepeat = false)
    {
        if (Search is not { } search) return true;
        var toggleKey = search.IsKeyboardFocused && search.SelectedKeyIndex >= 0 && search.SelectedKeyIndex < search.Keys.Count &&
            search.Keys[search.SelectedKeyIndex].Kind is BigModeSearchKeyKind.Layer or BigModeSearchKeyKind.Case;
        if (isRepeat && (action is BigModeKeyboardAction.NextPage or BigModeKeyboardAction.Details ||
            action == BigModeKeyboardAction.Select && (search.IsScopeFocused || toggleKey))) return true;
        switch (action)
        {
            case BigModeKeyboardAction.NavigateUp: search.Navigate(Services.GamepadService.GamepadDirection.Up); break;
            case BigModeKeyboardAction.NavigateDown: search.Navigate(Services.GamepadService.GamepadDirection.Down); break;
            case BigModeKeyboardAction.NavigateLeft: search.Navigate(Services.GamepadService.GamepadDirection.Left); break;
            case BigModeKeyboardAction.NavigateRight: search.Navigate(Services.GamepadService.GamepadDirection.Right); break;
            case BigModeKeyboardAction.Select: search.Confirm(); break;
            case BigModeKeyboardAction.Details: search.Details(); break;
            case BigModeKeyboardAction.Home: if (search.IsResultsFocused) search.FocusKeyboard(); else search.FocusResults(); break;
            case BigModeKeyboardAction.PreviousPage: search.Backspace(); break;
            case BigModeKeyboardAction.NextPage: search.ToggleScope(); break;
            case BigModeKeyboardAction.Back: search.Back(); break;
            case BigModeKeyboardAction.ExitBigMode:
            case BigModeKeyboardAction.Search: CloseSearch(); break;
            case BigModeKeyboardAction.SystemMenu: ToggleSystemMenu(); break;
        }
        ResetAttractIdleTimer();
        return true;
    }
    /// <summary>Native text editing owns printable bindings and editing keys, including Backspace.</summary>
    public bool HandleSearchTextKeyDown(Key key, KeyModifiers modifiers)
    {
        if (_windowInputInactive) return true;
        if (!IsSearchOpen) return false;
        if (!Search!.IsKeyboardFocused) return HandleKeyboardKeyDown(key, modifiers);
        var isRepeat = !_pressedKeyboardKeys.Add(key);
        if (_blockedKeyboardKeys.Contains(key)) return true;
        if (key is Key.Enter or Key.Tab) { Search!.FocusResults(); return true; }
        if (modifiers != KeyModifiers.None || key is Key.Back or Key.Delete or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End ||
            key is >= Key.A and <= Key.Z || key is >= Key.D0 and <= Key.D9 || key is >= Key.NumPad0 and <= Key.NumPad9 || key is Key.Add or Key.Subtract or Key.Multiply or Key.Divide or Key.Decimal or Key.Separator || key == Key.Space || key.ToString().StartsWith("Oem", StringComparison.Ordinal)) return false;
        if (KeyboardBindingHelper.TryResolveAction(_settings.KeyboardBindings, key, out var action))
        {
            if (action == BigModeKeyboardAction.Select) { Search!.FocusResults(); return true; }
            return HandleSearchAction(action, isRepeat);
        }
        return false;
    }
}
