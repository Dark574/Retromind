using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Retromind.Extensions;

namespace Retromind.Helpers.Video;

public sealed class CrossfadeVideoSurfaceControl : Grid
{
    public static readonly StyledProperty<IVideoSurface?> SurfaceAProperty =
        AvaloniaProperty.Register<CrossfadeVideoSurfaceControl, IVideoSurface?>(
            nameof(SurfaceA));

    public static readonly StyledProperty<IVideoSurface?> SurfaceBProperty =
        AvaloniaProperty.Register<CrossfadeVideoSurfaceControl, IVideoSurface?>(
            nameof(SurfaceB));

    public static readonly StyledProperty<int> ActiveIndexProperty =
        AvaloniaProperty.Register<CrossfadeVideoSurfaceControl, int>(
            nameof(ActiveIndex),
            defaultValue: -1);

    public static readonly StyledProperty<int> FadeDurationMsProperty =
        AvaloniaProperty.Register<CrossfadeVideoSurfaceControl, int>(
            nameof(FadeDurationMs),
            defaultValue: 0);

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<CrossfadeVideoSurfaceControl, Stretch>(
            nameof(Stretch),
            defaultValue: Stretch.Fill);

    private readonly VideoSurfaceControl _surfaceControlA;
    private readonly VideoSurfaceControl _surfaceControlB;
    private int _activeIndex;

    static CrossfadeVideoSurfaceControl()
    {
        SurfaceAProperty.Changed.AddClassHandler<CrossfadeVideoSurfaceControl>((c, e) =>
            SetSurface(c._surfaceControlA, (IVideoSurface?)e.NewValue));

        SurfaceBProperty.Changed.AddClassHandler<CrossfadeVideoSurfaceControl>((c, e) =>
            SetSurface(c._surfaceControlB, (IVideoSurface?)e.NewValue));

        ActiveIndexProperty.Changed.AddClassHandler<CrossfadeVideoSurfaceControl>((c, e) =>
            c.SetActiveIndex((int)e.NewValue!));

        FadeDurationMsProperty.Changed.AddClassHandler<CrossfadeVideoSurfaceControl>((c, _) =>
            c.UpdateTransitions());

        StretchProperty.Changed.AddClassHandler<CrossfadeVideoSurfaceControl>((c, e) =>
            c.ApplyStretch((Stretch)e.NewValue!));
    }

    public CrossfadeVideoSurfaceControl()
    {
        _surfaceControlA = CreateSurfaceControl(0);
        _surfaceControlB = CreateSurfaceControl(1);

        Children.Add(_surfaceControlA);
        Children.Add(_surfaceControlB);

        UpdateTransitions();
        SetActiveIndex(ActiveIndex);
    }

    public IVideoSurface? SurfaceA
    {
        get => GetValue(SurfaceAProperty);
        set => SetValue(SurfaceAProperty, value);
    }

    public IVideoSurface? SurfaceB
    {
        get => GetValue(SurfaceBProperty);
        set => SetValue(SurfaceBProperty, value);
    }

    public int ActiveIndex
    {
        get => GetValue(ActiveIndexProperty);
        set => SetValue(ActiveIndexProperty, value);
    }

    public int FadeDurationMs
    {
        get => GetValue(FadeDurationMsProperty);
        set => SetValue(FadeDurationMsProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    private static VideoSurfaceControl CreateSurfaceControl(int zIndex)
    {
        var control = new VideoSurfaceControl
        {
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        control.SetValue(Panel.ZIndexProperty, zIndex);
        return control;
    }

    private static void SetSurface(VideoSurfaceControl control, IVideoSurface? surface)
    {
        control.Surface = surface;
    }

    private void ApplyStretch(Stretch stretch)
    {
        _surfaceControlA.Stretch = stretch;
        _surfaceControlB.Stretch = stretch;
    }

    private void SetActiveIndex(int index)
    {
        if (_activeIndex == index)
            return;

        _activeIndex = index;

        if (index is 0 or 1)
        {
            var target = index == 0 ? _surfaceControlA : _surfaceControlB;
            var other = index == 0 ? _surfaceControlB : _surfaceControlA;

            UpdateTransitions();
            target.Opacity = 1;
            other.Opacity = 0;
        }
        else
        {
            UpdateTransitions();
            _surfaceControlA.Opacity = 0;
            _surfaceControlB.Opacity = 0;
        }
    }

    private void UpdateTransitions()
    {
        var duration = ResolveFadeDuration();
        EnsureOpacityTransition(_surfaceControlA, duration);
        EnsureOpacityTransition(_surfaceControlB, duration);
    }

    private TimeSpan ResolveFadeDuration()
    {
        var ms = FadeDurationMs;
        if (ms <= 0)
        {
            var ancestor = this.GetVisualParent();
            UserControl? themeRoot = null;
            while (ancestor != null)
            {
                if (ancestor is UserControl uc)
                {
                    themeRoot = uc;
                    break;
                }
                ancestor = ancestor.GetVisualParent();
            }

            if (themeRoot != null)
                ms = ThemeProperties.GetVideoFadeDurationMs(themeRoot);
        }

        if (ms <= 0)
            ms = 250;

        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, 10000));
    }

    private static void EnsureOpacityTransition(Control target, TimeSpan duration)
    {
        var transitions = target.Transitions ?? new Transitions();
        target.Transitions = transitions;

        DoubleTransition? opacityTransition = null;
        foreach (var t in transitions)
        {
            if (t is DoubleTransition dt && Equals(dt.Property, Visual.OpacityProperty))
            {
                opacityTransition = dt;
                break;
            }
        }

        if (opacityTransition == null)
        {
            opacityTransition = new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Easing = new CubicEaseOut()
            };
            transitions.Add(opacityTransition);
        }
        else
        {
            opacityTransition.Duration = duration;
            opacityTransition.Easing ??= new CubicEaseOut();
        }
    }
}
