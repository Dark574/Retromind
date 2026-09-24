using System;
using Avalonia.Threading;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private static readonly TimeSpan RetroAchievementsSelectionDelay =
        TimeSpan.FromMilliseconds(450);

    private DispatcherTimer? _retroAchievementsSelectionTimer;
    private MediaItem? _pendingRetroAchievementsItem;

    /// <summary>
    /// Selection-driven RetroAchievements state exposed to BigMode themes.
    /// </summary>
    public RetroAchievementsProgressViewModel RetroAchievementsProgress { get; }

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
        if (!value)
            return;

        CancelPendingRetroAchievementsSelection();
        _ = RetroAchievementsProgress.SelectItemAsync(null);
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
