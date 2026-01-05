using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Helper behaviors for ListBox controls (e.g. centering the selected item)
/// </summary>
public static class ListBoxBehaviors
{
    /// <summary>
    /// When set to true on a ListBox, automatically scrolls the selected item
    /// into view and tries to keep it vertically centered in the viewport.
    /// Useful for "wheel" style lists where the focused item should always
    /// be in the middle of the visible area
    /// </summary>
    public static readonly AttachedProperty<bool> CenterSelectedItemProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "CenterSelectedItem",
            typeof(ListBoxBehaviors));

    public static void SetCenterSelectedItem(AvaloniaObject element, bool value)
        => element.SetValue(CenterSelectedItemProperty, value);

    public static bool GetCenterSelectedItem(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemProperty);

    /// <summary>
    /// When set to true on a ListBox, centers the currently selected item
    /// exactly once on the first SelectionChanged event that has a valid selection.
    /// This is intended for "restore last selection on load" scenarios where we
    /// want the last selected item to appear in the middle of the viewport after
    /// loading, but do not want to re-center on every subsequent user selection
    /// </summary>
    public static readonly AttachedProperty<bool> CenterSelectedItemOnceOnFirstSelectionProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "CenterSelectedItemOnceOnFirstSelection",
            typeof(ListBoxBehaviors));

    // IMPORTANT: XAML needs matching Get*/Set* helpers for the attached property name
    public static void SetCenterSelectedItemOnceOnFirstSelection(AvaloniaObject element, bool value)
        => element.SetValue(CenterSelectedItemOnceOnFirstSelectionProperty, value);

    public static bool GetCenterSelectedItemOnceOnFirstSelection(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemOnceOnFirstSelectionProperty);
    
    // Internal flag to remember whether we already centered once for a given ListBox.
    private static readonly AttachedProperty<bool> HasCenteredOnceProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "HasCenteredOnce",
            typeof(ListBoxBehaviors));

    private static void SetHasCenteredOnce(AvaloniaObject element, bool value)
        => element.SetValue(HasCenteredOnceProperty, value);

    private static bool GetHasCenteredOnce(AvaloniaObject element)
        => element.GetValue(HasCenteredOnceProperty);
    
    static ListBoxBehaviors()
    {
        // React when the attached property changes on any ListBox instance
        CenterSelectedItemProperty.Changed.AddClassHandler<ListBox>((listBox, e) =>
        {
            // NewValue is boxed -> cast to bool
            var enable = (bool)e.NewValue!;

            if (enable)
            {
                listBox.SelectionChanged += OnListBoxSelectionChanged;
            }
            else
            {
                listBox.SelectionChanged -= OnListBoxSelectionChanged;
            }
        });
        
        // One-shot centering: attach/detach a different handler
        CenterSelectedItemOnceOnFirstSelectionProperty.Changed.AddClassHandler<ListBox>((listBox, e) =>
        {
            var enable = (bool)e.NewValue!;

            if (enable)
            {
                // Reset flag whenever the behavior is (re)enabled
                SetHasCenteredOnce(listBox, false);
                listBox.SelectionChanged += OnListBoxSelectionChangedOnce;
            }
            else
            {
                listBox.SelectionChanged -= OnListBoxSelectionChangedOnce;
            }
        });
    }

    private static void OnListBoxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        // Delay the centering until after layout has updated; otherwise
        // container positions and viewport size may be outdated.
        Dispatcher.UIThread.Post(() => CenterCurrentSelection(listBox), DispatcherPriority.Background);
    }

    private static void OnListBoxSelectionChangedOnce(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        // If we already centered once, detach and ignore further events
        if (GetHasCenteredOnce(listBox))
        {
            listBox.SelectionChanged -= OnListBoxSelectionChangedOnce;
            return;
        }

        if (listBox.SelectedItem == null)
            return;

        // Mark as done and schedule centering once
        SetHasCenteredOnce(listBox, true);

        Dispatcher.UIThread.Post(() =>
        {
            CenterCurrentSelection(listBox);
            // After the first successful centering, detach the handler
            listBox.SelectionChanged -= OnListBoxSelectionChangedOnce;
        }, DispatcherPriority.Background);
    }
    
    private static void CenterCurrentSelection(ListBox listBox)
    {
        if (listBox.SelectedItem == null)
            return;

        // Try to find the container (ListBoxItem) for the selected item
        if (listBox.ContainerFromItem(listBox.SelectedItem) is not Control container)
            return;

        // Find the ScrollViewer inside the ListBox visual tree
        var scrollViewer = listBox
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        if (scrollViewer == null)
            return;

        // Transform the item's top-left into the ScrollViewer's coordinate space
        var p = container.TranslatePoint(new Point(0, 0), scrollViewer);
        if (p == null)
            return;

        var itemTopLeft = p.Value;

        // We want the item to be vertically centered:
        // offsetY = currentItemTop - (viewportHeight - itemHeight) / 2
        var itemHeight = container.Bounds.Height;
        var viewportHeight = scrollViewer.Viewport.Height;

        if (viewportHeight <= 0 || itemHeight <= 0)
            return;

        var desiredOffsetY = itemTopLeft.Y - (viewportHeight - itemHeight) / 2.0;

        // Clamp to valid range (no negative offsets, no scrolling past content end)
        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - viewportHeight);
        var clampedOffsetY = Math.Max(0, Math.Min(desiredOffsetY, maxOffsetY));

        var currentOffset = scrollViewer.Offset;
        var newOffset = new Vector(currentOffset.X, clampedOffsetY);

        scrollViewer.Offset = newOffset;
    }
}