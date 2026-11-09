using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    private static readonly TimeSpan GamepadRepeatInitialDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan GamepadRepeatInterval = TimeSpan.FromMilliseconds(80);

    private DispatcherTimer? _gamepadRepeatTimer;
    private GamepadService.GamepadDirection? _gamepadRepeatDirection;
    private bool _gamepadRepeatInitialPhase;

    private void OnGamepadDirectionStateChanged(GamepadService.GamepadDirection direction, bool isPressed)
        => DispatchGamepadAction(() =>
        {
            if (_isLaunching)
            {
                if (!isPressed)
                    StopGamepadRepeat(direction);
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
                NavigateForDirection(direction, playSound: true);
                StartGamepadRepeat(direction);
                return;
            }

            StopGamepadRepeat(direction);
        });

    private void OnGamepadSelect()
        => DispatchGamepadAction(() =>
        {
            if (_isLaunching)
                return;

            ResetAttractIdleTimer();

            if (IsSystemMenuOpen)
            {
                ActivateSystemMenuSelection();
                return;
            }

            if (IsAchievementsOverlayOpen)
                return;

            PlaySound(_theme.Sounds.Confirm);
            _ = PlayCurrent();
        });

    private void OnGamepadBack()
        => DispatchGamepadAction(() =>
        {
            if (_isLaunching)
                return;

            ResetAttractIdleTimer();

            if (HandleSystemMenuBack())
                return;

            if (CloseAchievementsOverlay())
            {
                PlaySound(_theme.Sounds.Cancel);
                return;
            }

            PlaySound(_theme.Sounds.Cancel);
            _ = ExitBigMode();
        });

    private void OnGamepadDetails()
        => DispatchGamepadAction(() =>
        {
            if (_isLaunching)
                return;

            if (IsSystemMenuOpen)
                return;

            ResetAttractIdleTimer();
            ToggleAchievementsOverlay();
        });

    private void OnGamepadHome()
        => DispatchGamepadAction(() =>
        {
            if (_isLaunching)
                return;

            if (IsSystemMenuOpen)
                return;

            _ = ToggleHomeAsync();
        });

    /// <summary>
    /// Immediately exits BigMode regardless of the current navigation depth.
    /// Intended for keyboard ESC (quick way back to desktop UI),
    /// while gamepad back keeps its step-by-step behavior.
    /// </summary>
    [RelayCommand]
    private Task HardExitBigMode() => RequestBigModeCloseAsync();

    private async Task RequestBigModeCloseAsync()
    {
        if (_isLaunching || _isCloseRequestInProgress)
            return;

        var requestClose = RequestClose;
        if (requestClose == null)
            return;

        _isCloseRequestInProgress = true;
        StopGamepadRepeatTimer();

        try
        {
            await requestClose();
        }
        finally
        {
            // On successful teardown this instance is no longer reachable. If the
            // owner could not close it, allow a later attempt instead of stranding it.
            _isCloseRequestInProgress = false;
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

    private void StartGamepadRepeat(GamepadService.GamepadDirection direction)
    {
        EnsureGamepadRepeatTimer();

        _gamepadRepeatDirection = direction;
        _gamepadRepeatInitialPhase = true;
        _gamepadRepeatTimer!.Interval = GamepadRepeatInitialDelay;
        _gamepadRepeatTimer.IsEnabled = true;
    }

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
        if (_isLaunching || _gamepadRepeatDirection == null)
        {
            StopGamepadRepeatTimer();
            return;
        }

        if (_gamepadRepeatInitialPhase)
        {
            _gamepadRepeatInitialPhase = false;
            _gamepadRepeatTimer!.Interval = GamepadRepeatInterval;
        }

        NavigateForDirection(_gamepadRepeatDirection.Value, playSound: false);
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
        _gamepadRepeatInitialPhase = false;

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

        _gamepadRepeatDirection = null;
        _gamepadRepeatInitialPhase = false;

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
        PausePreviewForScroll();
    }

    private void ResumePreviewAfterScroll()
    {
        if (!_suspendPreviewDuringScroll)
            return;

        _suspendPreviewDuringScroll = false;

        if (TryResumePausedPreviewForCurrentSelection())
            return;

        TriggerPreviewPlaybackWithDebounce();
    }

    public void NotifyKeyboardScrollStart()
    {
        SuspendPreviewForScroll();
    }

    public void NotifyKeyboardScrollEnd()
    {
        ResumePreviewAfterScroll();
    }

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
        if (_isLaunching) return;

        ResetAttractIdleTimer();
        
        FlushNavigationChanges();
        _navigation.MoveSelection(-1);
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return;

        ResetAttractIdleTimer();
        
        FlushNavigationChanges();
        _navigation.MoveSelection(1);
    }

    [RelayCommand]
    private async Task PlayCurrent()
    {
        if (_isLaunching) return;

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

        StopGamepadRepeatTimer();
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
        if (_isLaunching)
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
