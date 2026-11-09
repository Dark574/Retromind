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
    private int _attractAnimationGeneration;
    private bool _isAttractAnimating;
    private bool _applyingAttractSelection;

    // Minimum delay between visual attract-mode steps so that users can
    // actually see the list "spinning" instead of jumping instantly.
    private static readonly TimeSpan AttractSpinStepDelay = TimeSpan.FromMilliseconds(120);
    
    /// <summary>
    /// True while the attract-mode animation is actively spinning through the list.
    /// Themes can bind to this to trigger temporary visual effects.
    /// </summary>
    [ObservableProperty]
    private bool _isInAttractMode;

    private void StopAttractModeTimer()
    {
        Interlocked.Increment(ref _attractAnimationGeneration);

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

        if (_isLaunching || IsSystemMenuOpen || IsAchievementsOverlayOpen)
            return;

        // Mixed frames can begin on a node; attract only visits their direct visible media.
        if (IsHomeActive || _mediaEntryPositions.Count == 0)
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

        _ = PerformAttractModeStepAnimatedAsync();
    }

    /// <summary>
    /// Performs one attract-mode "spin" animation and ends on a random item.
    /// Best-effort: must never crash the UI.
    /// </summary>
    private async Task PerformAttractModeStepAnimatedAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;

        if (IsHomeActive || _mediaEntryPositions.Count == 0)
            return;

        var count = _mediaEntryPositions.Count;
        var entries = _navigation.Current.Entries;
        var libraryVersion = _navigation.LibraryVersion;
        if (count == 0)
            return;

        if (_isAttractAnimating)
            return;

        _isAttractAnimating = true;
        var generation = Interlocked.Increment(ref _attractAnimationGeneration);

        bool IsCurrent() => generation == Volatile.Read(ref _attractAnimationGeneration) &&
            Volatile.Read(ref _disposed) == 0 && !IsHomeActive && !_isLaunching &&
            ReferenceEquals(entries, _navigation.Current.Entries) && libraryVersion == _navigation.LibraryVersion;
        try
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 1 ||
                    generation != Volatile.Read(ref _attractAnimationGeneration))
                    return;

                IsInAttractMode = true;
                
                // Stop any currently playing preview video before starting
                // the attract-mode spin, so that artwork (marquee/logo) and
                // the on-screen content do not show mismatched titles.
                StopVideo();
            }, DispatcherPriority.Background);

            if (generation != Volatile.Read(ref _attractAnimationGeneration))
                return;

            // Optional attract-mode sound (best effort).
            if (!string.IsNullOrWhiteSpace(_theme.AttractModeSoundPath))
            {
                try
                {
                    if (ThemePathResolver.TryResolveThemeAssetPath(
                            _theme.BasePath,
                            _theme.AttractModeSoundPath,
                            out var fullPath))
                    {
                        _soundEffectService.PlaySound(fullPath);
                    }
                }
                catch
                {
                    // best effort
                }
            }

            // Quick spin steps (slightly randomized).
            var minSteps = Math.Min(15, count);
            var maxSteps = Math.Min(25, count * 3);
            var steps = RandomHelper.Next(minSteps, maxSteps + 1);

            for (int i = 0; i < steps; i++)
            {
                if (Volatile.Read(ref _disposed) == 1 ||
                    generation != Volatile.Read(ref _attractAnimationGeneration))
                    break;

                if (!IsCurrent()) break;
                var mediaIndex = SelectedItemIndex;
                var nextIndex = mediaIndex < 0 ? 0 : (mediaIndex + 1) % count;
                SelectAttractMedia(nextIndex);

                // Keep the whole attract-mode spin on the UI thread so that
                // bindings (logo highlight, marquee, bezel) stay consistent.
                // The delay is large enough so users can actually see the scroll.
                await Task.Delay(AttractSpinStepDelay);
            }

            // Finally jump to a random title.
            if (IsCurrent())
            {
                var newIndex = RandomHelper.Next(0, count);
                if (count > 1 && newIndex == SelectedItemIndex) newIndex = (newIndex + 1) % count;
                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (IsCurrent()) SelectAttractMedia(newIndex);
                }, DispatcherPriority.Background);
            }
        }
        catch
        {
            // Attract mode must never crash the UI.
        }
        finally
        {
            try
            {
                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (Volatile.Read(ref _disposed) == 1 ||
                        generation != Volatile.Read(ref _attractAnimationGeneration))
                        return;

                    IsInAttractMode = false;
                }, DispatcherPriority.Background);
            }
            catch
            {
                // Attract mode cleanup is best effort.
            }

            _isAttractAnimating = false;
        }
    }

    private void SelectAttractMedia(int mediaIndex)
    {
        _applyingAttractSelection = true;
        try { _navigation.TrySelect(_mediaEntryPositions[mediaIndex]); }
        finally { _applyingAttractSelection = false; }
    }

    /// <summary>
    /// Resets the idle timer. Call this on any user input.
    /// </summary>
    private void ResetAttractIdleTimer()
    {
        Interlocked.Increment(ref _attractAnimationGeneration);
        _lastUserInputUtc = DateTime.UtcNow;
        _attractStepsExecuted = 0;

        // A real input must take ownership immediately. The running spin observes
        // the generation change before its next step and skips its final random pick.
        IsInAttractMode = false;
    }
}
