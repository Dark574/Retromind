using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

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

    public void SetThemeContent(Control themeRoot)
    {
        // Ensure bindings in the theme root resolve to the BigModeViewModel
        themeRoot.DataContext = DataContext;
        
        _themePresenter.Content = themeRoot;

        // Konvention: Theme definiert EIN Element mit x:Name="VideoSlot"
        _videoSlot = themeRoot.FindControl<Control>("VideoSlot");

        // Determine capability: theme says video enabled AND a slot exists
        var themeAllowsVideo = Retromind.Extensions.ThemeProperties.GetVideoEnabled(themeRoot);
        var hasSlot = _videoSlot != null;

        // Bei Theme-Wechsel lieber einmal “zurücksetzen”, damit nichts “hängen bleibt”
        ResetVideoBorder();
        
        // Theme capability -> VM informieren
        if (DataContext is Retromind.ViewModels.BigModeViewModel vm)
        {
            vm.CanShowVideo = _videoSlot != null;

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
        
        // Slot muss im VisualTree sein, sonst keine Koordinaten
        var topLeft = _videoSlot.TranslatePoint(new Point(0, 0), this);
        if (topLeft == null)
        {
            ResetVideoBorder();
            return;
        }
        
        var bounds = _videoSlot.Bounds;

        // Wenn Slot noch keine sinnvollen Bounds hat: Overlay aus (sonst full-screen/komisch)
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
        // 2 Render-Ticks als „settle time“ für XWayland/VLC-Embedding
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);

        // VM-Typ nicht hart referenzieren müssen wir nicht; aber hier ist es ok:
        if (viewModel is Retromind.ViewModels.BigModeViewModel vm)
            vm.NotifyViewReady();
    }
}