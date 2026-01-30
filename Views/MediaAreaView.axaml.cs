using System;
using System.Collections.Generic;
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
        _mediaList?.Focus();
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

            if (vm.SelectedMediaItem != null)
            {
                Dispatcher.UIThread.Post(
                    () => ScrollItemIntoView(vm.SelectedMediaItem),
                    DispatcherPriority.Background);
            }
        }
    }

    private void OnMediaListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MediaAreaViewModel vm)
            return;

        var items = vm.FilteredItems;
        if (items.Count == 0)
            return;

        if (e.Key == Key.Enter)
        {
            if (vm.SelectedMediaItem != null && vm.DoubleClickCommand.CanExecute(null))
            {
                vm.DoubleClickCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        var columnCount = Math.Max(1, vm.ColumnCount);
        var selectedIndex = FindSelectedIndex(items, vm.SelectedMediaItem);
        var targetIndex = selectedIndex;

        switch (e.Key)
        {
            case Key.Left:
                targetIndex = selectedIndex <= 0 ? 0 : selectedIndex - 1;
                break;
            case Key.Right:
                targetIndex = selectedIndex < 0 ? 0 : Math.Min(selectedIndex + 1, items.Count - 1);
                break;
            case Key.Up:
                targetIndex = selectedIndex < 0 ? 0 : Math.Max(selectedIndex - columnCount, 0);
                break;
            case Key.Down:
                targetIndex = selectedIndex < 0 ? 0 : Math.Min(selectedIndex + columnCount, items.Count - 1);
                break;
            case Key.Home:
                targetIndex = 0;
                break;
            case Key.End:
                targetIndex = items.Count - 1;
                break;
            default:
                return;
        }

        var item = items[targetIndex];
        vm.SelectedMediaItem = item;
        ScrollItemIntoView(item);
        e.Handled = true;
    }

    private static int FindSelectedIndex(IList<MediaItem> items, MediaItem? selected)
    {
        if (selected == null)
            return -1;

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (ReferenceEquals(item, selected) ||
                string.Equals(item.Id, selected.Id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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
