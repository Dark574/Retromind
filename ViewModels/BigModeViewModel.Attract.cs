using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Retromind.Helpers;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    // --- Attract mode (theme-driven idle navigation) ---

    private DispatcherTimer? _attractTimer;
    private DateTime _lastUserInputUtc;
    private int _attractStepsExecuted;
    private bool _isAttractAnimating;

    /// <summary>
    /// True while the attract-mode animation is actively spinning through the list.
    /// Themes can bind to this to trigger temporary visual effects.
    /// </summary>
    [ObservableProperty]
    private bool _isInAttractMode;

    private void StopAttractModeTimer()
    {
        try
        {
            if (_attractTimer != null)
            {
                _attractTimer.Tick -= OnAttractTimerTick;
                _attractTimer.Stop();
                _attractTimer.IsEnabled = false;
                _attractTimer = null;
            }
        }
        catch
        {
            // best effort
        }

        _isAttractAnimating = false;
        IsInAttractMode = false;
        _attractStepsExecuted = 0;
    }
    
    /// <summary>
    /// Starts the attract-mode timer if the active theme enables it.
    /// </summary>
    private void InitializeAttractModeTimer()
    {
        StopAttractModeTimer();
        
        if (!_theme.AttractModeEnabled ||
            _theme.AttractModeIdleInterval is null ||
            _theme.AttractModeIdleInterval.Value <= TimeSpan.Zero)
        {
            return;
        }

        _attractTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
            IsEnabled = true
        };

        _attractTimer.Tick += OnAttractTimerTick;
        _lastUserInputUtc = DateTime.UtcNow;
        _attractStepsExecuted = 0;
    }

    /// <summary>
    /// Timer tick handler (runs on the UI thread).
    /// </summary>
    private void OnAttractTimerTick(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        if (!_theme.AttractModeEnabled || _theme.AttractModeIdleInterval is null)
            return;

        if (_isLaunching)
            return;

        // Attract mode only makes sense while the game list is active.
        if (!IsGameListActive || Items is not { Count: > 0 })
        {
            _attractStepsExecuted = 0;
            return;
        }

        // Do not start another animation while one is running.
        if (_isAttractAnimating)
            return;

        var now = DateTime.UtcNow;
        var elapsed = now - _lastUserInputUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            _attractStepsExecuted = 0;
            return;
        }

        var interval = _theme.AttractModeIdleInterval.Value;
        if (interval <= TimeSpan.Zero)
            return;

        // How many intervals have elapsed since last input?
        var totalStepsShouldHave = (int)(elapsed.Ticks / interval.Ticks);
        if (totalStepsShouldHave <= _attractStepsExecuted)
            return;

        _attractStepsExecuted++;

        PerformAttractModeStepAnimated();
    }

    /// <summary>
    /// Performs one attract-mode "spin" animation and ends on a random item.
    /// Best-effort: must never crash the UI.
    /// </summary>
    private async void PerformAttractModeStepAnimated()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        if (!IsGameListActive || Items is not { Count: > 0 })
            return;

        var count = Items.Count;
        if (count == 0)
            return;

        if (_isAttractAnimating)
            return;

        _isAttractAnimating = true;

        try
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 1)
                    return;

                IsInAttractMode = true;
            }, DispatcherPriority.Background);

            // Optional attract-mode sound (best effort).
            if (!string.IsNullOrWhiteSpace(_theme.AttractModeSoundPath))
            {
                try
                {
                    var fullPath = System.IO.Path.Combine(_theme.BasePath, _theme.AttractModeSoundPath);
                    _soundEffectService.PlaySound(fullPath);
                }
                catch
                {
                    // best effort
                }
            }

            // Quick spin steps (slightly randomized).
            var minSteps = Math.Min(10, count);
            var maxSteps = Math.Min(25, count * 3);
            var steps = RandomHelper.Next(minSteps, maxSteps + 1);

            for (int i = 0; i < steps; i++)
            {
                if (Volatile.Read(ref _disposed) == 1)
                    break;

                if (!IsGameListActive || Items.Count == 0 || _isLaunching)
                    break;

                var nextIndex = SelectedItemIndex;
                if (nextIndex < 0 || nextIndex >= Items.Count)
                    nextIndex = 0;

                nextIndex = (nextIndex + 1) % Items.Count;
                SelectedItemIndex = nextIndex;
                SelectedItem = Items[nextIndex];

                await Task.Delay(60).ConfigureAwait(false);
            }

            // Finally jump to a random title.
            if (IsGameListActive && Items is { Count: > 0 } && !_isLaunching)
            {
                var currentIndex = SelectedItemIndex >= 0 && SelectedItemIndex < Items.Count
                    ? SelectedItemIndex
                    : -1;

                var newIndex = RandomHelper.Next(0, Items.Count);

                if (Items.Count > 1 && newIndex == currentIndex)
                    newIndex = (newIndex + 1) % Items.Count;

                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposed) == 1)
                        return;

                    if (!IsGameListActive || Items.Count == 0)
                        return;

                    SelectedItemIndex = newIndex;
                    SelectedItem = Items[newIndex];
                }, DispatcherPriority.Background);
            }
        }
        catch
        {
            // Attract mode must never crash the UI.
        }
        finally
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 1)
                    return;

                IsInAttractMode = false;
            }, DispatcherPriority.Background);

            _isAttractAnimating = false;
        }
    }

    /// <summary>
    /// Resets the idle timer. Call this on any user input.
    /// </summary>
    private void ResetAttractIdleTimer()
    {
        _lastUserInputUtc = DateTime.UtcNow;
        _attractStepsExecuted = 0;
    }
}