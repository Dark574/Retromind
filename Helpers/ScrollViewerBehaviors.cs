using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Behaviors for ScrollViewer controls used by themes.
/// </summary>
public static class ScrollViewerBehaviors
{
    private sealed class AutoVerticalScrollState : IDisposable
    {
        private readonly ScrollViewer _owner;
        private readonly DispatcherTimer _timer;
        private DateTime _lastTickUtc;
        private double _currentOffsetY;
        private double _waitRemainingMs;
        private bool _isPausedAtEnd;
        private bool _isDisposed;

        public AutoVerticalScrollState(ScrollViewer owner)
        {
            _owner = owner;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33),
                IsEnabled = false
            };
            _timer.Tick += OnTick;
            _owner.AttachedToVisualTree += OnAttachedToVisualTree;
            _owner.DetachedFromVisualTree += OnDetachedFromVisualTree;
            Reset(withStartDelay: true);
            _lastTickUtc = DateTime.UtcNow;
            _timer.IsEnabled = true;
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_isDisposed)
                return;

            _lastTickUtc = DateTime.UtcNow;
            _timer.IsEnabled = true;
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_isDisposed)
                return;

            _timer.IsEnabled = false;
        }

        public void Reset(bool withStartDelay)
        {
            _currentOffsetY = 0;
            _isPausedAtEnd = false;
            _waitRemainingMs = withStartDelay ? Math.Max(0, GetAutoVerticalScrollStartDelayMs(_owner)) : 0;
            SetVerticalOffset(0);
            _lastTickUtc = DateTime.UtcNow;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_isDisposed)
                return;

            if (!_owner.IsVisible)
            {
                _lastTickUtc = DateTime.UtcNow;
                return;
            }

            var now = DateTime.UtcNow;
            var dtSeconds = (now - _lastTickUtc).TotalSeconds;
            _lastTickUtc = now;
            if (dtSeconds <= 0)
                dtSeconds = 0.033;
            if (dtSeconds > 0.25)
                dtSeconds = 0.25;

            var overflow = Math.Max(0, _owner.Extent.Height - _owner.Viewport.Height);
            if (overflow <= 0.5)
            {
                if (_currentOffsetY > 0.5 || _owner.Offset.Y > 0.5)
                    SetVerticalOffset(0);

                _currentOffsetY = 0;
                _isPausedAtEnd = false;
                _waitRemainingMs = Math.Max(0, GetAutoVerticalScrollStartDelayMs(_owner));
                return;
            }

            if (_waitRemainingMs > 0)
            {
                _waitRemainingMs -= dtSeconds * 1000.0;
                if (_waitRemainingMs > 0)
                    return;

                _waitRemainingMs = 0;

                if (_isPausedAtEnd)
                {
                    _isPausedAtEnd = false;
                    _currentOffsetY = 0;
                    SetVerticalOffset(0);
                    _waitRemainingMs = Math.Max(0, GetAutoVerticalScrollStartDelayMs(_owner));
                    return;
                }
            }

            var pxPerSecond = Math.Max(0.0, GetAutoVerticalScrollSpeed(_owner));
            if (pxPerSecond <= 0.001)
                return;

            _currentOffsetY += pxPerSecond * dtSeconds;
            if (_currentOffsetY >= overflow)
            {
                _currentOffsetY = overflow;
                SetVerticalOffset(_currentOffsetY);
                _isPausedAtEnd = true;
                _waitRemainingMs = Math.Max(0, GetAutoVerticalScrollEndPauseMs(_owner));
                return;
            }

            SetVerticalOffset(_currentOffsetY);
        }

        private void SetVerticalOffset(double y)
        {
            var current = _owner.Offset;
            _owner.Offset = new Vector(current.X, Math.Max(0, y));
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer.IsEnabled = false;
            _owner.AttachedToVisualTree -= OnAttachedToVisualTree;
            _owner.DetachedFromVisualTree -= OnDetachedFromVisualTree;
        }
    }

    public static readonly AttachedProperty<bool> AutoVerticalScrollProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>(
            "AutoVerticalScroll",
            typeof(ScrollViewerBehaviors),
            defaultValue: false);

    public static bool GetAutoVerticalScroll(AvaloniaObject element) =>
        element.GetValue(AutoVerticalScrollProperty);

    public static void SetAutoVerticalScroll(AvaloniaObject element, bool value) =>
        element.SetValue(AutoVerticalScrollProperty, value);

    public static readonly AttachedProperty<double> AutoVerticalScrollSpeedProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, double>(
            "AutoVerticalScrollSpeed",
            typeof(ScrollViewerBehaviors),
            defaultValue: 14.0);

    public static double GetAutoVerticalScrollSpeed(AvaloniaObject element) =>
        element.GetValue(AutoVerticalScrollSpeedProperty);

    public static void SetAutoVerticalScrollSpeed(AvaloniaObject element, double value) =>
        element.SetValue(AutoVerticalScrollSpeedProperty, value);

    public static readonly AttachedProperty<int> AutoVerticalScrollStartDelayMsProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, int>(
            "AutoVerticalScrollStartDelayMs",
            typeof(ScrollViewerBehaviors),
            defaultValue: 1600);

    public static int GetAutoVerticalScrollStartDelayMs(AvaloniaObject element) =>
        element.GetValue(AutoVerticalScrollStartDelayMsProperty);

    public static void SetAutoVerticalScrollStartDelayMs(AvaloniaObject element, int value) =>
        element.SetValue(AutoVerticalScrollStartDelayMsProperty, value);

    public static readonly AttachedProperty<int> AutoVerticalScrollEndPauseMsProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, int>(
            "AutoVerticalScrollEndPauseMs",
            typeof(ScrollViewerBehaviors),
            defaultValue: 1400);

    public static int GetAutoVerticalScrollEndPauseMs(AvaloniaObject element) =>
        element.GetValue(AutoVerticalScrollEndPauseMsProperty);

    public static void SetAutoVerticalScrollEndPauseMs(AvaloniaObject element, int value) =>
        element.SetValue(AutoVerticalScrollEndPauseMsProperty, value);

    public static readonly AttachedProperty<object?> AutoVerticalScrollRestartTokenProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, object?>(
            "AutoVerticalScrollRestartToken",
            typeof(ScrollViewerBehaviors));

    public static object? GetAutoVerticalScrollRestartToken(AvaloniaObject element) =>
        element.GetValue(AutoVerticalScrollRestartTokenProperty);

    public static void SetAutoVerticalScrollRestartToken(AvaloniaObject element, object? value) =>
        element.SetValue(AutoVerticalScrollRestartTokenProperty, value);

    private static readonly AttachedProperty<AutoVerticalScrollState?> AutoVerticalScrollStateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, AutoVerticalScrollState?>(
            "AutoVerticalScrollState",
            typeof(ScrollViewerBehaviors));

    private static AutoVerticalScrollState? GetAutoVerticalScrollState(AvaloniaObject element) =>
        element.GetValue(AutoVerticalScrollStateProperty);

    private static void SetAutoVerticalScrollState(AvaloniaObject element, AutoVerticalScrollState? value) =>
        element.SetValue(AutoVerticalScrollStateProperty, value);

    static ScrollViewerBehaviors()
    {
        AutoVerticalScrollProperty.Changed.AddClassHandler<ScrollViewer>((scrollViewer, e) =>
        {
            var enable = (bool)e.NewValue!;

            var existing = GetAutoVerticalScrollState(scrollViewer);
            existing?.Dispose();
            SetAutoVerticalScrollState(scrollViewer, null);

            if (!enable)
                return;

            var state = new AutoVerticalScrollState(scrollViewer);
            SetAutoVerticalScrollState(scrollViewer, state);
        });

        AutoVerticalScrollRestartTokenProperty.Changed.AddClassHandler<ScrollViewer>((scrollViewer, _) =>
        {
            if (!GetAutoVerticalScroll(scrollViewer))
                return;

            GetAutoVerticalScrollState(scrollViewer)?.Reset(withStartDelay: true);
        });
    }
}
