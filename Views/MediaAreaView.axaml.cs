using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class MediaAreaView : UserControl
{
    private ListBox? _mediaList;

    public MediaAreaView()
    {
        InitializeComponent();
        
        _mediaList = this.FindControl<ListBox>("MediaList");
        if (_mediaList != null)
            _mediaList.SizeChanged += OnMediaListSizeChanged;

        // Ensure we run our scroll logic once the control is loaded.
        this.Loaded += OnLoadedOnce;
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // transfers the double click to the view model
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MediaAreaViewModel vm)
            if (vm.DoubleClickCommand.CanExecute(null))
                vm.DoubleClickCommand.Execute(null);
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MediaAreaViewModel vm)
            return;

        if (sender is not Control { DataContext: MediaItem item })
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        vm.SelectedMediaItem = item;
        e.Handled = true;
    }
    
    /// <summary>
    /// Called once when the control is loaded. Scrolls the current SelectedMediaItem
    /// into view so that the last-used item is visible after restoring a node
    /// </summary>
    private void OnLoadedOnce(object? sender, RoutedEventArgs e)
    {
        // We only need this once per view instance.
        this.Loaded -= OnLoadedOnce;

        // Defer to the UI thread after layout so that containers are created.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not MediaAreaViewModel vm)
                return;

            _mediaList ??= this.FindControl<ListBox>("MediaList");
            if (_mediaList is null)
                return;

            vm.ViewportWidth = _mediaList.Bounds.Width;

            if (vm.SelectedMediaItem is null)
                return;

            ScrollItemIntoView(vm.SelectedMediaItem);
        }, DispatcherPriority.Background);
    }

    private void OnMediaListSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MediaAreaViewModel vm)
        {
            _mediaList ??= this.FindControl<ListBox>("MediaList");
            if (_mediaList != null)
                vm.ViewportWidth = _mediaList.Bounds.Width;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MediaAreaViewModel vm)
        {
            _mediaList ??= this.FindControl<ListBox>("MediaList");
            if (_mediaList != null)
                vm.ViewportWidth = _mediaList.Bounds.Width;
        }
    }

    public void ScrollItemIntoView(MediaItem item)
    {
        if (DataContext is not MediaAreaViewModel vm)
            return;

        _mediaList ??= this.FindControl<ListBox>("MediaList");
        if (_mediaList is null)
            return;

        var row = vm.ItemRows.FirstOrDefault(r => r.Items.Contains(item));
        if (row == null)
            return;

        _mediaList.ScrollIntoView(row);
    }
}
