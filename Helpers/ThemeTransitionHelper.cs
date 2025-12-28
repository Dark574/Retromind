using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Retromind.Extensions;

namespace Retromind.Helpers;

public static class ThemeTransitionHelper
{
    // Öffentliche Convenience-Methoden für die drei Slots

    public static void AnimatePrimaryVisual(Control? themeRoot)
        => AnimateVisualSlot(
            themeRoot,
            ThemeProperties.GetPrimaryVisualElementName,
            ThemeProperties.GetPrimaryVisualEnterMode,
            ThemeProperties.GetPrimaryVisualEnterOffsetX,
            ThemeProperties.GetPrimaryVisualEnterOffsetY);

    public static void AnimateSecondaryVisual(Control? themeRoot)
        => AnimateVisualSlot(
            themeRoot,
            ThemeProperties.GetSecondaryVisualElementName,
            ThemeProperties.GetSecondaryVisualEnterMode,
            ThemeProperties.GetSecondaryVisualEnterOffsetX,
            ThemeProperties.GetSecondaryVisualEnterOffsetY);

    public static void AnimateBackgroundVisual(Control? themeRoot)
        => AnimateVisualSlot(
            themeRoot,
            ThemeProperties.GetBackgroundVisualElementName,
            ThemeProperties.GetBackgroundVisualEnterMode,
            ThemeProperties.GetBackgroundVisualEnterOffsetX,
            ThemeProperties.GetBackgroundVisualEnterOffsetY);

    // --- Gemeinsame Implementierung ---

    private static void AnimateVisualSlot(
        Control? themeRoot,
        Func<AvaloniaObject, string> getName,
        Func<AvaloniaObject, string> getMode,
        Func<AvaloniaObject, double> getOffsetX,
        Func<AvaloniaObject, double> getOffsetY)
    {
        if (themeRoot is null)
            return;

        var elementName = getName(themeRoot);
        if (string.IsNullOrWhiteSpace(elementName))
            return;

        var mode = getMode(themeRoot) ?? "None";
        var offsetXProp = getOffsetX(themeRoot);
        var offsetYProp = getOffsetY(themeRoot);
        var fadeDuration = TimeSpan.FromMilliseconds(ThemeProperties.GetFadeDurationMs(themeRoot));
        var moveDuration = TimeSpan.FromMilliseconds(ThemeProperties.GetMoveDurationMs(themeRoot));

        if (themeRoot.FindControl<Control>(elementName) is not { } target)
            return;

        EnsureTransitions(target, fadeDuration, moveDuration);

        // Aktuelle Transitions sichern; wir deaktivieren sie kurz für das Setzen des Startzustands.
        var transitionsBackup = target.Transitions;

        // Use a stable "base margin" that does not drift across animations.
        Thickness baseMargin;
        if (!ThemeProperties.GetVisualBaseMarginInitialized(target))
        {
            // First run: capture the current margin as the base margin.
            baseMargin = target.Margin;
            ThemeProperties.SetVisualBaseMargin(target, baseMargin);
            ThemeProperties.SetVisualBaseMarginInitialized(target, true);
        }
        else
        {
            // Subsequent runs: reuse the original base margin.
            baseMargin = ThemeProperties.GetVisualBaseMargin(target);
        }
        
        Thickness startMargin = baseMargin;

        // Für Zoom/Pulse sorgen wir für einen ScaleTransform
        ScaleTransform? scaleTransform = null;
        if (mode is "ZoomIn" or "Pulse")
        {
            if (target.RenderTransform is ScaleTransform st)
            {
                scaleTransform = st;
            }
            else
            {
                scaleTransform = new ScaleTransform(1, 1);
                target.RenderTransform = scaleTransform;
            }
            target.RenderTransformOrigin = RelativePoint.Center;
        }

        switch (mode)
        {
            case "Fade":
                target.Opacity = 0;
                startMargin = baseMargin;
                break;

            case "SlideFromLeft":
            {
                target.Opacity = 0;
                var dx = offsetXProp != 0 ? offsetXProp : -420;
                startMargin = new Thickness(
                    baseMargin.Left + dx,
                    baseMargin.Top,
                    baseMargin.Right,
                    baseMargin.Bottom);
                break;
            }

            case "SlideFromRight":
            {
                target.Opacity = 0;
                var dx = offsetXProp != 0 ? offsetXProp : 420;
                startMargin = new Thickness(
                    baseMargin.Left + dx,
                    baseMargin.Top,
                    baseMargin.Right,
                    baseMargin.Bottom);
                break;
            }

            case "SlideFromTop":
            {
                target.Opacity = 0;
                var dy = offsetYProp != 0 ? offsetYProp : -180;
                startMargin = new Thickness(
                    baseMargin.Left,
                    baseMargin.Top + dy,
                    baseMargin.Right,
                    baseMargin.Bottom);
                break;
            }

            case "SlideFromBottom":
            {
                target.Opacity = 0;
                var dy = offsetYProp != 0 ? offsetYProp : 180;
                startMargin = new Thickness(
                    baseMargin.Left,
                    baseMargin.Top + dy,
                    baseMargin.Right,
                    baseMargin.Bottom);
                break;
            }

            case "ZoomIn":
            {
                if (scaleTransform is null)
                    return;

                // etwas kleiner starten
                scaleTransform.ScaleX = 0.8;
                scaleTransform.ScaleY = 0.8;
                target.Opacity = 0;
                startMargin = baseMargin; // keine Verschiebung
                break;
            }

            case "Pulse":
            {
                if (scaleTransform is null)
                    return;

                // leicht kleiner starten, Ziel leicht größer als 1.0
                scaleTransform.ScaleX = 0.9;
                scaleTransform.ScaleY = 0.9;
                target.Opacity = 0;
                startMargin = baseMargin;
                break;
            }

            case "None":
            default:
                return;
        }

        // Debug: protokolliere Animationsstart (nur für PrimaryVisual/CoverPanel interessant)
        try
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ThemeTransition] Slot='{elementName}', Mode='{mode}', " +
                $"StartMargin={startMargin}, BaseMargin={baseMargin}, " +
                $"OffsetX={offsetXProp}, OffsetY={offsetYProp}");
        }
        catch
        {
            // Debug-Output darf keine Exceptions werfen
        }

        // 1) Startzustand OHNE Transitions setzen (sofortiger Sprung nach links + Opacity 0)
        target.Transitions = null;
        target.Margin = startMargin;
        target.Opacity = 0;

        // 2) Danach (nächster Render-Tick) Transitions wieder aktivieren und zum Ziel animieren
        Dispatcher.UIThread.Post(() =>
        {
            if (themeRoot.FindControl<Control>(elementName) != target)
                return;

            // Transitions wieder herstellen (oder neu setzen, falls der Host sie in der Zwischenzeit geändert hat)
            target.Transitions = transitionsBackup;

            // Ziel: Basis-Margin + volle Opacity
            target.Margin = baseMargin;
            target.Opacity = 1;

            if (mode is "ZoomIn" && target.RenderTransform is ScaleTransform stZoom)
            {
                stZoom.ScaleX = 1.0;
                stZoom.ScaleY = 1.0;
            }
            else if (mode is "Pulse" && target.RenderTransform is ScaleTransform stPulse)
            {
                // Kleiner „Pop“ nach vorne
                stPulse.ScaleX = 1.05;
                stPulse.ScaleY = 1.05;
            }

        }, DispatcherPriority.Render);
    }

    private static void EnsureTransitions(Control target, TimeSpan fadeDuration, TimeSpan moveDuration)
    {
        var transitions = target.Transitions ?? new Transitions();
        target.Transitions = transitions;

        // Opacity
        DoubleTransition? opacityTransition = null;
        foreach (var t in transitions)
        {
            if (t is DoubleTransition dt && Equals(dt.Property, Visual.OpacityProperty))
            {
                opacityTransition = dt;
                break;
            }
        }

        if (opacityTransition is null)
        {
            opacityTransition = new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = fadeDuration,
                Easing = new CubicEaseOut()
            };
            transitions.Add(opacityTransition);
        }
        else
        {
            opacityTransition.Duration = fadeDuration;
            opacityTransition.Easing ??= new CubicEaseOut();
        }

        // Margin (für Slide-Modi)
        ThicknessTransition? marginTransition = null;
        foreach (var t in transitions)
        {
            if (t is ThicknessTransition tt && Equals(tt.Property, Layoutable.MarginProperty))
            {
                marginTransition = tt;
                break;
            }
        }

        if (marginTransition is null)
        {
            marginTransition = new ThicknessTransition
            {
                Property = Layoutable.MarginProperty,
                Duration = moveDuration,
                Easing = new CubicEaseOut()
            };
            transitions.Add(marginTransition);
        }
        else
        {
            marginTransition.Duration = moveDuration;
            marginTransition.Easing ??= new CubicEaseOut();
        }

        // RenderTransform (für ZoomIn/Pulse – gesamte Transform wird animiert)
        TransformOperationsTransition? transformTransition = null;
        foreach (var t in transitions)
        {
            if (t is TransformOperationsTransition tr && Equals(tr.Property, Visual.RenderTransformProperty))
            {
                transformTransition = tr;
                break;
            }
        }

        if (transformTransition is null)
        {
            transformTransition = new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = moveDuration,
                Easing = new CubicEaseOut()
            };
            transitions.Add(transformTransition);
        }
        else
        {
            transformTransition.Duration = moveDuration;
            transformTransition.Easing ??= new CubicEaseOut();
        }
    }
}