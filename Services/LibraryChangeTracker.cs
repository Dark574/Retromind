using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services;

/// <summary>
/// Tracks changes to the library tree and triggers debounced saves.
/// </summary>
public class LibraryChangeTracker
{
    private readonly MediaDataService _dataService;
    
    // Callbacks
    private readonly Action<object?, PropertyChangedEventArgs> _onItemPropertyChanged;
    private readonly Action? _onStructureChanged;
    private readonly Action<MediaItem>? _onItemProtectionChanged;
    private readonly Func<ObservableCollection<MediaNode>, int>? _onBeforeSaveMigration;

    // Dirty state
    private bool _isLibraryDirty;
    private int _libraryDirtyVersion;
    
    // Tracked collections
    private readonly HashSet<MediaItem> _trackedItems = new();
    private readonly HashSet<MediaNode> _trackedNodes = new();
    private ObservableCollection<MediaNode>? _trackedRoots;
    
    // Debounced save
    private readonly object _saveTimerLock = new();
    private Timer? _saveTimer;
    private DateTime _saveDueAtUtc;
    private readonly TimeSpan _saveDebounce = TimeSpan.FromMilliseconds(800);
    private readonly LibrarySaveSequencer _saveSequencer = new();
    
    private static readonly HashSet<string> DirtyTrackedItemProperties = new(StringComparer.Ordinal)
    {
        nameof(MediaItem.IsFavorite)
    };
    
    public event Action? LibraryDirtyStateChanged;
    public event Action<Exception>? SaveFailed;
    public event Action? SaveSucceeded;

    internal bool IsDirty => _isLibraryDirty;
    internal int DirtyVersion => _libraryDirtyVersion;
    
    public LibraryChangeTracker(
        MediaDataService dataService,
        Action<object?, PropertyChangedEventArgs> onItemPropertyChanged,
        Action? onStructureChanged = null,
        Action<MediaItem>? onItemProtectionChanged = null,
        Func<ObservableCollection<MediaNode>, int>? onBeforeSaveMigration = null)
    {
        _dataService = dataService;
        _onItemPropertyChanged = onItemPropertyChanged;
        _onStructureChanged = onStructureChanged;
        _onItemProtectionChanged = onItemProtectionChanged;
        _onBeforeSaveMigration = onBeforeSaveMigration;
    }
    
    public void Initialize(ObservableCollection<MediaNode> roots)
    {
        ResetState();
        _trackedRoots = roots;
        _trackedRoots.CollectionChanged += OnRootItemsChanged;
        
        foreach (var node in _trackedRoots)
            TrackNodeRecursive(node);
    }
    
    public void ResetState()
    {
        StopDebouncedSave();

        if (_trackedRoots != null)
            _trackedRoots.CollectionChanged -= OnRootItemsChanged;

        UntrackAllNodesAndItems();
        
        _trackedRoots = null;
        _trackedItems.Clear();
        _trackedNodes.Clear();
        _isLibraryDirty = false;
        _libraryDirtyVersion = 0;
    }
    
    public void StopTracking()
    {
        ResetState();
    }
    
    public void MarkDirty()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(MarkDirty, Avalonia.Threading.DispatcherPriority.Background);
            return;
        }

        MarkDirtyCore();
    }

    private void MarkDirtyCore()
    {
        _isLibraryDirty = true;
        _libraryDirtyVersion++;
        LibraryDirtyStateChanged?.Invoke();
        
        DebouncedSave();
    }
    
    public void MarkDirtyAndSaveSoon()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(MarkDirtyAndSaveSoon, Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        
        MarkDirty();
        var version = _libraryDirtyVersion;
        _ = ObserveImmediateSaveAsync(version);
    }

    private async Task ObserveImmediateSaveAsync(int expectedVersion)
    {
        try
        {
            await SaveIfDirtyAsync(force: false, expectedVersion: expectedVersion).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // SaveIfDirtyAsync already retained the dirty state and raised SaveFailed.
            Debug.WriteLine($"[LibraryTracker] Immediate save failed: {ex.Message}");
        }
    }
    
    private void DebouncedSave()
    {
        lock (_saveTimerLock)
        {
            _saveDueAtUtc = DateTime.UtcNow + _saveDebounce;
            _saveTimer ??= new Timer(
                static state => ((LibraryChangeTracker)state!).OnSaveTimerElapsed(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _saveTimer.Change(_saveDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSaveTimerElapsed()
    {
        int version;
        lock (_saveTimerLock)
        {
            if (_saveTimer == null || !_isLibraryDirty)
                return;

            var remaining = _saveDueAtUtc - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _saveTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            version = _libraryDirtyVersion;
        }

        _ = ObserveDebouncedSaveAsync(version);
    }

    private async Task ObserveDebouncedSaveAsync(int expectedVersion)
    {
        try
        {
            await SaveIfDirtyAsync(force: false, expectedVersion: expectedVersion).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibraryTracker] Debounced save failed: {ex.Message}");
        }
    }

    private void StopDebouncedSave()
    {
        lock (_saveTimerLock)
        {
            _saveDueAtUtc = DateTime.MaxValue;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }
    
    public Task SaveIfDirtyAsync(bool force, int? expectedVersion = null)
        => _saveSequencer.RunAsync(() => SaveIfDirtyCoreAsync(force, expectedVersion));

    private async Task SaveIfDirtyCoreAsync(bool force, int? expectedVersion)
    {
        // Re-check after waiting: a newer queued save may already have persisted
        // this version, or this expected version may have become obsolete.
        if (!force && !_isLibraryDirty) return;
        if (expectedVersion.HasValue && expectedVersion.Value != _libraryDirtyVersion) return;

        var saveVersion = expectedVersion ?? _libraryDirtyVersion;

        try
        {
            // 1. Migration (e.g. portable paths)
            if (_trackedRoots != null && _onBeforeSaveMigration != null)
            {
                var migrated = await UiThreadHelper.InvokeAsync(() => _onBeforeSaveMigration(_trackedRoots!));
                if (migrated > 0)
                    Debug.WriteLine($"[LibraryTracker] Migrated {migrated} paths.");
            }

            var snapshot = await UiThreadHelper.InvokeAsync(() =>
            {
                return _trackedRoots != null ? _dataService.CreateSnapshot(_trackedRoots) : null;
            }).ConfigureAwait(false);

            if (snapshot == null) return;

            var json = await Task.Run(() => _dataService.Serialize(snapshot)).ConfigureAwait(false);
            await _dataService.SaveJsonAsync(json).ConfigureAwait(false);

            if (_libraryDirtyVersion == saveVersion)
                _isLibraryDirty = false;

            SaveSucceeded?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibraryTracker] Save failed: {ex.Message}");
            _isLibraryDirty = true;
            SaveFailed?.Invoke(ex);
            throw;
        }
    }
    
    // --- Recursive tracking ---
    
    private void TrackNodeRecursive(MediaNode node)
    {
        if (!_trackedNodes.Add(node))
            return;
        
        node.Items.CollectionChanged += OnNodeItemsChanged;
        node.Children.CollectionChanged += OnNodeChildrenChanged;
        
        foreach (var item in node.Items)
            TrackItem(item);
        
        foreach (var child in node.Children)
            TrackNodeRecursive(child);
    }
    
    private void UntrackNodeRecursive(MediaNode node)
    {
        if (!_trackedNodes.Remove(node))
            return;
        
        node.Items.CollectionChanged -= OnNodeItemsChanged;
        node.Children.CollectionChanged -= OnNodeChildrenChanged;
        
        foreach (var item in node.Items)
            UntrackItem(item);
        
        foreach (var child in node.Children)
            UntrackNodeRecursive(child);
    }
    
    private void TrackItem(MediaItem item)
    {
        if (!_trackedItems.Add(item))
            return;
        
        item.PropertyChanged += OnItemPropertyChanged;
    }
    
    private void UntrackItem(MediaItem item)
    {
        if (!_trackedItems.Remove(item))
            return;
        
        item.PropertyChanged -= OnItemPropertyChanged;
    }

    private void RebuildTrackedTree()
    {
        UntrackAllNodesAndItems();

        if (_trackedRoots == null)
            return;

        foreach (var node in _trackedRoots)
            TrackNodeRecursive(node);
    }

    private void UntrackAllNodesAndItems()
    {
        foreach (var item in _trackedItems)
            item.PropertyChanged -= OnItemPropertyChanged;

        foreach (var node in _trackedNodes)
        {
            node.Items.CollectionChanged -= OnNodeItemsChanged;
            node.Children.CollectionChanged -= OnNodeChildrenChanged;
        }

        _trackedItems.Clear();
        _trackedNodes.Clear();
    }
    
    // --- Event handlers ---
    
    private void OnRootItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildTrackedTree();
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (var oldItem in e.OldItems)
                {
                    if (oldItem is MediaNode node)
                        UntrackNodeRecursive(node);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var newItem in e.NewItems)
                {
                    if (newItem is MediaNode node)
                        TrackNodeRecursive(node);
                }
            }
        }
        
        _onStructureChanged?.Invoke();
        MarkDirtyCore();
    }
    
    private void OnNodeItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildTrackedTree();
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (var oldItem in e.OldItems)
                {
                    if (oldItem is MediaItem item)
                        UntrackItem(item);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var newItem in e.NewItems)
                {
                    if (newItem is MediaItem item)
                        TrackItem(item);
                }
            }
        }
        
        _onStructureChanged?.Invoke();
        MarkDirtyCore();
    }
    
    private void OnNodeChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RebuildTrackedTree();
        }
        else
        {
            if (e.OldItems != null)
            {
                foreach (var oldItem in e.OldItems)
                {
                    if (oldItem is MediaNode node)
                        UntrackNodeRecursive(node);
                }
            }

            if (e.NewItems != null)
            {
                foreach (var newItem in e.NewItems)
                {
                    if (newItem is MediaNode node)
                        TrackNodeRecursive(node);
                }
            }
        }
        
        _onStructureChanged?.Invoke();
        MarkDirtyCore();
    }
    
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MediaItem item)
            return;
        
        var isProtectionProperty = e.PropertyName == nameof(MediaItem.IsProtected);
        var skipDirtyTracking = isProtectionProperty;
        
        if (!skipDirtyTracking &&
            (string.IsNullOrWhiteSpace(e.PropertyName) ||
             DirtyTrackedItemProperties.Contains(e.PropertyName)))
        {
            MarkDirtyAndSaveSoon();
        }
        
        // 2. Parental Refresh
        if (isProtectionProperty)
        {
            _onItemProtectionChanged?.Invoke(item);
        }

        // Forward other item changes to the UI coordinator.
        if (!isProtectionProperty)
            _onItemPropertyChanged?.Invoke(sender, e);
    }
}
