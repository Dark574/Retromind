using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Services.BigMode;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private DispatcherTimer? _gamepadRepeatTimer;
    private GamepadService.GamepadDirection? _gamepadRepeatDirection;
    private int _gamepadRepeatPageDirection;
    private bool _gamepadRepeatInitialPhase;
    private long _gamepadRepeatStartedTimestamp;
    internal TimeProvider NavigationTimeProvider { get; set; } = TimeProvider.System;
    private long _navigationInputGeneration;
    private bool _keyboardScrollActive;

    private void OnGamepadDirectionStateChanged(GamepadService.GamepadDirection direction, bool isPressed)
    {
        var generation = Volatile.Read(ref _navigationInputGeneration);
        if (isPressed && _disposed == 0 && !_isLaunching && !_isCloseRequestInProgress)
            InvalidateSearchResultActivation();
        DispatchGamepadAction(() =>
        {
            if (_disposed != 0 || _isCloseRequestInProgress || generation != _navigationInputGeneration)
                return;
            if (_isLaunching)
            {
                if (!isPressed)
                    StopGamepadRepeat(direction);
                return;
            }

            if (InputContext == BigModeInputContext.Search)
            {
                if (isPressed) { var before = _navigationInputGeneration; Search!.Navigate(direction); if (IsSearchOpen && before == _navigationInputGeneration) StartGamepadRepeat(direction); }
                else StopGamepadRepeat(direction);
                return;
            }
            if (InputContext == BigModeInputContext.Alphabet)
            {
                if (isPressed)
                {
                    var before = _navigationInputGeneration;
                    NavigateAlphabet(direction);
                    if (InputContext == BigModeInputContext.Alphabet && before == _navigationInputGeneration)
                        StartGamepadRepeat(direction);
                }
                else StopGamepadRepeat(direction);
                return;
            }
            if (IsSystemMenuOpen)
            {
                if (isPressed)
                    NavigateSystemMenu(direction);
                else
                    StopGamepadRepeat(direction);
                return;
            }

            if (isPressed)
            {
                if (_gamepadRepeatDirection == direction && _gamepadRepeatTimer?.IsEnabled == true)
                    return;

                if (!IsAchievementsOverlayOpen)
                    SuspendPreviewForScroll();
                StartGamepadRepeat(direction);
                NavigateForDirection(direction, playSound: true);
                return;
            }

            StopGamepadRepeat(direction);
        });
    }

    private void OnGamepadSelect()
        => DispatchGamepadInput(() =>
        {
            if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
                return;

            ResetAttractIdleTimer();

            if (IsSystemMenuOpen)
            {
                ActivateSystemMenuSelection();
                return;
            }
            if (IsSearchOpen) { Search!.Confirm(); return; }
            if (IsAlphabetNavigationOpen) { ConfirmAlphabetNavigation(); return; }

            if (IsAchievementsOverlayOpen)
                return;

            PlaySound(_theme.Sounds.Confirm);
            _ = PlayCurrent();
        });

    private void OnGamepadBack()
        => DispatchGamepadInput(() =>
        {
            if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
                return;

            ResetAttractIdleTimer();

            if (HandleSystemMenuBack())
                return;
            if (IsSearchOpen) { Search!.Back(); return; }
            if (CloseAlphabetNavigation()) return;

            if (CloseAchievementsOverlay())
            {
                PlaySound(_theme.Sounds.Cancel);
                return;
            }

            PlaySound(_theme.Sounds.Cancel);
            _ = ExitBigMode();
        });

    private void OnGamepadDetails()
        => DispatchGamepadInput(() =>
        {
            if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
                return;

            if (IsSearchOpen) { Search!.Details(); return; }
            if (IsSystemMenuOpen || IsAlphabetNavigationOpen)
                return;

            ResetAttractIdleTimer();
            ToggleAchievementsOverlay();
        });

    private void OnGamepadHome()
        => DispatchGamepadInput(() =>
        {
            if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
                return;

            if (IsSearchOpen) { HandleSearchAction(BigModeKeyboardAction.Home); return; }
            if (IsSystemMenuOpen || IsAlphabetNavigationOpen)
                return;

            _ = ToggleHomeAsync();
        });

    /// <summary>
    /// Immediately exits BigMode regardless of the current navigation depth.
    /// Intended for keyboard ESC (quick way back to desktop UI),
    /// while gamepad back keeps its step-by-step behavior.
    /// </summary>
    [RelayCommand]
    private Task HardExitBigMode() => CloseSearch() || CloseAlphabetNavigation() ? Task.CompletedTask : RequestBigModeCloseAsync();

    private async Task RequestBigModeCloseAsync()
    {
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
            return;

        var requestClose = RequestClose;
        if (requestClose == null)
            return;

        _isCloseRequestInProgress = true;
        CloseSearch(resumePreview: false);
        CloseAlphabetNavigation(resumePreview: false);
        EndNavigationScroll(resumePreview: false);

        try
        {
            await requestClose();
        }
        finally
        {
            // On successful teardown this instance is no longer reachable. If the
            // owner could not close it, allow a later attempt instead of stranding it.
            _isCloseRequestInProgress = false;
            if (_disposed == 0)
                TriggerPreviewPlaybackWithDebounce();
        }
    }
    
    /// <summary>
    /// Polling and lifecycle callbacks share the same UI queue, including releases raised on the UI thread.
    /// </summary>
    internal static void DispatchGamepadAction(Action action, Dispatcher? dispatcher = null)
    {
        // Never run a UI-thread lifecycle release inline ahead of a previously queued SDL press.
        (dispatcher ?? Dispatcher.UIThread).Post(action, DispatcherPriority.Input);
    }

    private void DispatchGamepadInput(Action action)
    {
        var generation = Volatile.Read(ref _navigationInputGeneration);
        // Input may arrive while a Home continuation has higher dispatcher priority.
        // Invalidate at receipt; the UI generation below still rejects stale actions.
        if (_disposed == 0 && !_isLaunching && !_isCloseRequestInProgress)
            InvalidateSearchResultActivation();
        DispatchGamepadAction(() =>
        {
            if (_disposed == 0 && !_isLaunching && !_isCloseRequestInProgress && generation == _navigationInputGeneration) action();
        });
    }

    internal void DispatchControllerExit(Action onHandled) => DispatchGamepadInput(() =>
    {
        onHandled();
        _ = HardExitBigMode();
    });

    private void StartGamepadRepeat(GamepadService.GamepadDirection direction)
        => StartGamepadRepeat(direction, pageDirection: 0);

    private void StartGamepadRepeat(GamepadService.GamepadDirection? direction, int pageDirection)
    {
        EnsureGamepadRepeatTimer();
        // Restart even when the new owner arrives during the same 350 ms phase.
        _gamepadRepeatTimer!.Stop();

        _gamepadRepeatDirection = direction;
        _gamepadRepeatPageDirection = pageDirection;
        ResetGamepadAcceleration();
        _gamepadRepeatInitialPhase = true;
        _gamepadRepeatTimer.Interval = BigModeScrollProfile.InitialDelay;
        _gamepadRepeatTimer.IsEnabled = true;
    }

    private void ResetGamepadAcceleration()
        => _gamepadRepeatStartedTimestamp = _gamepadRepeatDirection != null || _gamepadRepeatPageDirection != 0
            ? (NavigationTimeProvider ?? TimeProvider.System).GetTimestamp() : 0;

    private void EnsureGamepadRepeatTimer()
    {
        if (_gamepadRepeatTimer != null)
            return;

        _gamepadRepeatTimer = new DispatcherTimer
        {
            IsEnabled = false
        };

        _gamepadRepeatTimer.Tick += OnGamepadRepeatTimerTick;
    }

    private void OnGamepadRepeatTimerTick(object? sender, EventArgs e)
    {
        // A queued tick after release must not consume an independent keyboard hold.
        if (_gamepadRepeatDirection == null && _gamepadRepeatPageDirection == 0) return;
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress || IsSystemMenuOpen)
        {
            EndNavigationScroll(resumePreview: false);
            return;
        }

        if (_gamepadRepeatInitialPhase)
        {
            _gamepadRepeatInitialPhase = false;
            _gamepadRepeatTimer!.Interval = BigModeScrollProfile.RepeatInterval;
        }

        if (IsSearchOpen)
        {
            if (_gamepadRepeatPageDirection < 0) Search!.Backspace();
            else if (_gamepadRepeatDirection is { } searchDirection) Search!.Navigate(searchDirection);
            return;
        }
        if (IsAlphabetNavigationOpen)
        {
            if (_gamepadRepeatDirection is { } alphabetDirection) NavigateAlphabet(alphabetDirection, playSound: false);
            return;
        }
        if (IsHomeActive || IsAchievementsOverlayOpen)
        {
            if (_gamepadRepeatPageDirection != 0)
            {
                EndNavigationScroll(resumePreview: true);
                return;
            }
            if (_gamepadRepeatDirection is { } direction)
                NavigateForDirection(direction, playSound: false);
            return;
        }

        FlushNavigationChanges();
        // Refresh can remove the opened node and end this hold at a context boundary.
        if (_gamepadRepeatDirection == null && _gamepadRepeatPageDirection == 0) return;
        ResetAttractIdleTimer();
        var clock = NavigationTimeProvider ?? TimeProvider.System;
        var step = BigModeScrollProfile.GetStep(clock.GetElapsedTime(_gamepadRepeatStartedTimestamp),
            _navigation.Current.Entries.Count, page: _gamepadRepeatPageDirection != 0);
        var sign = _gamepadRepeatPageDirection != 0 ? _gamepadRepeatPageDirection :
            _gamepadRepeatDirection is GamepadService.GamepadDirection.Up or GamepadService.GamepadDirection.Left ? -1 : 1;
        var count = _navigation.Current.Entries.Count;
        if (count == 0) return;
        var current = _navigation.Current.SelectedIndex;
        // Repeats reach and remain at the edge. Only a fresh press may wrap.
        var target = (int)Math.Clamp((long)current + sign * step, 0, count - 1);
        if (target != current) _navigation.TrySelect(target);
    }

    private void StopGamepadRepeat(GamepadService.GamepadDirection direction)
    {
        if (_gamepadRepeatDirection != direction)
            return;

        StopGamepadRepeatTimer();
        if (!IsAchievementsOverlayOpen)
            ResumePreviewAfterScroll();
    }

    private void StopGamepadRepeatTimer()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(StopGamepadRepeatTimer, DispatcherPriority.Background);
            return;
        }

        _gamepadRepeatDirection = null;
        _gamepadRepeatPageDirection = 0;
        _gamepadRepeatInitialPhase = false;
        _gamepadRepeatStartedTimestamp = 0;

        if (_gamepadRepeatTimer != null)
            _gamepadRepeatTimer.IsEnabled = false;
    }

    private void DisposeGamepadRepeatTimer()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(DisposeGamepadRepeatTimer, DispatcherPriority.Background);
            return;
        }

        StopGamepadRepeatTimer();

        if (_gamepadRepeatTimer != null)
        {
            _gamepadRepeatTimer.Tick -= OnGamepadRepeatTimerTick;
            _gamepadRepeatTimer.Stop();
            _gamepadRepeatTimer.IsEnabled = false;
            _gamepadRepeatTimer = null;
        }
    }

    private void SuspendPreviewForScroll()
    {
        if (_suspendPreviewDuringScroll)
            return;

        _suspendPreviewDuringScroll = true;
        CancelPreviewDebounce();
        PausePreviewForScroll();
    }

    private void ResumePreviewAfterScroll()
    {
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress || IsAchievementsOverlayOpen || IsSearchOpen ||
            !_suspendPreviewDuringScroll || _gamepadRepeatDirection != null || _gamepadRepeatPageDirection != 0 || _keyboardScrollActive)
            return;

        _suspendPreviewDuringScroll = false;
        _selectionVideoResolved = false;

        if (TryResumePausedPreviewForCurrentSelection())
            return;

        TriggerPreviewPlaybackWithDebounce();
    }

    public void NotifyKeyboardScrollStart()
    {
        _keyboardScrollActive = true;
        SuspendPreviewForScroll();
    }

    public void NotifyKeyboardScrollEnd()
    {
        _keyboardScrollActive = false;
        ResumePreviewAfterScroll();
    }

    // Context changes consume the current hold. A queued old press/release must
    // not restart it in Home, an overlay, a returned game or a closing instance.
    private void EndNavigationScroll(bool resumePreview)
    {
        InvalidateSearchResultActivation();
        Interlocked.Increment(ref _navigationInputGeneration);
        _gamepadService?.DiscardPendingUiInput();
        _blockedKeyboardKeys.UnionWith(_pressedKeyboardKeys);
        StopGamepadRepeatTimer();
        CancelCircularLogoPreloads();
        _keyboardScrollKeys?.Clear();
        _keyboardScrollActive = false;
        if (resumePreview && _disposed == 0 && !_isLaunching && !_isCloseRequestInProgress && !IsAchievementsOverlayOpen)
            ResumePreviewAfterScroll();
        else
        {
            _suspendPreviewDuringScroll = false;
            ClearPausedPreviewState();
        }
    }

    public void NotifyNavigationInputLost() => EndNavigationScroll(resumePreview: true);

    private void NavigateForDirection(GamepadService.GamepadDirection direction, bool playSound)
    {
        ResetAttractIdleTimer();

        if (playSound)
            PlaySound(_theme.Sounds.Navigate);

        if (IsAchievementsOverlayOpen)
        {
            NavigateAchievementsOverlay(direction);
            return;
        }

        if (IsHomeActive)
        {
            NavigateHome(direction);
            return;
        }

        switch (direction)
        {
            case GamepadService.GamepadDirection.Up:
            case GamepadService.GamepadDirection.Left:
                SelectPrevious();
                break;
            case GamepadService.GamepadDirection.Down:
            case GamepadService.GamepadDirection.Right:
                SelectNext();
                break;
        }
    }

    private void PlaySound(string? relativeSoundPath)
    {
        if (string.IsNullOrWhiteSpace(relativeSoundPath))
            return;

        if (ThemePathResolver.TryResolveThemeAssetPath(
                _theme.BasePath,
                relativeSoundPath,
                out var fullPath))
        {
            _soundEffectService.PlaySound(fullPath);
        }
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (InputContext != BigModeInputContext.Library) return;
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress) return;

        InvalidateSearchResultActivation();
        ResetAttractIdleTimer();
        
        FlushNavigationChanges();
        _navigation.MoveSelection(-1);
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (InputContext != BigModeInputContext.Library) return;
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress) return;

        InvalidateSearchResultActivation();
        ResetAttractIdleTimer();
        
        FlushNavigationChanges();
        _navigation.MoveSelection(1);
    }

    [RelayCommand]
    private async Task PlayCurrent()
    {
        if (InputContext is BigModeInputContext.Search or BigModeInputContext.Alphabet or BigModeInputContext.SystemMenu or BigModeInputContext.Achievements) return;
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress) return;

        ResetAttractIdleTimer();
        var selectedKey = SelectedEntry?.Key;
        var homeSelection = SelectedHomeEntry;
        FlushNavigationChanges();
        // A refresh may replace a removed/protected selection. The same press
        // must not activate that replacement merely because it inherited the index.
        if (IsHomeActive)
        {
            if (homeSelection?.Kind != SelectedHomeEntry?.Kind ||
                !ReferenceEquals(homeSelection?.Item, SelectedHomeEntry?.Item) ||
                !ReferenceEquals(homeSelection?.Node, SelectedHomeEntry?.Node)) return;
        }
        else if (selectedKey != SelectedEntry?.Key) return;

        if (IsHomeActive)
        {
            var homeEntry = SelectedHomeEntry;
            if (homeEntry == null)
                return;

            if (NavigateHomeLibrary(homeEntry))
                return;

            if (homeEntry.Kind == BigModeHomeEntryKind.LibraryCurrent && homeEntry.Node == null)
                return;

            if (homeEntry.Node != null)
            {
                var node = homeEntry.Node;
                await ToggleHomeAsync();
                await OpenHomeLibraryNodeAsync(node);
                return;
            }

            if (homeEntry?.Item == null)
                return;
        }
        
        MediaItem? itemToPlay;
        if (IsHomeActive)
        {
            itemToPlay = SelectedHomeEntry?.Item;
            if (itemToPlay == null || !BigModeVisibilityPolicy.IsItemVisible(itemToPlay, _parentalFilterActive)) return;
        }
        else
        {
            FlushNavigationChanges();
            if (_navigation.ActivateSelected() is not BigModeNavigationResult.MediaActivationRequested media) return;
            itemToPlay = media.Entry.Item;
        }

        EndNavigationScroll(resumePreview: false);
        _isLaunching = true;

        try
        {
            StopVideo();
            StopSecondaryBackgroundVideo();

            if (RequestPlay != null)
                await RequestPlay(itemToPlay);
        }
        finally
        {
            _isLaunching = false;
            ResetAttractIdleTimer();

            // Resume preview once the game returns (or if launch failed).
            TriggerPreviewPlaybackWithDebounce();
            EnsureSecondaryBackgroundPlayingIfReady();
        }
    }

    [RelayCommand]
    private async Task ExitBigMode()
    {
        if (IsSearchOpen) { Search!.Back(); return; }
        if (_disposed != 0 || _isLaunching || _isCloseRequestInProgress)
            return;

        ResetAttractIdleTimer();

        if (IsHomeActive)
        {
            await RequestBigModeCloseAsync();
            return;
        }
        
        FlushNavigationChanges();
        if (_navigation.Back() is BigModeNavigationResult.ExitRequested)
            await RequestBigModeCloseAsync();
    }
}
