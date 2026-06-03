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
    private CancellationTokenSource? _saveCts;
    private readonly TimeSpan _saveDebounce = TimeSpan.FromMilliseconds(800);
    
    private static readonly HashSet<string> DirtyTrackedItemProperties = new(StringComparer.Ordinal)
    {
        nameof(MediaItem.IsFavorite)
    };
    
    public bool IsDirty => _isLibraryDirty;
    public event Action? LibraryDirtyStateChanged;
    
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
        if (_trackedRoots != null)
        {
            _trackedRoots.CollectionChanged -= OnRootItemsChanged;
            foreach (var node in _trackedRoots)
                UntrackNodeRecursive(node);
        }
        
        _trackedRoots = null;
        _trackedItems.Clear();
        _trackedNodes.Clear();
        _isLibraryDirty = false;
        _libraryDirtyVersion = 0;
    }
    
    public void StopTracking()
    {
        ResetState();
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = null;
    }
    
    public void MarkDirty()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(MarkDirty, Avalonia.Threading.DispatcherPriority.Background);
            return;
        }
        
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
        _ = SaveIfDirtyAsync(force: false, expectedVersion: version);
    }
    
    private void DebouncedSave()
    {
        _saveCts?.Cancel();
        _saveCts?.Dispose();
        _saveCts = new CancellationTokenSource();
        
        var token = _saveCts.Token;
        var myVersion = _libraryDirtyVersion;
        
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_saveDebounce, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                
                await SaveIfDirtyAsync(force: false, expectedVersion: myVersion).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LibraryTracker] Debounced save failed: {ex.Message}");
            }
        }, token);
    }
    
    public async Task SaveIfDirtyAsync(bool force, int? expectedVersion = null)
    {
        if (!force && !_isLibraryDirty) return;
        if (expectedVersion.HasValue && expectedVersion.Value != _libraryDirtyVersion) return;
        
        var saveVersion = expectedVersion ?? _libraryDirtyVersion;
        
        // 1. Migration (z.B. Portable Paths)
        if (_trackedRoots != null && _onBeforeSaveMigration != null)
        {
            var migrated = await UiThreadHelper.InvokeAsync(() => _onBeforeSaveMigration(_trackedRoots!));
            if (migrated > 0)
                Debug.WriteLine($"[LibraryTracker] Migrated {migrated} paths.");
        }

        try
        {
            var snapshot = await UiThreadHelper.InvokeAsync(() =>
            {
                return _trackedRoots != null ? _dataService.CreateSnapshot(_trackedRoots) : null;
            }).ConfigureAwait(false);
            
            if (snapshot == null) return;
            
            var json = await Task.Run(() => _dataService.Serialize(snapshot)).ConfigureAwait(false);
            await _dataService.SaveJsonAsync(json).ConfigureAwait(false);
            
            if (_libraryDirtyVersion == saveVersion)
                _isLibraryDirty = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LibraryTracker] Save failed: {ex.Message}");
            _isLibraryDirty = true;
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
    
    // --- Event handlers ---
    
    private void OnRootItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
        
        _onStructureChanged?.Invoke();
        LibraryDirtyStateChanged?.Invoke();
    }
    
    private void OnNodeItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
        
        _onStructureChanged?.Invoke();
        LibraryDirtyStateChanged?.Invoke();
    }
    
    private void OnNodeChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
        
        _onStructureChanged?.Invoke();
        LibraryDirtyStateChanged?.Invoke();
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

        // Forward to VM for asset path updates (only if not protection)
        if (!isProtectionProperty)
            _onItemPropertyChanged?.Invoke(sender, e);
    }
}