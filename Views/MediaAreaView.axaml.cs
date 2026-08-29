using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class MediaAreaView : UserControl
{
    private const double DragThreshold = 6.0;

    private ListBox? _mediaList;
    private MediaItem? _draggedItem;
    private Point? _dragStartPoint;
    private PointerPressedEventArgs? _dragStartPressedEvent;
    private bool _dragInProgress;

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

        _draggedItem = item;
        _dragStartPoint = e.GetPosition(this);
        _dragStartPressedEvent = e;
        e.Handled = true;
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragInProgress || _draggedItem == null || _dragStartPoint == null || _dragStartPressedEvent == null)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetItemDragState();
            return;
        }

        var currentPosition = e.GetPosition(this);
        var delta = currentPosition - _dragStartPoint.Value;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        var window = this.FindAncestorOfType<MainWindow>();
        if (window == null)
        {
            ResetItemDragState();
            return;
        }

        _dragInProgress = true;
        try
        {
            await window.BeginMediaItemDragAsync(_draggedItem, _dragStartPressedEvent);
        }
        finally
        {
            ResetItemDragState();
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragInProgress)
            ResetItemDragState();
    }

    private void ResetItemDragState()
    {
        _draggedItem = null;
        _dragStartPoint = null;
        _dragStartPressedEvent = null;
        _dragInProgress = false;
    }
    
    /// <summary>
    /// Called once when the control is loaded to initialize viewport-related layout state.
    /// </summary>
    private void OnLoadedOnce(object? sender, RoutedEventArgs e)
    {
        // We only need this once per view instance.
        this.Loaded -= OnLoadedOnce;

        if (DataContext is not MediaAreaViewModel vm)
            return;

        _mediaList ??= this.FindControl<ListBox>("MediaList");
        if (_mediaList is null)
            return;

        vm.ViewportWidth = _mediaList.Bounds.Width;
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
                    DispatcherPriority.Loaded);
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

        var selectedIndex = MediaGridNavigationHelper.FindSelectedIndex(items, vm.SelectedMediaItem);
        if (!MediaGridNavigationHelper.TryGetTargetIndex(
                e.Key,
                selectedIndex,
                items.Count,
                vm.ColumnCount,
                out var targetIndex))
        {
            return;
        }

        var item = items[targetIndex];
        vm.SelectedMediaItem = item;
        ScrollItemIntoView(item);
        e.Handled = true;
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

    private async void OnOpenFilterBuilderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MediaAreaViewModel vm)
            return;

        var owner = this.FindAncestorOfType<Window>() ?? TopLevel.GetTopLevel(this) as Window;
        var builderData = await BuildBuilderDataAsync(vm);
        var builderVm = new SearchQueryBuilderViewModel(builderData.Fields, builderData.SuggestionsByField);
        var dialog = new SearchQueryBuilderDialogView { DataContext = builderVm };

        builderVm.RequestApply += result =>
            vm.SearchText = SearchQueryBuilderHelper.ApplyTokenToSearch(
                vm.SearchText,
                result.Token,
                result.ReplaceSearch,
                result.JoinOperator);
        builderVm.RequestClearSearch += () => vm.SearchText = string.Empty;

        if (owner == null)
        {
            dialog.Show();
            return;
        }

        await dialog.ShowDialog(owner);
    }

    private static Task<SearchQueryBuilderData> BuildBuilderDataAsync(MediaAreaViewModel vm)
    {
        var snapshot = vm.Node.Items.ToList();
        return Task.Run(() => SearchQueryBuilderHelper.BuildData(snapshot));
    }
}
