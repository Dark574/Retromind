using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Helper behaviors for ListBox controls (e.g. centering the selected item)
/// </summary>
public static class ListBoxBehaviors
{
    private sealed class ActionObserver<T> : IObserver<T>
    {
        private readonly Action<T> _onNext;

        public ActionObserver(Action<T> onNext)
        {
            _onNext = onNext ?? throw new ArgumentNullException(nameof(onNext));
        }

        public void OnNext(T value) => _onNext(value);

        public void OnError(Exception error)
        {
            // best-effort: selection changes should never throw
        }

        public void OnCompleted()
        {
            // no-op
        }
    }

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
    /// When set to true on a ListBox, automatically scrolls the selected item
    /// into view and tries to keep it horizontally centered in the viewport.
    /// Useful for cover rows where the focused item should stay centered.
    /// </summary>
    public static readonly AttachedProperty<bool> CenterSelectedItemHorizontallyProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "CenterSelectedItemHorizontally",
            typeof(ListBoxBehaviors));

    public static void SetCenterSelectedItemHorizontally(AvaloniaObject element, bool value)
        => element.SetValue(CenterSelectedItemHorizontallyProperty, value);

    public static bool GetCenterSelectedItemHorizontally(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemHorizontallyProperty);

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

    private static readonly AttachedProperty<IDisposable?> CenterSelectedItemSubscriptionProperty =
        AvaloniaProperty.RegisterAttached<ListBox, IDisposable?>(
            "CenterSelectedItemSubscription",
            typeof(ListBoxBehaviors));

    private static void SetCenterSelectedItemSubscription(AvaloniaObject element, IDisposable? value)
        => element.SetValue(CenterSelectedItemSubscriptionProperty, value);

    private static IDisposable? GetCenterSelectedItemSubscription(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemSubscriptionProperty);

    private static readonly AttachedProperty<IDisposable?> CenterSelectedItemHorizontalSubscriptionProperty =
        AvaloniaProperty.RegisterAttached<ListBox, IDisposable?>(
            "CenterSelectedItemHorizontalSubscription",
            typeof(ListBoxBehaviors));

    private static void SetCenterSelectedItemHorizontalSubscription(AvaloniaObject element, IDisposable? value)
        => element.SetValue(CenterSelectedItemHorizontalSubscriptionProperty, value);

    private static IDisposable? GetCenterSelectedItemHorizontalSubscription(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemHorizontalSubscriptionProperty);

    public static readonly AttachedProperty<bool> CenterSelectedItemHorizontallyOnceOnFirstSelectionProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "CenterSelectedItemHorizontallyOnceOnFirstSelection",
            typeof(ListBoxBehaviors));

    public static void SetCenterSelectedItemHorizontallyOnceOnFirstSelection(AvaloniaObject element, bool value)
        => element.SetValue(CenterSelectedItemHorizontallyOnceOnFirstSelectionProperty, value);

    public static bool GetCenterSelectedItemHorizontallyOnceOnFirstSelection(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemHorizontallyOnceOnFirstSelectionProperty);

    private static readonly AttachedProperty<IDisposable?> CenterSelectedItemHorizontalOnceSubscriptionProperty =
        AvaloniaProperty.RegisterAttached<ListBox, IDisposable?>(
            "CenterSelectedItemHorizontalOnceSubscription",
            typeof(ListBoxBehaviors));

    private static void SetCenterSelectedItemHorizontalOnceSubscription(AvaloniaObject element, IDisposable? value)
        => element.SetValue(CenterSelectedItemHorizontalOnceSubscriptionProperty, value);

    private static IDisposable? GetCenterSelectedItemHorizontalOnceSubscription(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemHorizontalOnceSubscriptionProperty);

    private static readonly AttachedProperty<bool> HasCenteredOnceHorizontalProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "HasCenteredOnceHorizontal",
            typeof(ListBoxBehaviors));

    private static void SetHasCenteredOnceHorizontal(AvaloniaObject element, bool value)
        => element.SetValue(HasCenteredOnceHorizontalProperty, value);

    private static bool GetHasCenteredOnceHorizontal(AvaloniaObject element)
        => element.GetValue(HasCenteredOnceHorizontalProperty);

    private static readonly AttachedProperty<IDisposable?> CenterSelectedItemOnceSubscriptionProperty =
        AvaloniaProperty.RegisterAttached<ListBox, IDisposable?>(
            "CenterSelectedItemOnceSubscription",
            typeof(ListBoxBehaviors));

    private static void SetCenterSelectedItemOnceSubscription(AvaloniaObject element, IDisposable? value)
        => element.SetValue(CenterSelectedItemOnceSubscriptionProperty, value);

    private static IDisposable? GetCenterSelectedItemOnceSubscription(AvaloniaObject element)
        => element.GetValue(CenterSelectedItemOnceSubscriptionProperty);
    
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

            var existing = GetCenterSelectedItemSubscription(listBox);
            existing?.Dispose();
            SetCenterSelectedItemSubscription(listBox, null);

            if (enable)
            {
                var subscription = listBox
                    .GetObservable(SelectingItemsControl.SelectedItemProperty)
                    .Subscribe(new ActionObserver<object?>(_ => QueueCenterCurrentSelection(listBox)));

                SetCenterSelectedItemSubscription(listBox, subscription);
                QueueCenterCurrentSelection(listBox);
            }
        });

        CenterSelectedItemHorizontallyProperty.Changed.AddClassHandler<ListBox>((listBox, e) =>
        {
            var enable = (bool)e.NewValue!;

            var existing = GetCenterSelectedItemHorizontalSubscription(listBox);
            existing?.Dispose();
            SetCenterSelectedItemHorizontalSubscription(listBox, null);

            if (enable)
            {
                var subscription = listBox
                    .GetObservable(SelectingItemsControl.SelectedItemProperty)
                    .Subscribe(new ActionObserver<object?>(_ => QueueCenterCurrentSelectionHorizontal(listBox)));

                SetCenterSelectedItemHorizontalSubscription(listBox, subscription);
                QueueCenterCurrentSelectionHorizontal(listBox);
            }
        });

        CenterSelectedItemHorizontallyOnceOnFirstSelectionProperty.Changed.AddClassHandler<ListBox>((listBox, e) =>
        {
            var enable = (bool)e.NewValue!;

            var existing = GetCenterSelectedItemHorizontalOnceSubscription(listBox);
            existing?.Dispose();
            SetCenterSelectedItemHorizontalOnceSubscription(listBox, null);

            if (enable)
            {
                SetHasCenteredOnceHorizontal(listBox, false);
                QueueCenterCurrentSelectionHorizontalStabilized(listBox);

                IDisposable? subscription = null;
                subscription = listBox
                    .GetObservable(SelectingItemsControl.SelectedItemProperty)
                    .Subscribe(new ActionObserver<object?>(_ =>
                    {
                        if (GetHasCenteredOnceHorizontal(listBox))
                            return;

                        if (listBox.SelectedItem == null)
                            return;

                        SetHasCenteredOnceHorizontal(listBox, true);
                        QueueCenterCurrentSelectionHorizontalStabilized(listBox);

                        subscription?.Dispose();
                        SetCenterSelectedItemHorizontalOnceSubscription(listBox, null);
                    }));

                SetCenterSelectedItemHorizontalOnceSubscription(listBox, subscription);
            }
        });
        
        // One-shot centering: attach/detach a different handler
        CenterSelectedItemOnceOnFirstSelectionProperty.Changed.AddClassHandler<ListBox>((listBox, e) =>
        {
            var enable = (bool)e.NewValue!;

            var existing = GetCenterSelectedItemOnceSubscription(listBox);
            existing?.Dispose();
            SetCenterSelectedItemOnceSubscription(listBox, null);

            if (enable)
            {
                // Reset flag whenever the behavior is (re)enabled
                SetHasCenteredOnce(listBox, false);

                IDisposable? subscription = null;
                subscription = listBox
                    .GetObservable(SelectingItemsControl.SelectedItemProperty)
                    .Subscribe(new ActionObserver<object?>(_ =>
                    {
                        if (GetHasCenteredOnce(listBox))
                            return;

                        if (listBox.SelectedItem == null)
                            return;

                        SetHasCenteredOnce(listBox, true);
                        QueueCenterCurrentSelection(listBox);

                        subscription?.Dispose();
                        SetCenterSelectedItemOnceSubscription(listBox, null);
                    }));

                SetCenterSelectedItemOnceSubscription(listBox, subscription);
            }
        });
    }

    private static void QueueCenterCurrentSelection(ListBox listBox)
    {
        if (listBox.SelectedItem != null)
            listBox.ScrollIntoView(listBox.SelectedItem);

        // Delay the centering until after layout has updated; otherwise
        // container positions and viewport size may be outdated.
        Dispatcher.UIThread.Post(() => CenterCurrentSelection(listBox, remainingAttempts: 8), DispatcherPriority.Render);
    }

    private static void QueueCenterCurrentSelectionHorizontal(ListBox listBox)
    {
        if (listBox.SelectedItem != null)
            listBox.ScrollIntoView(listBox.SelectedItem);

        Dispatcher.UIThread.Post(() => CenterCurrentSelectionHorizontal(listBox, remainingAttempts: 60), DispatcherPriority.Render);
    }

    /// <summary>
    /// Some layouts need a short settle phase before the horizontal extent is final
    /// (especially with virtualization and large item counts). We re-center a few
    /// times after initial render so the first visible state is already centered.
    /// </summary>
    private static void QueueCenterCurrentSelectionHorizontalStabilized(ListBox listBox)
    {
        QueueCenterCurrentSelectionHorizontal(listBox);

        var delaysMs = new[] { 16, 40, 80, 140, 220, 340, 500, 700 };
        foreach (var delayMs in delaysMs)
        {
            DispatcherTimer.RunOnce(
                () => QueueCenterCurrentSelectionHorizontal(listBox),
                TimeSpan.FromMilliseconds(delayMs));
        }
    }
    
    private static void CenterCurrentSelection(ListBox listBox, int remainingAttempts)
    {
        if (listBox.SelectedItem == null)
            return;

        // Try to find the container (ListBoxItem) for the selected item
        if (listBox.ContainerFromItem(listBox.SelectedItem) is not Control container)
        {
            if (remainingAttempts > 0)
            {
                Dispatcher.UIThread.Post(
                    () => CenterCurrentSelection(listBox, remainingAttempts - 1),
                    DispatcherPriority.Render);
            }
            return;
        }

        // Find the ScrollViewer inside the ListBox visual tree
        var scrollViewer = listBox
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        if (scrollViewer == null)
        {
            if (remainingAttempts > 0)
            {
                Dispatcher.UIThread.Post(
                    () => CenterCurrentSelection(listBox, remainingAttempts - 1),
                    DispatcherPriority.Render);
            }
            return;
        }

        // Transform the item's top-left into the ScrollViewer's coordinate space
        var p = container.TranslatePoint(new Point(0, 0), scrollViewer);
        if (p == null)
            return;

        var itemTopLeft = p.Value;

        // We want the item to be vertically centered:
        // offsetY = itemTopInContent - (viewportHeight - itemHeight) / 2
        var itemHeight = container.Bounds.Height;
        var viewportHeight = scrollViewer.Viewport.Height;

        if (viewportHeight <= 0 || itemHeight <= 0)
        {
            if (remainingAttempts > 0)
            {
                Dispatcher.UIThread.Post(
                    () => CenterCurrentSelection(listBox, remainingAttempts - 1),
                    DispatcherPriority.Render);
            }
            return;
        }

        var currentOffset = scrollViewer.Offset;
        var desiredOffsetY = currentOffset.Y + itemTopLeft.Y - (viewportHeight - itemHeight) / 2.0;

        // Clamp to valid range (no negative offsets, no scrolling past content end)
        var maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - viewportHeight);
        var clampedOffsetY = Math.Max(0, Math.Min(desiredOffsetY, maxOffsetY));

        var newOffset = new Vector(currentOffset.X, clampedOffsetY);

        scrollViewer.Offset = newOffset;
    }

    private static void CenterCurrentSelectionHorizontal(ListBox listBox, int remainingAttempts)
    {
        if (listBox.SelectedItem == null)
            return;

        if (listBox.ContainerFromItem(listBox.SelectedItem) is not Control container)
        {
            if (remainingAttempts > 0)
            {
                Dispatcher.UIThread.Post(
                    () => CenterCurrentSelectionHorizontal(listBox, remainingAttempts - 1),
                    DispatcherPriority.Render);
            }
            return;
        }

        var scrollViewer = listBox
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        if (scrollViewer == null)
        {
            if (remainingAttempts > 0)
            {
                Dispatcher.UIThread.Post(
                    () => CenterCurrentSelectionHorizontal(listBox, remainingAttempts - 1),
                    DispatcherPriority.Render);
            }
            return;
        }

        var p = container.TranslatePoint(new Point(0, 0), scrollViewer);
        if (p == null)
            return;

        var itemTopLeft = p.Value;

        var itemWidth = container.Bounds.Width;
        var viewportWidth = scrollViewer.Viewport.Width;

        if (viewportWidth <= 0 || itemWidth <= 0)
        {
            if (remainingAttempts > 0)
            {
                Dispatcher.UIThread.Post(
                    () => CenterCurrentSelectionHorizontal(listBox, remainingAttempts - 1),
                    DispatcherPriority.Render);
            }
            return;
        }

        var currentOffset = scrollViewer.Offset;
        var desiredOffsetX = currentOffset.X + itemTopLeft.X - (viewportWidth - itemWidth) / 2.0;

        var maxOffsetX = Math.Max(0, scrollViewer.Extent.Width - viewportWidth);
        var clampedOffsetX = Math.Max(0, Math.Min(desiredOffsetX, maxOffsetX));

        var newOffset = new Vector(clampedOffsetX, currentOffset.Y);
        scrollViewer.Offset = newOffset;
    }
}
