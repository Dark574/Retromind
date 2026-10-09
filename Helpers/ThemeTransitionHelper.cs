using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Retromind.Extensions;

namespace Retromind.Helpers;

public static class ThemeTransitionHelper
{
    private sealed class AnimationState
    {
        private readonly List<(AvaloniaObject Source, EventHandler<AvaloniaPropertyChangedEventArgs> Handler)>
            _subscriptions = new();

        public int Generation { get; set; }

        public void AddSubscription(
            AvaloniaObject source,
            EventHandler<AvaloniaPropertyChangedEventArgs> handler)
        {
            source.PropertyChanged += handler;
            _subscriptions.Add((source, handler));
        }

        public void ClearSubscriptions()
        {
            foreach (var (source, handler) in _subscriptions)
                source.PropertyChanged -= handler;

            _subscriptions.Clear();
        }
    }

    private static readonly ConditionalWeakTable<Control, AnimationState> AnimationStates = new();

    // Public convenience methods for the three slots

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

    // --- Shared implementation ---

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
        if (mode == "None")
            return;

        var offsetXProp = getOffsetX(themeRoot);
        var offsetYProp = getOffsetY(themeRoot);
        var fadeDuration = TimeSpan.FromMilliseconds(ThemeProperties.GetFadeDurationMs(themeRoot));
        var moveDuration = TimeSpan.FromMilliseconds(ThemeProperties.GetMoveDurationMs(themeRoot));

        if (themeRoot.FindControl<Control>(elementName) is not { } target)
            return;

        var animationState = AnimationStates.GetOrCreateValue(target);
        animationState.ClearSubscriptions();
        var animationGeneration = ++animationState.Generation;
        var isSlideMode = mode is "SlideFromLeft" or "SlideFromRight" or "SlideFromTop" or "SlideFromBottom";
        var compositionVisual = isSlideMode && ThemeProperties.GetUseCompositorEnterAnimation(target)
            ? ElementComposition.GetElementVisual(target)
            : null;

        if (compositionVisual != null)
        {
            compositionVisual.StopAnimation(nameof(CompositionVisual.Offset));
            compositionVisual.StopAnimation(nameof(CompositionVisual.Opacity));
        }

        EnsureTransitions(target, fadeDuration, moveDuration);

        // Backup current transitions; temporarily disable them to set the start state.
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
        var startTranslateX = 0.0;
        var startTranslateY = 0.0;
        TranslateTransform? slideTransform = null;
        Transitions? slideTransitions = null;

        if (isSlideMode && compositionVisual == null)
        {
            // Moving Margin forces a full layout pass for every animation frame.
            // TranslateTransform keeps the layout stable and lets the compositor
            // move the already arranged visual instead.
            target.Transitions = null;
            slideTransform = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
            target.RenderTransform = slideTransform;
            EnsureSlideTransitions(slideTransform, moveDuration);
            slideTransitions = slideTransform.Transitions;
            target.Transitions = transitionsBackup;
        }

        // For Zoom/Pulse, ensure a ScaleTransform.
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
                startTranslateX = offsetXProp != 0 ? offsetXProp : -420;
                break;
            }

            case "SlideFromRight":
            {
                target.Opacity = 0;
                startTranslateX = offsetXProp != 0 ? offsetXProp : 420;
                break;
            }

            case "SlideFromTop":
            {
                target.Opacity = 0;
                startTranslateY = offsetYProp != 0 ? offsetYProp : -180;
                break;
            }

            case "SlideFromBottom":
            {
                target.Opacity = 0;
                startTranslateY = offsetYProp != 0 ? offsetYProp : 180;
                break;
            }

            case "ZoomIn":
            {
                if (scaleTransform is null)
                    return;

                // Start slightly smaller.
                scaleTransform.ScaleX = 0.8;
                scaleTransform.ScaleY = 0.8;
                target.Opacity = 0;
                startMargin = baseMargin; // No offset.
                break;
            }

            case "Pulse":
            {
                if (scaleTransform is null)
                    return;

                // Start slightly smaller, target slightly larger than 1.0.
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

        // Debug: log animation start (only relevant for PrimaryVisual/CoverPanel).
        try
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ThemeTransition] Slot='{elementName}', Mode='{mode}', " +
                $"StartMargin={startMargin}, BaseMargin={baseMargin}, " +
                $"OffsetX={offsetXProp}, OffsetY={offsetYProp}");
        }
        catch
        {
            // Debug output must not throw exceptions.
        }

        // 1) Set start state without transitions (instant jump + opacity 0).
        target.Transitions = null;
        if (slideTransform != null)
        {
            slideTransform.Transitions = null;
            slideTransform.X = startTranslateX;
            slideTransform.Y = startTranslateY;
        }

        target.Margin = baseMargin;
        target.Opacity = 0;

        void StartTransition()
        {
            if (animationState.Generation != animationGeneration)
                return;

            if (themeRoot.FindControl<Control>(elementName) != target)
                return;

            animationState.ClearSubscriptions();

            if (compositionVisual != null)
            {
                // Keep the XAML properties at their final values and animate only
                // the backing composition visual. These keyframes are evaluated on
                // the render thread, independently of UI-thread media setup.
                target.Transitions = null;
                target.Margin = baseMargin;
                target.Opacity = 1;
                StartCompositorSlide(
                    compositionVisual,
                    startTranslateX,
                    startTranslateY,
                    moveDuration,
                    fadeDuration);
                target.Transitions = transitionsBackup;
                return;
            }

            // Restore transitions (or set new ones if the host changed them in the meantime).
            target.Transitions = transitionsBackup;

            // Target: base margin + full opacity.
            target.Margin = baseMargin;
            target.Opacity = 1;

            if (slideTransform != null)
            {
                slideTransform.Transitions = slideTransitions;
                slideTransform.X = 0;
                slideTransform.Y = 0;
            }

            if (mode is "ZoomIn" && target.RenderTransform is ScaleTransform stZoom)
            {
                stZoom.ScaleX = 1.0;
                stZoom.ScaleY = 1.0;
            }
            else if (mode is "Pulse" && target.RenderTransform is ScaleTransform stPulse)
            {
                // Small pop forward.
                stPulse.ScaleX = 1.05;
                stPulse.ScaleY = 1.05;
            }

        }

        // 2) On the next render tick, bindings point at the new selection. Themes
        // can keep the visual at its hidden start position until its async artwork
        // is ready, avoiding an image appearing halfway through the movement.
        Dispatcher.UIThread.Post(() =>
        {
            if (animationState.Generation != animationGeneration)
                return;

            var waitForAsyncImage = ThemeProperties.GetWaitForAsyncImageBeforeEnter(target);
            var waitForPreviewMedia = ThemeProperties.GetWaitForPreviewMediaBeforeEnter(target);
            if (!waitForAsyncImage && !waitForPreviewMedia)
            {
                StartTransition();
                return;
            }

            var pendingImages = new List<Image>();
            if (waitForAsyncImage)
            {
                if (target is Image targetImage)
                    AddPendingImage(targetImage);

                foreach (var image in target.GetVisualDescendants().OfType<Image>())
                    AddPendingImage(image);
            }

            bool IsReady()
            {
                var artworkReady = !waitForAsyncImage ||
                                   pendingImages.Count == 0 ||
                                   pendingImages.Any(AsyncImageHelper.GetIsLoaded);
                var previewMediaReady = !waitForPreviewMedia ||
                                        ThemeProperties.GetPreviewMediaReady(themeRoot);
                return artworkReady && previewMediaReady;
            }

            void TryStartTransition()
            {
                if (IsReady())
                    StartTransition();
            }

            foreach (var image in pendingImages)
            {
                EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, args) =>
                {
                    if (args.Property == AsyncImageHelper.IsLoadedProperty &&
                        AsyncImageHelper.GetIsLoaded(image))
                    {
                        TryStartTransition();
                    }
                };
                animationState.AddSubscription(image, handler);
            }

            if (waitForPreviewMedia)
            {
                EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, args) =>
                {
                    if (args.Property == ThemeProperties.PreviewMediaReadyProperty)
                        TryStartTransition();
                };
                animationState.AddSubscription(themeRoot, handler);
            }

            TryStartTransition();

            void AddPendingImage(Image image)
            {
                if (image.IsVisible && !string.IsNullOrWhiteSpace(AsyncImageHelper.GetUrl(image)))
                    pendingImages.Add(image);
            }

        }, DispatcherPriority.Render);
    }

    private static void StartCompositorSlide(
        CompositionVisual visual,
        double offsetX,
        double offsetY,
        TimeSpan moveDuration,
        TimeSpan fadeDuration)
    {
        var compositor = visual.Compositor;
        var baseOffset = visual.Offset;
        var finalOffset = new Vector3(
            (float)baseOffset.X,
            (float)baseOffset.Y,
            (float)baseOffset.Z);

        var moveAnimation = compositor.CreateVector3KeyFrameAnimation();
        moveAnimation.Duration = moveDuration;
        moveAnimation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        moveAnimation.InsertKeyFrame(
            0f,
            finalOffset + new Vector3((float)offsetX, (float)offsetY, 0));
        moveAnimation.InsertKeyFrame(1f, finalOffset, new CubicEaseOut());

        var fadeAnimation = compositor.CreateScalarKeyFrameAnimation();
        fadeAnimation.Duration = fadeDuration;
        fadeAnimation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        fadeAnimation.InsertKeyFrame(0f, 0f);
        fadeAnimation.InsertKeyFrame(1f, 1f, new CubicEaseOut());

        visual.StartAnimation(nameof(CompositionVisual.Offset), moveAnimation);
        visual.StartAnimation(nameof(CompositionVisual.Opacity), fadeAnimation);
    }

    private static void EnsureSlideTransitions(TranslateTransform transform, TimeSpan duration)
    {
        var transitions = transform.Transitions ?? new Transitions();
        transform.Transitions = transitions;

        EnsureDoubleTransition(transitions, TranslateTransform.XProperty, duration);
        EnsureDoubleTransition(transitions, TranslateTransform.YProperty, duration);
    }

    private static void EnsureDoubleTransition(
        Transitions transitions,
        AvaloniaProperty<double> property,
        TimeSpan duration)
    {
        DoubleTransition? transition = null;
        foreach (var candidate in transitions)
        {
            if (candidate is DoubleTransition existing && Equals(existing.Property, property))
            {
                transition = existing;
                break;
            }
        }

        if (transition == null)
        {
            transition = new DoubleTransition
            {
                Property = property,
                Duration = duration,
                Easing = new CubicEaseOut()
            };
            transitions.Add(transition);
            return;
        }

        transition.Duration = duration;
        transition.Easing ??= new CubicEaseOut();
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

        // Margin (for slide modes)
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

        // RenderTransform (for ZoomIn/Pulse - the whole transform is animated)
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
