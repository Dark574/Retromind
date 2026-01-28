using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.Views;

public partial class BigModeHostView : UserControl
{
    private ContentPresenter _themePresenter = null!;
    
    // When the active theme is a "system host" theme, this points to its
    // right-hand content placeholder (SystemLayoutHost).
    private ContentControl? _systemLayoutHost;

    // True while the active theme is the SystemHost theme (detected by presence
    // of a "SystemLayoutHost" ContentControl in the theme root).
    private bool _isSystemHostTheme;
    
    // Cache of loaded system subthemes (e.g. Themes/System/C64/theme.axaml).
    // Key = SystemPreviewThemeId / folder name ("Default", "C64", ...).
    // The cached Theme contains a factory that can create fresh view instances
    // on demand, so we never reuse the same UserControl across different parents.
    private readonly Dictionary<string, Theme> _systemThemeCache = new(StringComparer.OrdinalIgnoreCase);

    // Shared primary video control for the main preview channel.
    private readonly VideoSurfaceControl _primaryVideoControl;

    // Shared secondary video control for the background / B-roll channel.
    private readonly VideoSurfaceControl _secondaryVideoControl;
    
    // Track ViewModel notifications so we can react to SelectedCategory changes
    // while the SystemHost theme is active.
    private INotifyPropertyChanged? _vmNotifications;
    
    // Track hooked list boxes so we can unhook on theme swap (avoid leaks)
    private readonly List<ListBox> _tunedListBoxes = new();

    public BigModeHostView()
    {
        InitializeComponent();

        _themePresenter = this.FindControl<ContentPresenter>("ThemePresenter")
                          ?? throw new InvalidOperationException("ThemePresenter control not found in BigModeHostView.");

        // Shared primary video control: main preview channel
        _primaryVideoControl = new VideoSurfaceControl
        {
            Stretch = Stretch.Uniform
        };

        // Bind video surface and basic visibility to the main preview properties.
        // We keep the bindings simple and let the theme handle additional styling.
        _primaryVideoControl.Bind(VideoSurfaceControl.SurfaceProperty,
            new Binding("MainVideoSurface"));
        _primaryVideoControl.Bind(IsVisibleProperty,
            new Binding("MainVideoHasFrame"));
        
        // Shared secondary video control: background / B-roll channel.
        _secondaryVideoControl = new VideoSurfaceControl
        {
            Stretch = Stretch.Uniform
        };
        _secondaryVideoControl.Bind(VideoSurfaceControl.SurfaceProperty,
            new Binding("SecondaryVideoSurface"));
        _secondaryVideoControl.Bind(IsVisibleProperty,
            new Binding("SecondaryVideoHasContent"));
        
        // React when BigModeViewModel is swapped so we can subscribe to property changes.
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vmNotifications != null)
            _vmNotifications.PropertyChanged -= OnViewModelPropertyChanged;

        _vmNotifications = DataContext as INotifyPropertyChanged;

        if (_vmNotifications != null)
            _vmNotifications.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_vmNotifications != null)
        {
            _vmNotifications.PropertyChanged -= OnViewModelPropertyChanged;
            _vmNotifications = null;
        }

        UnhookThemeTuning();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 1) Item change: trigger the PrimaryVisual animation (for all themes)
        if (e.PropertyName == nameof(Retromind.ViewModels.BigModeViewModel.SelectedItem))
        {
            if (_themePresenter.Content is Control currentThemeRoot)
            {
                ThemeTransitionHelper.AnimatePrimaryVisual(currentThemeRoot);
            }
        }

        // 2) SystemHost theme only: refresh the system layout on category change
        if (_isSystemHostTheme &&
            e.PropertyName == nameof(Retromind.ViewModels.BigModeViewModel.SelectedCategory))
        {
            UpdateSystemLayoutForSelectedCategory();
        }
    }
    
    public void SetThemeContent(Control themeRoot, Theme theme)
    {
        UnhookThemeTuning();

        // Ensure bindings in the theme root resolve to the BigModeViewModel
        themeRoot.DataContext = DataContext;

        _themePresenter.Content = themeRoot;

        // Reset previous SystemHost-specific content when switching away
        if (!_isSystemHostTheme && _systemLayoutHost != null)
        {
            _systemLayoutHost.Content = null;
        }
        
        // Detect whether this theme is the "System Host" theme by checking for
        // a named content placeholder "SystemLayoutHost" in the visual tree
        _systemLayoutHost = themeRoot.FindControl<ContentControl>("SystemLayoutHost");
        _isSystemHostTheme = _systemLayoutHost != null;

        var vm = DataContext as Retromind.ViewModels.BigModeViewModel;

        // System Host theme is category-first; avoid being stuck in game list mode.
        if (_isSystemHostTheme && vm != null)
        {
            if (vm.CurrentCategories.Count > 0)
            {
                if (vm.IsGameListActive)
                {
                    vm.IsGameListActive = false;
                    vm.Items = new ObservableCollection<MediaItem>();
                    vm.SelectedItem = null;
                }

                if (vm.SelectedCategory == null || !vm.CurrentCategories.Contains(vm.SelectedCategory))
                    vm.SelectedCategory = vm.CurrentCategories[0];
            }
        }

        // Theme-based video capability (no more slot-based shutdown)
        if (vm != null)
        {
            // Base capability from outer theme
            vm.CanShowVideo = theme.PrimaryVideoEnabled;
        }
        
        // Apply theme tuning (selection UX, spacing, typography, animation timings)
        ApplyThemeTuning(themeRoot);

        // Primary video slot (main preview).
        AttachPrimaryVideoToSlot(themeRoot, theme);

        // Secondary video slot (background / B-roll), if configured by the theme.
        AttachSecondaryVideoToSlot(themeRoot);
        
        // Initial PrimaryVisual animation for the newly set theme
        ThemeTransitionHelper.AnimatePrimaryVisual(themeRoot);

        // If this is the SystemHost theme, initialize the right-hand system layout
        // immediately for the current SelectedCategory
        if (_isSystemHostTheme)
        {
            UpdateSystemLayoutForSelectedCategory();
        }
        
        // Layout settles asynchronously; schedule VM "view ready" after render ticks
        NotifyViewReadyAfterRender(DataContext!);
    }

    /// <summary>
    /// Attaches the shared primary video control to the active theme's video slot.
    /// Uses Theme.PrimaryVideoEnabled and Theme.VideoSlotName to decide whether and where
    /// to place the control. This avoids creating multiple VideoSurfaceControls
    /// bound to the same MainVideoSurface.
    /// </summary>
    private void AttachPrimaryVideoToSlot(Control themeRoot, Theme theme)
    {
        // Only attach primary video when the theme explicitly enables it
        if (!theme.PrimaryVideoEnabled)
            return;

        if (DataContext is not Retromind.ViewModels.BigModeViewModel)
            return;

        var slotName = theme.VideoSlotName;
        if (string.IsNullOrWhiteSpace(slotName))
            return;

        var slot = themeRoot.FindControl<Control>(slotName);
        if (slot is null)
            return;

        DetachFromParent(_primaryVideoControl);
        AttachToSlot(slot, _primaryVideoControl);
    }

    /// <summary>
    /// Attaches the shared secondary video control to the active theme's
    /// secondary video slot (if any). The slot name is provided via
    /// ThemeProperties.SecondaryVideoSlotName on the theme root.
    /// </summary>
    private void AttachSecondaryVideoToSlot(Control themeRoot)
    {
        if (DataContext is not Retromind.ViewModels.BigModeViewModel)
            return;

        var slotName = ThemeProperties.GetSecondaryVideoSlotName(themeRoot);
        if (string.IsNullOrWhiteSpace(slotName))
            return;

        var slot = themeRoot.FindControl<Control>(slotName);
        if (slot is null)
            return;

        DetachFromParent(_secondaryVideoControl);
        AttachToSlot(slot, _secondaryVideoControl);
    }

    /// <summary>
    /// Helper to detach a control from its current parent, if any.
    /// Supports Panel, ContentControl and Decorator.
    /// </summary>
    private static void DetachFromParent(Control control)
    {
        var parent = control.Parent;
        switch (parent)
        {
            case Panel oldPanel:
                oldPanel.Children.Remove(control);
                break;
            case ContentControl oldContent:
                if (ReferenceEquals(oldContent.Content, control))
                    oldContent.Content = null;
                break;
            case Decorator decorator:
                if (ReferenceEquals(decorator.Child, control))
                    decorator.Child = null;
                break;
        }
    }

    /// <summary>
    /// Helper to attach a control to a generic slot. Supports Panel,
    /// ContentControl and Decorator (e.g. Border).
    /// </summary>
    private static void AttachToSlot(Control slot, Control child)
    {
        switch (slot)
        {
            case Panel panel:
                panel.Children.Clear();
                panel.Children.Add(child);
                break;
            case ContentControl cc:
                cc.Content = child;
                break;
            case Decorator decorator:
                decorator.Child = child;
                break;
        }
    }

    /// <summary>
    /// Updates the right-hand system layout (SystemLayoutHost) when the SystemHost
    /// theme is active. Selects and loads the per-system subtheme based on the
    /// current node's SystemPreviewThemeId.
    ///
    /// The Theme itself (metadata + cached XAML) is reused from _systemThemeCache,
    /// but a fresh view instance is created on each call via Theme.CreateView().
    /// This keeps category switching fast while avoiding "already has a visual parent"
    /// crashes from reusing the same UserControl instance.
    /// </summary>
    private void UpdateSystemLayoutForSelectedCategory()
    {
        if (!_isSystemHostTheme || _systemLayoutHost is null)
            return;

        if (DataContext is not Retromind.ViewModels.BigModeViewModel vm)
            return;

        var node = vm.SelectedCategory;
        if (node is null)
        {
            _systemLayoutHost.Content = null;
            vm.CanShowVideo = false;
            return;
        }

        // Resolve system preview theme id with a safe default
        // The id corresponds to a folder under Themes/System (e.g. "Default", "C64")
        var id = string.IsNullOrWhiteSpace(node.SystemPreviewThemeId)
            ? "Default"
            : node.SystemPreviewThemeId;
        
        Theme systemTheme;

        // Use cached Theme (with cached XAML and factory) when available
        if (!_systemThemeCache.TryGetValue(id, out systemTheme!))
        {
            try
            {
                var relativePath = System.IO.Path.Combine("System", id, "theme.axaml");
                systemTheme = ThemeLoader.LoadTheme(relativePath);
            }
            catch
            {
                // Try fallback to the "Default" system theme
                if (!string.Equals(id, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var fallbackPath = System.IO.Path.Combine("System", "Default", "theme.axaml");
                        systemTheme = ThemeLoader.LoadTheme(fallbackPath);
                    }
                    catch
                    {
                        _systemLayoutHost.Content = null;
                        vm.CanShowVideo = false;
                        return;
                    }
                }
                else
                {
                    _systemLayoutHost.Content = null;
                    vm.CanShowVideo = false;
                    return;
                }
            }

            _systemThemeCache[id] = systemTheme;
        }

        // Always create a fresh view instance for the system layout host.
        // The underlying XAML content is cached by ThemeLoader/XamlCache, so this
        // avoids both disk I/O and reusing a single control instance.
        var subView = systemTheme.CreateView();
        subView.DataContext = vm;

        _systemLayoutHost.Content = subView;

        ThemeTransitionHelper.AnimatePrimaryVisual(subView);

        // When we are inside the SystemHost theme, the per-system subtheme
        // can define its own primary video slot. Attach the shared primary
        // video control here so the system view uses the main channel.
        AttachPrimaryVideoToSlot(subView, systemTheme);

        // Attach the secondary video control to the system subtheme, if it exposes
        // a secondary video slot name (e.g. via SecondaryVideoSlotName)
        AttachSecondaryVideoToSlot(subView);
        
        // Update the VM flag so higher-level logic knows whether video can be shown.
        // For system view we allow video when either the outer host theme or the
        // per-system subtheme enables the primary channel.
        vm.CanShowVideo = vm.CanShowVideo || systemTheme.PrimaryVideoEnabled;
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

        var titleFontFamily = ThemeFontFamilyConverter.ResolveFontFamily(
            ThemeProperties.GetTitleFontFamily(themeRoot));
        var bodyFontFamily = ThemeFontFamilyConverter.ResolveFontFamily(
            ThemeProperties.GetBodyFontFamily(themeRoot));
        var captionFontFamily = ThemeFontFamilyConverter.ResolveFontFamily(
            ThemeProperties.GetCaptionFontFamily(themeRoot));
        var monoFontFamily = ThemeFontFamilyConverter.ResolveFontFamily(
            ThemeProperties.GetMonoFontFamily(themeRoot));

        var accent = ThemeProperties.GetAccentColor(themeRoot);

        var fadeDuration = TimeSpan.FromMilliseconds(Math.Clamp(fadeMs, 0, 5000));
        
        // Selection UX: apply to ALL ListBoxes in the theme visual tree.
        // We use ContainerPrepared + SelectionChanged so it works with virtualization.
        foreach (var lb in themeRoot.GetVisualDescendants())
        {
            if (lb is not ListBox listBox)
                continue;

            // Themes can explicitly disable the generic host effect.
            if (!ThemeProperties.GetUseHostSelectionEffects(listBox))
                continue;
            
            listBox.SelectionChanged += OnListBoxSelectionChanged;
            listBox.ContainerPrepared += OnListBoxContainerPrepared;
            _tunedListBoxes.Add(listBox);

            // Apply current selection state for realized containers.
            ApplySelectionVisuals(listBox, selectedScale, unselectedOpacity, selectedGlowOpacity, fadeMs, moveMs, accent);
        }

        // Spacing/Padding: opt-in via classes (theme authors can just add Classes="rm-panel"/"rm-header")
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

        // Typography defaults: opt-in via classes (rm-title/rm-body/rm-caption/rm-mono)
        foreach (var v in themeRoot.GetVisualDescendants())
        {
            if (v is not TextBlock tb)
                continue;

            if (tb.Classes.Contains("rm-title"))
            {
                tb.FontSize = titleFontSize;
                if (titleFontFamily != null)
                    tb.FontFamily = titleFontFamily;
            }
            else if (tb.Classes.Contains("rm-body"))
            {
                tb.FontSize = bodyFontSize;
                if (bodyFontFamily != null)
                    tb.FontFamily = bodyFontFamily;
            }
            else if (tb.Classes.Contains("rm-caption"))
            {
                tb.FontSize = captionFontSize;
                if (captionFontFamily != null)
                    tb.FontFamily = captionFontFamily;
            }
            else if (tb.Classes.Contains("rm-mono"))
            {
                if (monoFontFamily != null)
                    tb.FontFamily = monoFontFamily;
            }
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

    public async void NotifyViewReadyAfterRender(object viewModel)
    {
        // Wait a few render ticks so the theme is fully built
        // before LibVLC starts playback.
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);

        // After the initial layout, explicitly set keyboard focus into the theme content
        // so the first arrow key does not just move focus into the window.
        if (_themePresenter.Content is Control themeRoot)
        {
            // Prefer the first ListBox in the theme (main navigation).
            var focusTarget =
                themeRoot.GetVisualDescendants().OfType<ListBox>().FirstOrDefault()
                ?? themeRoot as IInputElement;

            focusTarget?.Focus();
        }
        if (viewModel is Retromind.ViewModels.BigModeViewModel vm)
            vm.NotifyViewReady();
    }
}
