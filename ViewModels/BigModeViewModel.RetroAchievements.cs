using System;
using System.ComponentModel;
using System.Linq;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private const int AchievementColumnCount = 6;

    private static readonly TimeSpan RetroAchievementsSelectionDelay =
        TimeSpan.FromMilliseconds(450);

    private DispatcherTimer? _retroAchievementsSelectionTimer;
    private MediaItem? _pendingRetroAchievementsItem;

    [ObservableProperty]
    private bool _isAchievementsOverlayOpen;

    [ObservableProperty]
    private RetroAchievementsAchievementItemViewModel? _selectedAchievement;

    /// <summary>
    /// Selection-driven RetroAchievements state exposed to BigMode themes.
    /// </summary>
    public RetroAchievementsProgressViewModel RetroAchievementsProgress { get; }

    public bool CanOpenAchievementsOverlay =>
        !IsInAttractMode &&
        RetroAchievementsProgress.IsVisible &&
        RetroAchievementsProgress.HasAchievements;

    public string AchievementsOverlayTitle => T(
        "BigMode_AchievementsOverlayTitle",
        "RetroAchievements");

    public string AchievementsOverlayHintText => string.Format(
        T("BigMode_AchievementsOverlayHint", "{0} / {1} · Achievements"),
        ControllerButtonDisplayHelper.Format(_settings.ControllerBindings.Details),
        KeyboardBindingHelper.Format(_settings.KeyboardBindings.Details, Key.I));

    public string AchievementsOverlayControlsText => string.Format(
        T(
            "BigMode_AchievementsOverlayControls",
            "D-pad · Select achievement    {0} / {1} / {2} · Close"),
        ControllerButtonDisplayHelper.Format(_settings.ControllerBindings.Back),
        KeyboardBindingHelper.Format(_settings.KeyboardBindings.Back, Key.Back),
        KeyboardBindingHelper.Format(_settings.KeyboardBindings.ExitBigMode, Key.Escape));

    private void InitializeRetroAchievementsOverlay()
    {
        RetroAchievementsProgress.PropertyChanged += OnRetroAchievementsProgressPropertyChanged;
    }

    private void ScheduleRetroAchievementsProgress(MediaItem? item)
    {
        CancelPendingRetroAchievementsSelection();

        // Clear stale progress immediately so a theme never shows the previous
        // game's achievement state while the new selection is settling.
        _ = RetroAchievementsProgress.SelectItemAsync(null);

        if (_settings.RetroAchievements?.ShowInBigMode != true ||
            !IsGameListActive ||
            IsInAttractMode ||
            item?.RetroAchievementsGame?.GameId is not > 0)
            return;

        _pendingRetroAchievementsItem = item;
        _retroAchievementsSelectionTimer ??= CreateRetroAchievementsSelectionTimer();
        _retroAchievementsSelectionTimer.Interval = RetroAchievementsSelectionDelay;
        _retroAchievementsSelectionTimer.Start();
    }

    private DispatcherTimer CreateRetroAchievementsSelectionTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += OnRetroAchievementsSelectionTimerTick;
        return timer;
    }

    private void OnRetroAchievementsSelectionTimerTick(object? sender, EventArgs e)
    {
        _retroAchievementsSelectionTimer?.Stop();

        var item = _pendingRetroAchievementsItem;
        _pendingRetroAchievementsItem = null;
        if (item == null ||
            !IsGameListActive ||
            IsInAttractMode ||
            !ReferenceEquals(SelectedItem, item))
        {
            return;
        }

        _ = RetroAchievementsProgress.SelectItemAsync(item);
    }

    partial void OnIsInAttractModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOpenAchievementsOverlay));

        if (!value)
            return;

        CloseAchievementsOverlay();
        CancelPendingRetroAchievementsSelection();
        _ = RetroAchievementsProgress.SelectItemAsync(null);
    }

    public void ToggleAchievementsOverlay()
    {
        if (IsAchievementsOverlayOpen)
        {
            CloseAchievementsOverlay();
            return;
        }

        OpenAchievementsOverlay();
    }

    public void OpenAchievementsOverlay()
    {
        if (!CanOpenAchievementsOverlay)
            return;

        ResetAttractIdleTimer();
        StopGamepadRepeatTimer();
        CancelPreviewDebounce();
        StopVideo();

        SelectedAchievement = RetroAchievementsProgress.AchievementItems.FirstOrDefault();
        IsAchievementsOverlayOpen = true;
        RetroAchievementsProgress.EnsureAchievementBadgesLoaded();
    }

    public bool CloseAchievementsOverlay()
    {
        if (!IsAchievementsOverlayOpen)
            return false;

        IsAchievementsOverlayOpen = false;
        SelectedAchievement = null;
        StopGamepadRepeatTimer();
        if (!IsInAttractMode)
            TriggerPreviewPlaybackWithDebounce();
        return true;
    }

    public void NavigateAchievementsOverlay(GamepadService.GamepadDirection direction)
    {
        if (!IsAchievementsOverlayOpen)
            return;

        var achievements = RetroAchievementsProgress.AchievementItems;
        if (achievements.Count == 0)
            return;

        var currentIndex = -1;
        if (SelectedAchievement != null)
        {
            for (var index = 0; index < achievements.Count; index++)
            {
                if (!ReferenceEquals(achievements[index], SelectedAchievement))
                    continue;

                currentIndex = index;
                break;
            }
        }
        if (currentIndex < 0)
            currentIndex = 0;

        var nextIndex = direction switch
        {
            GamepadService.GamepadDirection.Left =>
                (currentIndex - 1 + achievements.Count) % achievements.Count,
            GamepadService.GamepadDirection.Right =>
                (currentIndex + 1) % achievements.Count,
            GamepadService.GamepadDirection.Up => Math.Max(0, currentIndex - AchievementColumnCount),
            GamepadService.GamepadDirection.Down => Math.Min(achievements.Count - 1, currentIndex + AchievementColumnCount),
            _ => currentIndex
        };

        SelectedAchievement = achievements[nextIndex];
    }

    private void OnRetroAchievementsProgressPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RetroAchievementsProgressViewModel.IsVisible) or
            nameof(RetroAchievementsProgressViewModel.HasAchievements) or
            nameof(RetroAchievementsProgressViewModel.AchievementItems)))
        {
            return;
        }

        OnPropertyChanged(nameof(CanOpenAchievementsOverlay));

        if (!CanOpenAchievementsOverlay)
        {
            CloseAchievementsOverlay();
            return;
        }

        if (IsAchievementsOverlayOpen &&
            (SelectedAchievement == null ||
             !RetroAchievementsProgress.AchievementItems.Contains(SelectedAchievement)))
        {
            SelectedAchievement = RetroAchievementsProgress.AchievementItems.FirstOrDefault();
        }
    }

    public void RefreshRetroAchievementsAfterTrackedSession(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!IsGameListActive ||
            _settings.RetroAchievements?.ShowInBigMode != true ||
            IsInAttractMode ||
            !ReferenceEquals(SelectedItem, item) ||
            item.RetroAchievementsGame?.GameId is not > 0)
        {
            return;
        }

        CancelPendingRetroAchievementsSelection();
        _ = RetroAchievementsProgress.SelectItemAsync(item, forceRefresh: true);
    }

    private void CancelPendingRetroAchievementsSelection()
    {
        _pendingRetroAchievementsItem = null;
        _retroAchievementsSelectionTimer?.Stop();
    }

    private void DisposeRetroAchievementsProgress()
    {
        IsAchievementsOverlayOpen = false;
        SelectedAchievement = null;
        RetroAchievementsProgress.PropertyChanged -= OnRetroAchievementsProgressPropertyChanged;

        if (_retroAchievementsSelectionTimer != null)
        {
            _retroAchievementsSelectionTimer.Stop();
            _retroAchievementsSelectionTimer.Tick -= OnRetroAchievementsSelectionTimerTick;
            _retroAchievementsSelectionTimer = null;
        }

        _pendingRetroAchievementsItem = null;
        RetroAchievementsProgress.Dispose();
    }
}
