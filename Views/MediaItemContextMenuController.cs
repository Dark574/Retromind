using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Retromind.Models;
using Retromind.ViewModels;

namespace Retromind.Views;

// One lazy menu per view, never one menu per recycled tile. The ListBox remains
// the popup owner so the cached popup cannot retain a removed tile as its target.
internal sealed class MediaItemContextMenuController
{
    private readonly UserControl _view;
    private readonly ListBox _list;
    private Control? _target;
    private INotifyPropertyChanged? _selectionOwner;
    private MediaItemContextMenu? _menu;

    internal MediaItemContextMenuController(UserControl view, ListBox list)
    {
        _view = view;
        _list = list;
        // Handle keyboard requests before the list's empty-area menu. Pointer
        // requests continue to the tile's normal bubbling handler.
        list.AddHandler(InputElement.ContextRequestedEvent, OnListContextRequested, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.ContextCanceledEvent, (_, _) => Close(), RoutingStrategies.Tunnel);
        view.DetachedFromVisualTree += (_, _) => ReleaseMenu();
        view.DataContextChanged += (_, _) => ReleaseMenu();
    }

    internal MediaItemContextMenu? Menu => _menu;

    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.Handled && ReferenceEquals(e.Source, _list) && !e.TryGetPosition(_list, out _))
            Request(null, e);
    }

    internal void Request(Control? target, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsMultiSelectMode)
        {
            Close();
            return;
        }

        var item = target != null ? target.DataContext as MediaItem : SelectedItem;
        if (item == null || TopLevel.GetTopLevel(_view) is not Window { DataContext: MainWindowViewModel commands })
            return;

        target ??= (Control?)_list.GetVisualDescendants().OfType<Border>().FirstOrDefault(control =>
            control.Width > 0 && control.Height > 0 && control.IsEffectivelyVisible &&
            ReferenceEquals(control.DataContext, item) &&
            control.TranslatePoint(default, _list) is { } point &&
            new Rect(point, control.Bounds.Size).Intersects(new Rect(_list.Bounds.Size))) ?? _list;

        // Search also opens on pointer press. A subsequent context request must
        // not close and reopen the same popup on pointer release.
        if (_menu is { IsOpen: true } && ReferenceEquals(_target, target) &&
            _menu.DataContext is MediaItemMenuContext current && ReferenceEquals(current.Item, item))
            return;

        Select(item);
        _list.Focus();
        try
        {
            var menu = Prepare(target, new MediaItemMenuContext(commands, item,
                (_view.DataContext as MediaAreaViewModel)?.Node));
            var byPointer = e.TryGetPosition(_list, out _);
            menu.Placement = byPointer ? PlacementMode.Pointer : PlacementMode.Bottom;
            menu.PlacementRect = byPointer ? null : new Rect(target.TranslatePoint(default, _list) ?? default, target.Bounds.Size);
            menu.Open(_list);
        }
        catch
        {
            Close();
            throw;
        }
    }

    internal MediaItemContextMenu Prepare(Control target, MediaItemMenuContext context)
    {
        Close();
        _menu ??= CreateMenu();
        _target = target;
        target.DataContextChanged += TargetDataContextChanged;
        target.DetachedFromVisualTree += TargetDetached;
        _list.LayoutUpdated += TargetLayoutUpdated;
        _selectionOwner = _view.DataContext as INotifyPropertyChanged;
        if (_selectionOwner != null)
            _selectionOwner.PropertyChanged += SelectionChanged;
        _menu.DataContext = context;
        return _menu;
    }

    private MediaItemContextMenu CreateMenu()
    {
        var menu = new MediaItemContextMenu();
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu))
                ClearTarget();
        };
        return menu;
    }

    internal void Close()
    {
        _menu?.Close();
        ClearTarget();
    }

    private void ClearTarget()
    {
        _list.LayoutUpdated -= TargetLayoutUpdated;
        if (_selectionOwner != null)
        {
            _selectionOwner.PropertyChanged -= SelectionChanged;
            _selectionOwner = null;
        }
        if (_target != null)
        {
            _target.DataContextChanged -= TargetDataContextChanged;
            _target.DetachedFromVisualTree -= TargetDetached;
            _target = null;
        }
        if (_menu != null)
            _menu.DataContext = null;
    }

    private void ReleaseMenu()
    {
        Close();
        _menu = null;
    }

    private void TargetDataContextChanged(object? sender, EventArgs e) => Close();
    private void TargetDetached(object? sender, VisualTreeAttachmentEventArgs e) => Close();

    private void TargetLayoutUpdated(object? sender, EventArgs e)
    {
        // Virtualization may hide a retained row without detaching its tiles or
        // changing their DataContext. Such a row must not leave a live popup.
        if (_target is { } target && (!target.IsEffectivelyVisible ||
            target.TranslatePoint(default, _list) is not { } point ||
            !new Rect(point, target.Bounds.Size).Intersects(new Rect(_list.Bounds.Size))))
            Close();
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_menu?.DataContext is MediaItemMenuContext context &&
            (IsMultiSelectMode || !ReferenceEquals(SelectedItem, context.Item)))
            Close();
    }

    private bool IsMultiSelectMode => _view.DataContext switch
    {
        MediaAreaViewModel media => media.IsMultiSelectMode,
        SearchAreaViewModel search => search.IsMultiSelectMode,
        _ => true
    };

    private MediaItem? SelectedItem => _view.DataContext switch
    {
        MediaAreaViewModel media => media.SelectedMediaItem,
        SearchAreaViewModel search => search.SelectedMediaItem,
        _ => null
    };

    private void Select(MediaItem item)
    {
        switch (_view.DataContext)
        {
            case MediaAreaViewModel media: media.SelectedMediaItem = item; break;
            case SearchAreaViewModel search: search.SelectedMediaItem = item; break;
        }
    }
}
