using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Retromind.Models;

namespace Retromind.Services.BigMode;

/// <summary>Observes edit boundaries, never selection. The owner coalesces edits before Refresh/Rebind.</summary>
public sealed class BigModeLibraryObserver : IDisposable
{
    private readonly IReadOnlyList<MediaNode> _roots;
    private readonly Action _changed;
    private readonly Action<INotifyPropertyChanged, string?>? _presentationChanged;
    private readonly List<Action> _unsubscribe = [];
    public bool IsDirty { get; private set; }

    public BigModeLibraryObserver(IReadOnlyList<MediaNode> roots, Action changed,
        Action<INotifyPropertyChanged, string?>? presentationChanged = null)
    {
        _roots = roots;
        _changed = changed;
        _presentationChanged = presentationChanged;
        Rebind();
    }

    public void Rebind()
    {
        Dispose();
        IsDirty = false;
        WatchCollection(_roots);
        var pending = new Stack<MediaNode>(_roots);
        var visited = new HashSet<MediaNode>();
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node)) continue;
            WatchModel(node);
            WatchCollection(node.Children);
            WatchCollection(node.Items);
            foreach (var item in node.Items) WatchModel(item);
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    private void Dirty()
    {
        if (IsDirty) return;
        IsDirty = true;
        _changed();
    }

    private void WatchCollection(object source)
    {
        if (source is not INotifyCollectionChanged collection) return;
        NotifyCollectionChangedEventHandler handler = (_, _) => Dirty();
        collection.CollectionChanged += handler;
        _unsubscribe.Add(() => collection.CollectionChanged -= handler);
    }

    private void WatchModel(INotifyPropertyChanged model)
    {
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is null or "Id" or "IsProtected" or "Children" or "Items") Dirty();
            else _presentationChanged?.Invoke(model, args.PropertyName);
        };
        model.PropertyChanged += handler;
        _unsubscribe.Add(() => model.PropertyChanged -= handler);
    }

    public void Dispose()
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
    }
}
