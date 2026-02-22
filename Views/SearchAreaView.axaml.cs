using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

public partial class SearchAreaView : UserControl
{
    private SearchAreaViewModel? _currentViewModel;
    private ListBox? _resultsList;

    public SearchAreaView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        _resultsList = this.FindControl<ListBox>("ResultsList");
        if (_resultsList != null)
            _resultsList.SizeChanged += OnResultsSizeChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentViewModel != null && !ReferenceEquals(_currentViewModel, DataContext))
            _currentViewModel.Dispose();

        _currentViewModel = DataContext as SearchAreaViewModel;
        if (_currentViewModel != null)
        {
            _resultsList ??= this.FindControl<ListBox>("ResultsList");
            if (_resultsList != null)
                _currentViewModel.ViewportWidth = _resultsList.Bounds.Width;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _currentViewModel?.Dispose();
        _currentViewModel = null;
    }

    // Triggered by XAML "DoubleTapped" on a result tile.
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not SearchAreaViewModel vm)
            return;

        // The Border's DataContext is the bound item (MediaItem).
        if (sender is not Control { DataContext: MediaItem item })
            return;

        // Keep selection in sync (useful for the detail panel on the right).
        vm.SelectedMediaItem = item;

        // Execute play request via command (keeps the ViewModel as the single source of truth).
        if (vm.PlayCommand.CanExecute(item))
            vm.PlayCommand.Execute(item);
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not SearchAreaViewModel vm)
            return;

        if (sender is not Control { DataContext: MediaItem item })
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        vm.SelectedMediaItem = item;
        _resultsList?.Focus();
        e.Handled = true;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SearchAreaViewModel vm)
        {
            _resultsList ??= this.FindControl<ListBox>("ResultsList");
            if (_resultsList != null)
                vm.ViewportWidth = _resultsList.Bounds.Width;
        }
    }

    private void OnResultsSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is SearchAreaViewModel vm)
        {
            _resultsList ??= this.FindControl<ListBox>("ResultsList");
            if (_resultsList != null)
                vm.ViewportWidth = _resultsList.Bounds.Width;
        }
    }

    private void OnResultsListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SearchAreaViewModel vm)
            return;

        var items = vm.SearchResults;
        if (items.Count == 0)
            return;

        if (e.Key == Key.Enter)
        {
            var item = vm.SelectedMediaItem;
            if (item != null && vm.PlayCommand.CanExecute(item))
            {
                vm.PlayCommand.Execute(item);
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

        var next = items[targetIndex];
        vm.SelectedMediaItem = next;
        ScrollItemIntoView(next);
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

    private void ScrollItemIntoView(MediaItem item)
    {
        if (DataContext is not SearchAreaViewModel vm)
            return;

        _resultsList ??= this.FindControl<ListBox>("ResultsList");
        if (_resultsList is null)
            return;

        var row = vm.ItemRows.FirstOrDefault(r => r.Items.Contains(item));
        if (row == null)
            return;

        _resultsList.ScrollIntoView(row);
    }

    private async void OnEditScopesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SearchAreaViewModel vm)
            return;

        var owner = this.FindAncestorOfType<Window>() ?? TopLevel.GetTopLevel(this) as Window;
        var dialogVm = new SearchScopeDialogViewModel(vm.RootNodesSnapshot, vm.GetSelectedScopeIdsSnapshot());
        var dialog = new SearchScopeDialogView { DataContext = dialogVm };

        dialogVm.RequestClose += result => dialog.Close(result);

        if (owner == null)
        {
            dialog.Show();
            return;
        }

        var confirmed = await dialog.ShowDialog<bool>(owner);
        if (confirmed)
            vm.ApplyScopeSelection(dialogVm.GetSelectedNodeIds());
    }
}
