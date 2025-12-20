using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Services;

namespace Retromind.Views;

public partial class BigModeHostView : UserControl
{
    private ContentPresenter _themePresenter = null!;
    private Border _videoBorder = null!;

    private Control? _videoSlot;

    // Coalesce LayoutUpdated spam into a single placement calculation per UI tick
    private bool _placementUpdateQueued;

    // Track hooked list boxes so we can unhook on theme swap (avoid leaks)
    private readonly List<ListBox> _tunedListBoxes = new();

    public BigModeHostView()
    {
        InitializeComponent();

        _themePresenter = this.FindControl<ContentPresenter>("ThemePresenter");
        _videoBorder = this.FindControl<Border>("VideoBorder");

        ResetVideoBorder();
        
        LayoutUpdated += (_, _) => QueueUpdateVideoPlacement();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public void SetThemeContent(Control themeRoot, Theme theme)
    {
        UnhookThemeTuning();

        // Ensure bindings in the theme root resolve to the BigModeViewModel
        themeRoot.DataContext = DataContext;

        _themePresenter.Content = themeRoot;

        // Convention: the theme defines one element that acts as slot for video placement.
        // Default is "VideoSlot", but theme can override via ThemeProperties.VideoSlotName on the root.
        var slotName = string.IsNullOrWhiteSpace(theme.VideoSlotName) ? "VideoSlot" : theme.VideoSlotName;
        _videoSlot = themeRoot.FindControl<Control>(slotName);

        // Capability: theme allows video AND a slot exists
        var canShowVideo = theme.VideoEnabled && _videoSlot != null;

        // Reset first so old placements/state never leaks across theme swaps
        ResetVideoBorder();
        
        if (DataContext is Retromind.ViewModels.BigModeViewModel vm)
        {
            vm.CanShowVideo = canShowVideo;

            if (!vm.CanShowVideo)
            {
                // Ensure overlay is fully gone (no standbild, no wasted work)
                vm.IsVideoVisible = false;
                vm.IsVideoOverlayVisible = false;
            }
        }
        
        // Apply theme tuning (selection UX, spacing, typography, animation timings)
        ApplyThemeTuning(themeRoot);

        // Layout settles asynchronously; schedule placement + VM "view ready" after render ticks
        QueueUpdateVideoPlacement();
        NotifyViewReadyAfterRender(DataContext!);
    }

    private void UnhookThemeTuning()
    {
        foreach (var lb in _tunedListBoxes)
        {
            lb.SelectionChanged -= OnListBoxSelectionChanged;
            lb.ContainerPrepared -= OnListBoxContainerPrepared;
        }

        _tunedListBoxes.Clear();
    }

    private void ApplyThemeTuning(Control themeRoot)
    {
        // Read tuning values from theme root (attached properties).
        var selectedScale = ThemeProperties.GetSelectedScale(themeRoot);
        var unselectedOpacity = ThemeProperties.GetUnselectedOpacity(themeRoot);
        var selectedGlowOpacity = ThemeProperties.GetSelectedGlowOpacity(themeRoot);

        var fadeMs = ThemeProperties.GetFadeDurationMs(themeRoot);
        var moveMs = ThemeProperties.GetMoveDurationMs(themeRoot);

        var panelPadding = ThemeProperties.GetPanelPadding(themeRoot);
        var headerSpacing = ThemeProperties.GetHeaderSpacing(themeRoot);

        var titleFontSize = ThemeProperties.GetTitleFontSize(themeRoot);
        var bodyFontSize = ThemeProperties.GetBodyFontSize(themeRoot);
        var captionFontSize = ThemeProperties.GetCaptionFontSize(themeRoot);

        var accent = ThemeProperties.GetAccentColor(themeRoot);

        // 3) Apply animation duration to the stable video overlay fade (Opacity transition).
        // Keep it very safe: if transitions are missing, create them.
        var fadeDuration = TimeSpan.FromMilliseconds(Math.Clamp(fadeMs, 0, 5000));
        _videoBorder.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = fadeDuration
            }
        };

        // 1) Selection UX: apply to ALL ListBoxes in the theme visual tree.
        // We use ContainerPrepared + SelectionChanged so it works with virtualization.
        foreach (var lb in themeRoot.GetVisualDescendants())
        {
            if (lb is not ListBox listBox)
                continue;

            listBox.SelectionChanged += OnListBoxSelectionChanged;
            listBox.ContainerPrepared += OnListBoxContainerPrepared;
            _tunedListBoxes.Add(listBox);

            // Apply current selection state for realized containers.
            ApplySelectionVisuals(listBox, selectedScale, unselectedOpacity, selectedGlowOpacity, fadeMs, moveMs, accent);
        }

        // 2) Spacing/Padding: opt-in via classes (theme authors can just add Classes="rm-panel"/"rm-header")
        foreach (var v in themeRoot.GetVisualDescendants())
        {
            if (v is not Control c)
                continue;

            if (c.Classes.Contains("rm-panel"))
            {
                // Prefer Border.Padding when possible; fallback to Margin.
                if (c is Border border)
                {
                    border.Padding = panelPadding;
                }
                else
                {
                    c.Margin = panelPadding;
                }
            }

            if (c is StackPanel sp && sp.Classes.Contains("rm-header"))
            {
                sp.Spacing = headerSpacing;
            }
        }

        // 4) Typography defaults: opt-in via classes (rm-title/rm-body/rm-caption)
        foreach (var v in themeRoot.GetVisualDescendants())
        {
            if (v is not TextBlock tb)
                continue;

            if (tb.Classes.Contains("rm-title"))
                tb.FontSize = titleFontSize;
            else if (tb.Classes.Contains("rm-body"))
                tb.FontSize = bodyFontSize;
            else if (tb.Classes.Contains("rm-caption"))
                tb.FontSize = captionFontSize;
        }
    }

    private void OnListBoxContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (_themePresenter.Content is not Control themeRoot)
            return;

        var selectedScale = ThemeProperties.GetSelectedScale(themeRoot);
        var unselectedOpacity = ThemeProperties.GetUnselectedOpacity(themeRoot);
        var selectedGlowOpacity = ThemeProperties.GetSelectedGlowOpacity(themeRoot);
        var fadeMs = ThemeProperties.GetFadeDurationMs(themeRoot);
        var moveMs = ThemeProperties.GetMoveDurationMs(themeRoot);
        var accent = ThemeProperties.GetAccentColor(themeRoot);

        // Ensure newly realized container gets the correct visuals.
        ApplySelectionVisuals(listBox, selectedScale, unselectedOpacity, selectedGlowOpacity, fadeMs, moveMs, accent);
    }

    private void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        if (_themePresenter.Content is not Control themeRoot)
            return;

        var selectedScale = ThemeProperties.GetSelectedScale(themeRoot);
        var unselectedOpacity = ThemeProperties.GetUnselectedOpacity(themeRoot);
        var selectedGlowOpacity = ThemeProperties.GetSelectedGlowOpacity(themeRoot);
        var fadeMs = ThemeProperties.GetFadeDurationMs(themeRoot);
        var moveMs = ThemeProperties.GetMoveDurationMs(themeRoot);
        var accent = ThemeProperties.GetAccentColor(themeRoot);

        ApplySelectionVisuals(listBox, selectedScale, unselectedOpacity, selectedGlowOpacity, fadeMs, moveMs, accent);
    }

    private static void ApplySelectionVisuals(
        ListBox listBox,
        double selectedScale,
        double unselectedOpacity,
        double selectedGlowOpacity,
        int fadeMs,
        int moveMs,
        Color? accentColor)
    {
        var fadeDuration = TimeSpan.FromMilliseconds(Math.Clamp(fadeMs, 0, 5000));
        var moveDuration = TimeSpan.FromMilliseconds(Math.Clamp(moveMs, 0, 5000));

        // Walk realized containers only (virtualization-friendly).
        for (int i = 0; i < listBox.ItemCount; i++)
        {
            if (listBox.ContainerFromIndex(i) is not ListBoxItem item)
                continue;

            var isSelected = item.IsSelected;

            // Base values
            item.Opacity = isSelected ? 1.0 : Math.Clamp(unselectedOpacity, 0.0, 1.0);
            item.RenderTransformOrigin = RelativePoint.Center;

            // Ensure transform exists
            if (item.RenderTransform is not ScaleTransform st)
            {
                st = new ScaleTransform(1, 1);
                item.RenderTransform = st;
            }

            var scale = isSelected ? Math.Clamp(selectedScale, 0.1, 4.0) : 1.0;
            st.ScaleX = scale;
            st.ScaleY = scale;

            // Selected glow as a subtle drop shadow on the whole container
            if (isSelected && selectedGlowOpacity > 0.001)
            {
                var glowColor = accentColor ?? Colors.Gold;

                item.Effect = new DropShadowEffect
                {
                    Color = glowColor,
                    BlurRadius = 18,
                    OffsetX = 0,
                    OffsetY = 0,
                    Opacity = Math.Clamp(selectedGlowOpacity, 0.0, 1.0)
                };
            }
            else
            {
                item.Effect = null;
            }

            // Transitions: keep them small and predictable.
            item.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = fadeDuration, Easing = new CubicEaseOut() },
                // ScaleTransform changes are animated via RenderTransform property transition.
                // We transition the whole RenderTransform, which is good enough here.
                new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = moveDuration, Easing = new CubicEaseOut() }
            };
        }
    }
    
    private void QueueUpdateVideoPlacement()
    {
        if (_placementUpdateQueued)
            return;

        _placementUpdateQueued = true;

        UiThreadHelper.Post(() =>
        {
            _placementUpdateQueued = false;
            UpdateVideoPlacement();
        }, DispatcherPriority.Render);
    }

    private void ResetVideoBorder()
    {
        _videoBorder.Width = 0;
        _videoBorder.Height = 0;
        Canvas.SetLeft(_videoBorder, 0);
        Canvas.SetTop(_videoBorder, 0);
    }
    
    private void UpdateVideoPlacement()
    {
        if (_videoSlot == null)
        {
            ResetVideoBorder();
            return;
        }
        
        // The slot must be attached to the visual tree to resolve coordinates.
        var topLeft = _videoSlot.TranslatePoint(new Point(0, 0), this);
        if (topLeft == null)
        {
            ResetVideoBorder();
            return;
        }
        
        var bounds = _videoSlot.Bounds;

        // If the slot has no meaningful size yet, keep the overlay disabled
        // to avoid full-screen or glitchy placement.
        if (bounds.Width < 1 || bounds.Height < 1)
        {
            ResetVideoBorder();
            return;
        }

        Canvas.SetLeft(_videoBorder, topLeft.Value.X);
        Canvas.SetTop(_videoBorder, topLeft.Value.Y);

        _videoBorder.Width = Math.Max(0, bounds.Width);
        _videoBorder.Height = Math.Max(0, bounds.Height);
    }

    public async void NotifyViewReadyAfterRender(object viewModel)
    {
        // We want the VideoSlot to have real bounds before BigMode starts preview playback.
        // Otherwise the preview may start while the overlay is still 0x0 and appears "not playing".
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
        UpdateVideoPlacement();

        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
        UpdateVideoPlacement();

        // One extra tick for tricky setups (Wayland/XWayland + LibVLC embedding).
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
        UpdateVideoPlacement();

        if (viewModel is Retromind.ViewModels.BigModeViewModel vm)
            vm.NotifyViewReady();
    }
}