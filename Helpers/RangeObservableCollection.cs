using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Retromind.Helpers;

/// <summary>
/// An ObservableCollection optimized for bulk operations.
/// It suppresses individual notification events when adding multiple items,
/// firing only a single "Reset" event at the end.
/// </summary>
/// <typeparam name="T">The type of elements in the collection.</typeparam>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification = false;

    public RangeObservableCollection() : base() { }

    public RangeObservableCollection(IEnumerable<T> collection) : base(collection) { }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification)
            base.OnPropertyChanged(e);
    }

    /// <summary>
    /// Adds a range of items to the collection, triggering only one notification.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));

        _suppressNotification = true;
        try
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppressNotification = false;
        }
        
        // Notify observers that the collection has changed dramatically (Reset is safest for bulk adds)
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// Clears the collection and adds the new items, triggering only one notification.
    /// Ideal for filtering scenarios.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));

        _suppressNotification = true;
        try
        {
            ClearItems();
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppressNotification = false;
        }

        // Single notification for the whole operation
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}