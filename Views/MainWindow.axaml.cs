using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Retromind.Helpers;
using Retromind.ViewModels;

namespace Retromind;

public partial class MainWindow : Window
{
    // Flag to check if we have already performed the final save/cleanup.
    private bool _canClose = false;
    
    // Prevents re-entry (e.g., duplicate Close/Alt+F4 while Flush is still running).
    private bool _closeInProgress = false;
    
    public MainWindow()
    {
        InitializeComponent();
    }

    // Override the method that is called when the window is closing.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        // If we already saved and cleaned up (2nd pass), allow the window to close.
        if (_canClose)
        {
            base.OnClosing(e);
            return;
        }

        // If a close/flush is already in progress, keep the window from closing.
        if (_closeInProgress)
        {
            e.Cancel = true;
            return;
        }

        // 1st pass: cancel closing and start the flush/cleanup sequence.
        e.Cancel = true;
        _closeInProgress = true;

        // Hide ASAP to avoid the "window pops back up" effect while the close is cancelled.
        // Minimize is usually less glitchy than IsVisible=false on some compositors.
        try
        {
            IsEnabled = false;
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
        }
        catch
        {
            // Best-effort; never block closing because of UI state issues.
        }

        // Try to get the ViewModel.
        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                await vm.FlushAndCleanupAsync();
            }
            catch
            {
                // Best effort: if saving crashes, still continue and close the app.
            }
        }

        // Now mark that the next closing attempt may proceed.
        _canClose = true;

        // Important to avoid "pop back up":
        // Do not call Close() inline, but post it to the UI queue.
        UiThreadHelper.Post(() =>
        {
            try
            {
                Close();
            }
            catch
            {
                // Ignore: we are shutting down anyway.
            }
        }, DispatcherPriority.Background);
    }

    // Event handler for dragging the custom title bar.
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Distinguish between click and drag.
        // Double-click toggles maximize/restore.
        if (e.ClickCount == 2)
            ToggleWindowState();
        else
            BeginMoveDrag(e);
    }

    public void MinimizeWindow()
    {
        WindowState = WindowState.Minimized;
    }

    public void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void CloseWindow()
    {
        Close();
    }

    // Resizing logic for custom resize grips.
    private void OnResizeDrag(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.Tag is WindowEdge edge) BeginResizeDrag(edge, e);
    }
    
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // If a controller sends Guide + a synthetic Escape/Back, ignore the key for a short time window.
        if ((e.Key == Key.Escape || e.Key == Key.Back) && vm.ShouldIgnoreBackKeyTemporarily())
        {
            e.Handled = true;
            return;
        }
        
        // Try to locate the BigModeViewModel.
        // It can either be the FullScreenContent directly (ViewModel-first)
        // OR be the DataContext of a control (View-first).
        BigModeViewModel? bigVm = null;

        if (vm.FullScreenContent is BigModeViewModel directVm)
        {
            // Case 1: the content IS the view model.
            bigVm = directVm;
        }
        else if (vm.FullScreenContent is Control { DataContext: BigModeViewModel contextVm })
        {
            // Case 2: the content is a control that holds the view model.
            bigVm = contextVm;
        }

        // If we found the view model, forward the key to its commands.
        if (bigVm != null)
        {
            switch (e.Key)
            {
                case Key.Up:
                    bigVm.SelectPreviousCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Down:
                    bigVm.SelectNextCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    bigVm.PlayCurrentCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                case Key.Back:
                    bigVm.ExitBigModeCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}