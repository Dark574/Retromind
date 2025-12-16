using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Retromind.Services;

namespace Retromind.Views;

public partial class BigModeHostView : UserControl
{
    private ContentPresenter _themePresenter = null!;
    private Border _videoBorder = null!;

    private Control? _videoSlot;

    // Coalesce LayoutUpdated spam into a single placement calculation per UI tick
    private bool _placementUpdateQueued;
    
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
        // Ensure bindings in the theme root resolve to the BigModeViewModel
        themeRoot.DataContext = DataContext;
        
        _themePresenter.Content = themeRoot;

        // Convention: the theme defines one element named "VideoSlot"
        _videoSlot = themeRoot.FindControl<Control>("VideoSlot");

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
        
        // Layout settles asynchronously; schedule placement + VM "view ready" after render ticks
        QueueUpdateVideoPlacement();
        NotifyViewReadyAfterRender(DataContext!);
    }

    private void QueueUpdateVideoPlacement()
    {
        if (_placementUpdateQueued)
            return;

        _placementUpdateQueued = true;

        Dispatcher.UIThread.Post(() =>
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
        // Two render ticks as settle time for XWayland/VLC embedding.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);

        if (viewModel is Retromind.ViewModels.BigModeViewModel vm)
            vm.NotifyViewReady();
    }
}