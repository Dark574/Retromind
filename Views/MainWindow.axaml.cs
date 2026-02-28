using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Views;
using Retromind.ViewModels;

namespace Retromind;

public partial class MainWindow : Window
{
    private const double DragThreshold = 6.0;
    private const string DropBeforeClass = "drop-before";
    private const string DropAfterClass = "drop-after";
    private const string DropInsideClass = "drop-inside";

    // Flag to check if we have already performed the final save/cleanup.
    private bool _canClose = false;
    
    // Prevents re-entry (e.g., duplicate Close/Alt+F4 while Flush is still running).
    private bool _closeInProgress = false;

    private MediaNode? _draggedNode;
    private Point? _dragStartPoint;
    private bool _dragInProgress;
    private Control? _dropIndicatorTarget;
    private MainWindowViewModel.NodeDropPosition? _dropIndicatorPosition;

    private static readonly TimeSpan TypeSearchResetDelay = TimeSpan.FromMilliseconds(800);
    private string _typeSearchBuffer = string.Empty;
    private DateTime _typeSearchLastUtc = DateTime.MinValue;
    
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
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

    private void OnTreeNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (control.DataContext is not MediaNode node)
            return;

        _draggedNode = node;
        _dragStartPoint = e.GetPosition(this);
    }

    private async void OnTreeNodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragInProgress || _draggedNode == null || _dragStartPoint == null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var currentPos = e.GetPosition(this);
        var delta = currentPos - _dragStartPoint.Value;

        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _dragInProgress = true;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(_draggedNode.Id));

        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ResetDragState();
        }
    }

    private void OnTreeNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragInProgress)
            return;

        ResetDragState();
    }

    private void OnTreeNodeDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Control targetControl)
            return;

        if (_draggedNode == null)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        if (targetControl.DataContext is not MediaNode targetNode)
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        var sourceNode = _draggedNode;
        if (sourceNode == null || ReferenceEquals(sourceNode, targetNode))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        if (IsDescendant(sourceNode, targetNode))
        {
            e.DragEffects = DragDropEffects.None;
            ClearDropIndicator();
            return;
        }

        var dropPosition = GetDropPosition(targetControl, e);
        ApplyDropIndicator(targetControl, dropPosition);

        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnTreeNodeDragLeave(object? sender, DragEventArgs e)
    {
        if (sender == _dropIndicatorTarget)
            ClearDropIndicator();
    }

    private async void OnTreeNodeDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control targetControl)
            return;

        if (_draggedNode == null)
            return;

        if (targetControl.DataContext is not MediaNode targetNode)
            return;

        var sourceNode = _draggedNode;
        if (sourceNode == null || ReferenceEquals(sourceNode, targetNode))
            return;

        if (IsDescendant(sourceNode, targetNode))
            return;

        if (DataContext is not MainWindowViewModel vm)
            return;

        var dropPosition = GetDropPosition(targetControl, e);
        await vm.TryMoveNodeAsync(sourceNode, targetNode, dropPosition);
        ClearDropIndicator();
    }

    private static MainWindowViewModel.NodeDropPosition GetDropPosition(Control targetControl, DragEventArgs e)
    {
        var height = targetControl.Bounds.Height;
        if (height <= 0)
            return MainWindowViewModel.NodeDropPosition.Inside;

        var position = e.GetPosition(targetControl);
        var upperBand = height * 0.25;
        var lowerBand = height * 0.75;

        if (position.Y <= upperBand)
            return MainWindowViewModel.NodeDropPosition.Before;

        if (position.Y >= lowerBand)
            return MainWindowViewModel.NodeDropPosition.After;

        return MainWindowViewModel.NodeDropPosition.Inside;
    }

    private static bool IsDescendant(MediaNode parent, MediaNode potentialChild)
    {
        foreach (var child in parent.Children)
        {
            if (ReferenceEquals(child, potentialChild))
                return true;

            if (IsDescendant(child, potentialChild))
                return true;
        }

        return false;
    }

    private void ResetDragState()
    {
        _draggedNode = null;
        _dragStartPoint = null;
        _dragInProgress = false;
        ClearDropIndicator();
    }

    private void ApplyDropIndicator(Control target, MainWindowViewModel.NodeDropPosition position)
    {
        if (_dropIndicatorTarget != null && !ReferenceEquals(_dropIndicatorTarget, target))
            ClearDropIndicator();

        if (ReferenceEquals(_dropIndicatorTarget, target) && _dropIndicatorPosition == position)
            return;

        ClearDropIndicator();

        _dropIndicatorTarget = target;
        _dropIndicatorPosition = position;

        var className = position switch
        {
            MainWindowViewModel.NodeDropPosition.Before => DropBeforeClass,
            MainWindowViewModel.NodeDropPosition.After => DropAfterClass,
            _ => DropInsideClass
        };

        target.Classes.Add(className);
    }

    private void ClearDropIndicator()
    {
        if (_dropIndicatorTarget == null)
            return;

        _dropIndicatorTarget.Classes.Remove(DropBeforeClass);
        _dropIndicatorTarget.Classes.Remove(DropAfterClass);
        _dropIndicatorTarget.Classes.Remove(DropInsideClass);
        _dropIndicatorTarget = null;
        _dropIndicatorPosition = null;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        if (vm.FullScreenContent != null)
            return;

        if (IsTextInputFocused())
            return;

        var input = e.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
            return;

        if (vm.SelectedNodeContent is not MediaAreaViewModel mediaVm)
            return;

        if (mediaVm.FilteredItems.Count == 0)
            return;

        var now = DateTime.UtcNow;
        if (now - _typeSearchLastUtc > TypeSearchResetDelay)
            _typeSearchBuffer = input;
        else
            _typeSearchBuffer += input;

        _typeSearchLastUtc = now;

        if (!TrySelectMediaByPrefix(mediaVm, _typeSearchBuffer) && _typeSearchBuffer.Length > input.Length)
        {
            _typeSearchBuffer = input;
            TrySelectMediaByPrefix(mediaVm, _typeSearchBuffer);
        }

        e.Handled = true;
    }

    private bool TrySelectMediaByPrefix(MediaAreaViewModel mediaVm, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return false;

        MediaItem? match = null;
        for (var i = 0; i < mediaVm.FilteredItems.Count; i++)
        {
            var item = mediaVm.FilteredItems[i];
            var title = item.Title;

            if (string.IsNullOrWhiteSpace(title))
                continue;

            if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                match = item;
                break;
            }
        }

        if (match == null)
            return false;

        mediaVm.SelectedMediaItem = match;
        ScrollMediaItemIntoView(mediaVm, match);
        return true;
    }

    private void ScrollMediaItemIntoView(MediaAreaViewModel mediaVm, MediaItem item)
    {
        var mediaView = this.GetVisualDescendants()
            .OfType<MediaAreaView>()
            .FirstOrDefault(view => ReferenceEquals(view.DataContext, mediaVm));

        if (mediaView == null)
            return;

        Dispatcher.UIThread.Post(
            () => mediaView.ScrollItemIntoView(item),
            DispatcherPriority.Background);
    }

    private bool IsTextInputFocused()
    {
        var focusManager = TopLevel.GetTopLevel(this)?.FocusManager;
        var focused = focusManager?.GetFocusedElement();

        if (focused is TextBox)
            return true;

        if (focused is Visual visual)
            return visual.GetVisualAncestors().OfType<TextBox>().Any();

        return false;
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
        var bigVm = ResolveBigModeViewModel(vm);

        // If we found the view model, forward the key to its commands.
        if (bigVm != null)
        {
            switch (e.Key)
            {
                case Key.Up:
                    bigVm.NotifyKeyboardScrollStart();
                    bigVm.SelectPreviousCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Down:
                    bigVm.NotifyKeyboardScrollStart();
                    bigVm.SelectNextCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    bigVm.PlayCurrentCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Space:
                    bigVm.PlayCurrentCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    // ESC: always exit BigMode completely.
                    bigVm.HardExitBigModeCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Back:
                    bigVm.ExitBigModeCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var bigVm = ResolveBigModeViewModel(vm);
        if (bigVm == null)
            return;

        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                bigVm.NotifyKeyboardScrollEnd();
                e.Handled = true;
                break;
        }
    }

    private static BigModeViewModel? ResolveBigModeViewModel(MainWindowViewModel vm)
    {
        if (vm.FullScreenContent is BigModeViewModel directVm)
            return directVm;

        if (vm.FullScreenContent is Control { DataContext: BigModeViewModel contextVm })
            return contextVm;

        return null;
    }
}
