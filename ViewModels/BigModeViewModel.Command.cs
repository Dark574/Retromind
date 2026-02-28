using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
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
            if (isPressed)
            {
                if (_gamepadRepeatDirection == direction && _gamepadRepeatTimer?.IsEnabled == true)
                    return;

                SuspendPreviewForScroll();
                NavigateForDirection(direction, playSound: true);
                StartGamepadRepeat(direction);
                return;
            }

            StopGamepadRepeat(direction);
        });

    private void OnGamepadUp()
        => DispatchGamepadAction(() =>
        {
            ResetAttractIdleTimer();
            PlaySound(_theme.Sounds.Navigate);
            SelectPrevious();
        });

    private void OnGamepadDown()
        => DispatchGamepadAction(() =>
        {
            ResetAttractIdleTimer();
            PlaySound(_theme.Sounds.Navigate);
            SelectNext();
        });

    private void OnGamepadLeft()
        => DispatchGamepadAction(() =>
        {
            ResetAttractIdleTimer();
            PlaySound(_theme.Sounds.Navigate);
            // Default mapping: left behaves like "previous".
            SelectPrevious();
        });

    private void OnGamepadRight()
        => DispatchGamepadAction(() =>
        {
            ResetAttractIdleTimer();
            PlaySound(_theme.Sounds.Navigate);
            // Default mapping: right behaves like "next".
            SelectNext();
        });

    private void OnGamepadSelect()
        => DispatchGamepadAction(() =>
        {
            ResetAttractIdleTimer();
            PlaySound(_theme.Sounds.Confirm);
            _ = PlayCurrent();
        });

    private void OnGamepadBack()
        => DispatchGamepadAction(() =>
        {
            ResetAttractIdleTimer();
            PlaySound(_theme.Sounds.Cancel);
            ExitBigMode();
        });

    /// <summary>
    /// Immediately exits BigMode regardless of the current navigation depth.
    /// Intended for keyboard ESC (quick way back to desktop UI),
    /// while gamepad back keeps its step-by-step behavior.
    /// </summary>
    [RelayCommand]
    private void HardExitBigMode()
    {
        ThemeContextNode = null;
        RequestClose?.Invoke();
    }
    
    /// <summary>
    /// Gamepad callbacks arrive on the SDL thread.
    /// All UI-bound state changes must be marshaled to the UI thread.
    /// </summary>
    private static void DispatchGamepadAction(System.Action action)
    {
        if (action == null) return;

        // Gamepad callbacks arrive on the SDL thread.
        // All UI-bound state changes must be marshaled to the UI thread.
        UiThreadHelper.Post(action, DispatcherPriority.Input);
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
        if (_gamepadRepeatDirection == null)
        {
            if (_gamepadRepeatTimer != null)
                _gamepadRepeatTimer.IsEnabled = false;
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
        StopVideo();
    }

    private void ResumePreviewAfterScroll()
    {
        if (!_suspendPreviewDuringScroll)
            return;

        _suspendPreviewDuringScroll = false;
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

        // Theme.BasePath is the theme directory as provided by ThemeLoader.
        var fullPath = Path.Combine(_theme.BasePath, relativeSoundPath);
        _soundEffectService.PlaySound(fullPath);
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (_isLaunching) return;

        ResetAttractIdleTimer();
        
        if (IsGameListActive)
        {
            if (Items.Count == 0) return;

            if (SelectedItemIndex < 0)
                SelectedItemIndex = 0;

            // Wrap-around navigation.
            SelectedItemIndex = (SelectedItemIndex - 1 + Items.Count) % Items.Count;
            SelectedItem = Items[SelectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            if (SelectedCategoryIndex < 0)
                SelectedCategoryIndex = 0;

            // Wrap-around navigation.
            SelectedCategoryIndex = (SelectedCategoryIndex - 1 + CurrentCategories.Count) % CurrentCategories.Count;
            SelectedCategory = CurrentCategories[SelectedCategoryIndex];
        }

        // Defensive fallback (should rarely be needed).
        if (SelectedCategory == null && CurrentCategories.Any())
            SelectedCategory = CurrentCategories.First();
    }

    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return;

        ResetAttractIdleTimer();
        
        if (IsGameListActive)
        {
            if (Items.Count == 0) return;

            if (SelectedItemIndex < 0)
                SelectedItemIndex = 0;

            // Wrap-around navigation.
            SelectedItemIndex = (SelectedItemIndex + 1) % Items.Count;
            SelectedItem = Items[SelectedItemIndex];
        }
        else
        {
            if (CurrentCategories.Count == 0) return;

            if (SelectedCategoryIndex < 0)
                SelectedCategoryIndex = 0;

            // Wrap-around navigation.
            SelectedCategoryIndex = (SelectedCategoryIndex + 1) % CurrentCategories.Count;
            SelectedCategory = CurrentCategories[SelectedCategoryIndex];
        }

        // Defensive fallback (should rarely be needed).
        if (SelectedCategory == null && CurrentCategories.Any())
            SelectedCategory = CurrentCategories.First();
    }

    [RelayCommand]
    private async Task PlayCurrent()
    {
        if (_isLaunching) return;

        ResetAttractIdleTimer();
        
        // Category view: enter folder (children) or switch into game list (items).
        if (!IsGameListActive)
        {
            if (SelectedCategory == null) return;

            var node = SelectedCategory;

            if (node.Children is { Count: > 0 })
            {
                _navigationStack.Push(CurrentCategories);
                _titleStack.Push(CategoryTitle);
                _navigationPath.Push(node);

                CategoryTitle = node.Name;
                CurrentCategories = node.Children;

                // Theme context becomes the current node.
                ThemeContextNode = node;

                SelectedCategory = CurrentCategories.FirstOrDefault();

                TriggerPreviewPlaybackWithDebounce();
                return;
            }

            if (node.Items is { Count: > 0 })
            {
                _navigationStack.Push(CurrentCategories);
                _titleStack.Push(CategoryTitle);

                // Ensure leaf is part of the navigation path for persistence/restore.
                if (_navigationPath.Count == 0 || _navigationPath.Peek() != node)
                    _navigationPath.Push(node);

                CurrentNode = node;
                SelectedCategory = node;

                Items = node.Items;
                IsGameListActive = true;

                // Theme context is also the leaf node.
                ThemeContextNode = node;

                SelectedItem = Items.FirstOrDefault();

                TriggerPreviewPlaybackWithDebounce();
                return;
            }

            return;
        }

        // Game view: launch the selected item.
        if (SelectedItem == null) return;

        _isLaunching = true;

        try
        {
            StopVideo();

            if (RequestPlay != null)
                await RequestPlay(SelectedItem);
        }
        finally
        {
            _isLaunching = false;

            // Resume preview once the game returns (or if launch failed).
            TriggerPreviewPlaybackWithDebounce();
        }
    }

    [RelayCommand]
    private void ExitBigMode()
    {
        ResetAttractIdleTimer();
        
        // If we are currently in the game list, go back to category view first.
        if (IsGameListActive)
        {
            IsGameListActive = false;

            // Clear item list in category view.
            Items = new ObservableCollection<MediaItem>();
            SelectedItem = null;

            var previousList = _navigationStack.Count > 0 ? _navigationStack.Pop() : _rootNodes;
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : Strings.BigMode_MainMenu;

            CurrentCategories = previousList;
            CategoryTitle = previousTitle;

            if (_navigationPath.Count > 0) _navigationPath.Pop();

            // Restore selection to the leaf node if it exists in the current level.
            var leafNode = CurrentNode ?? (_navigationPath.Count > 0 ? _navigationPath.Peek() : null);
            SelectedCategory = leafNode != null && CurrentCategories.Contains(leafNode)
                ? leafNode
                : CurrentCategories.FirstOrDefault();

            // Theme context = current folder (peek) or selected root.
            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : SelectedCategory;

            TriggerPreviewPlaybackWithDebounce();

            _settings.LastBigModeSelectedNodeId = null;
            return;
        }

        // If we are already at root, exit BigMode completely.
        if (CurrentCategories == _rootNodes)
        {
            ThemeContextNode = null;
            RequestClose?.Invoke();
            return;
        }

        // Pop one navigation level.
        if (_navigationStack.Count > 0)
        {
            var previousList = _navigationStack.Pop();
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : Strings.BigMode_MainMenu;

            if (_navigationPath.Count > 0) _navigationPath.Pop();

            CategoryTitle = previousTitle;
            CurrentCategories = previousList;

            SelectedCategory = CurrentCategories.FirstOrDefault();

            ThemeContextNode = _navigationPath.Count > 0 ? _navigationPath.Peek() : SelectedCategory;

            TriggerPreviewPlaybackWithDebounce();
            return;
        }

        ThemeContextNode = null;
        RequestClose?.Invoke();
    }
}
