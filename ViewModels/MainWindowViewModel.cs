using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// Main ViewModel acting as the orchestrator for the application.
/// This part contains State, Constructor and Lifecycle logic.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    // --- Services ---
    private readonly AudioService _audioService;
    private readonly MediaDataService _dataService;
    private readonly FileManagementService _fileService;
    private readonly ImportService _importService;
    private readonly LauncherService _launcherService;
    private readonly StoreImportService _storeService;
    private readonly SettingsService _settingsService;
    private readonly MetadataService _metadataService; 
    private readonly GamepadService _gamepadService;
    private readonly SoundEffectService _soundEffectService;
    private readonly IDocumentService _documentService;

    // shared HttpClient from DI (timeouts + user-agent, avoids socket churn)
    private readonly HttpClient _httpClient;
    
    // Token Source for cancelling old content loading tasks
    private CancellationTokenSource? _updateContentCts;
    private TaskCompletionSource<bool>? _updateContentTcs;
    
    // --- State ---
    private AppSettings _currentSettings;

    // UI Layout State (Persisted)
    private GridLength _treePaneWidth = new(250);
    private GridLength _detailPaneWidth = new(300);
    private double _itemWidth;

    // --- UI Properties ---
    private ObservableCollection<MediaNode> _rootItems = new();
    private MediaNode? _selectedNode;
    private object? _selectedNodeContent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibraryLoadingHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLibraryHint))]
    private bool _isLibraryLoading;

    // Holds the currently loaded theme view. If null, standard desktop mode is shown.
    [ObservableProperty]
    private object? _fullScreenContent;
    
    // Prevents overlapping launches (double-click + Start button + Play Random).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunchIdle))]
    private bool _isLaunchInProgress;
    
    /// <summary>
    /// True if no launch is currently running. Can be used to enable/disable UI elements.
    /// </summary>
    public bool IsLaunchIdle => !IsLaunchInProgress;
    
    // Keeps the currently active content VM so we can detach event handlers (prevents leaks).
    private MediaAreaViewModel? _currentMediaAreaVm;
    private SearchAreaViewModel? _currentSearchAreaVm;
    private int _currentShownGameCount;
    private int _totalLibraryGameCount;
    
    public IStorageProvider? StorageProvider { get; set; }
    
    // Command to open per-item manuals/documents with the system viewer.
    public IRelayCommand<MediaAsset?> OpenManualCommand { get; private set; } = null!;

    private DateTime _lastGuideHandledUtc = DateTime.MinValue;

    // Debounced Settings Save
    private CancellationTokenSource? _saveSettingsCts;
    private readonly TimeSpan _saveSettingsDebounce = TimeSpan.FromMilliseconds(500);

    private readonly TaskCompletionSource<bool> _loadDataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _pendingBigModeEntry;

    // --- Library Dirty Tracking + Debounced Library Save ---
    private bool _isLibraryDirty;
    private int _libraryDirtyVersion;

    // Tracks items/nodes for in-place edits that should persist immediately.
    private readonly HashSet<MediaItem> _dirtyTrackedItems = new();
    private readonly HashSet<MediaNode> _dirtyTrackedNodes = new();
    private ObservableCollection<MediaNode>? _dirtyTrackedRoots;

    // Per-node selection memory for quick return after switching nodes.
    private readonly Dictionary<string, string> _lastSelectedMediaByNodeId = new(StringComparer.Ordinal);

    // Remembers where the user came from before entering global search.
    private string? _searchReturnNodeId;
    private string? _searchReturnItemId;

    private static readonly HashSet<string> DirtyTrackedItemProperties = new(StringComparer.Ordinal)
    {
        nameof(MediaItem.IsFavorite)
    };
    
    // ensure Cleanup() is executed at most once (Exit + Closing can both fire)
    private int _cleanupOnce;

    private CancellationTokenSource? _saveLibraryCts;
    private readonly TimeSpan _saveLibraryDebounce = TimeSpan.FromMilliseconds(800);
    
    public bool ShouldIgnoreBackKeyTemporarily()
        => (DateTime.UtcNow - _lastGuideHandledUtc) < TimeSpan.FromMilliseconds(600);
    
    // Helper to access the current window for dialogs
    private Window? CurrentWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    // Remember the window state before entering BigMode so we can restore it afterwards.
    private WindowState _previousWindowState = WindowState.Maximized;
    
    public ObservableCollection<MediaNode> RootItems
    {
        get => _rootItems;
        set
        {
            if (!SetProperty(ref _rootItems, value))
                return;

            RefreshTreeVisibility();
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
            UpdateLibraryGameCounters();
        }
    }

    public MediaNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                UpdateContent();
                NotifyNodeCommandsCanExecuteChanged();

                OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            
                // Persist selection
                if (value != null)
                {
                    _currentSettings.LastSelectedNodeId = value.Id;
                    SaveSettingsOnly();
                    
                    // Mirror state into BigMode (CoreApp -> BigMode).
                    UpdateBigModeStateFromCoreSelection(value, null);
                }
            }
        }
    }

    public object? SelectedNodeContent
    {
        get => _selectedNodeContent;
        set
        {
            if (!SetProperty(ref _selectedNodeContent, value))
                return;

            OnPropertyChanged(nameof(ShowLibraryLoadingHint));
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
            UpdateLibraryGameCounters();
        }
    }

    public bool ShowLibraryLoadingHint => IsLibraryLoading && SelectedNodeContent is null;

    // Empty-library hint should only be shown for truly empty libraries,
    // not while startup loading is still building the first content.
    public bool ShowEmptyLibraryHint => !IsLibraryLoading && SelectedNodeContent is null && RootItems.Count == 0;
    public bool ShowLibraryGameCountSummary => TotalLibraryGameCount > 0;
    public int CurrentShownGameCount => _currentShownGameCount;
    public int TotalLibraryGameCount => _totalLibraryGameCount;
    public string LibraryGameCountSummary =>
        $"{CurrentShownGameCount.ToString("N0", CultureInfo.CurrentCulture)} angezeigt | {TotalLibraryGameCount.ToString("N0", CultureInfo.CurrentCulture)} gesamt";

    public string? ResolvedSelectedItemLogoPath => ResolveSelectedItemAsset(AssetType.Logo);
    public string? ResolvedSelectedItemWallpaperPath => ResolveSelectedItemAsset(AssetType.Wallpaper);
    public string? ResolvedSelectedItemVideoPath => ResolveSelectedItemAsset(AssetType.Video);
    public string? ResolvedSelectedItemMarqueePath => ResolveSelectedItemAsset(AssetType.Marquee);

    // --- Layout Properties ---
    public GridLength TreePaneWidth
    {
        get => _treePaneWidth;
        set { if (SetProperty(ref _treePaneWidth, value)) _currentSettings.TreeColumnWidth = value.Value; }
    }

    public GridLength DetailPaneWidth
    {
        get => _detailPaneWidth;
        set
        {
            if (SetProperty(ref _detailPaneWidth, value))
            {
                _currentSettings.DetailColumnWidth = value.Value;
                SaveSettingsOnly();
            }
        }
    }

    public double ItemWidth
    {
        get => _itemWidth;
        set
        {
            if (SetProperty(ref _itemWidth, value))
            {
                _currentSettings.ItemWidth = value;
                SaveSettingsOnly();
            }
        }
    }

    // --- Theme Properties ---
    public bool IsDarkTheme
    {
        get => _currentSettings.IsDarkTheme;
        set
        {
            if (_currentSettings.IsDarkTheme != value)
            {
                _currentSettings.IsDarkTheme = value;
                OnPropertyChanged();
            
                // Trigger refresh of dependent brushes
                OnPropertyChanged(nameof(PanelBackground));
                OnPropertyChanged(nameof(TextColor));
                OnPropertyChanged(nameof(WindowBackground));

                if (Application.Current != null)
                    Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;

                SaveSettingsOnly();
            }
        }
    }

    public IBrush PanelBackground => IsDarkTheme
        ? new SolidColorBrush(Color.Parse("#CC252526"))
        : new SolidColorBrush(Color.Parse("#CCC8C8C8"));
    public IBrush TextColor => IsDarkTheme ? Brushes.White : new SolidColorBrush(Color.Parse("#1A1A1A"));
    public IBrush WindowBackground => IsDarkTheme
        ? new SolidColorBrush(Color.Parse("#252526"))
        : new SolidColorBrush(Color.Parse("#D6D6D6"));

    private readonly Action _onGuidePressed;
    
    // --- Constructor (Dependency Injection) ---
    public MainWindowViewModel(
        AudioService audioService,
        MediaDataService dataService,
        FileManagementService fileService,
        ImportService importService,
        LauncherService launcherService,
        StoreImportService storeService,
        SettingsService settingsService,
        MetadataService metadataService,
        SoundEffectService soundEffectService,
        HttpClient httpClient,
        AppSettings preloadedSettings,
        IDocumentService documentService)
    {
        _audioService = audioService;
        _dataService = dataService;
        _fileService = fileService;
        _importService = importService;
        _launcherService = launcherService;
        _storeService = storeService;
        _settingsService = settingsService;
        _metadataService = metadataService;
        _soundEffectService = soundEffectService;
        _httpClient = httpClient;
        _currentSettings = preloadedSettings;
        _documentService = documentService;
        _fileService.LibraryChanged += MarkLibraryDirty;
        _audioService.MusicPlaybackEnded += OnMusicPlaybackEnded;

        // Seed layout values early so bindings are stable before LoadData completes.
        _treePaneWidth = new GridLength(_currentSettings.TreeColumnWidth);
        _detailPaneWidth = new GridLength(_currentSettings.DetailColumnWidth);
        _itemWidth = _currentSettings.ItemWidth;
        
        // Ensure SDL also reports background events and applies deadzone handling.
        Environment.SetEnvironmentVariable("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        Environment.SetEnvironmentVariable("SDL_LINUX_JOYSTICK_DEADZONES", "1");

        // Gamepad service (hot-plug + guide routing)
        _gamepadService = new GamepadService();
        _gamepadService.StartMonitoring();
    
        // Route Guide/Home globally: only act if BigMode is actually active.
        _onGuidePressed = () =>
        {
            // SDL callback thread -> always marshal to UI thread
            UiThreadHelper.Post(() =>
            {
                _lastGuideHandledUtc = DateTime.UtcNow;

                if (FullScreenContent == null) return;

                // If BigMode is active, request closing it.
                if (FullScreenContent is BigModeViewModel directVm)
                {
                    directVm.ForceExitCommand.Execute(null);
                }
                else if (FullScreenContent is Avalonia.Controls.Control { DataContext: BigModeViewModel vm })
                {
                    vm.ForceExitCommand.Execute(null);
                }
            });
        };
        _gamepadService.OnGuide += _onGuidePressed;
        
        InitializeCommands();
        Debug.WriteLine("[DEBUG] Konstruktor finished. BigModeOnly = " + (App.Current?.IsBigModeOnly == true));
    }

    // --- Persistence & Lifecycle ---

    public async Task LoadData()
    {
        IsLibraryLoading = true;

        try 
        {
            RootItems = await _dataService.LoadAsync();
            Debug.WriteLine("[DEBUG] LoadData: RootItems loaded. Count = " + RootItems.Count);
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
        
            _isLibraryDirty = false;
            _libraryDirtyVersion = 0;
            ResetLibraryChangeTracking();
            InitializeParentalStateAfterLoad();
            
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(PanelBackground));
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(WindowBackground));
    
            if (Application.Current != null)
                Application.Current.RequestedThemeVariant = _currentSettings.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

            TreePaneWidth = new GridLength(_currentSettings.TreeColumnWidth);
            DetailPaneWidth = new GridLength(_currentSettings.DetailColumnWidth);
            ItemWidth = _currentSettings.ItemWidth;

            // Clean up persisted selection state if it points to deleted/moved nodes or items.
            // This prevents invalid IDs from crashing the UI on startup.
            var settingsChanged = SanitizeSelectionState();
            if (settingsChanged)
                SaveSettingsOnly();

            if (!string.IsNullOrEmpty(_currentSettings.LastSelectedNodeId))
            {
                var node = FindNodeById(RootItems, _currentSettings.LastSelectedNodeId);
                if (node != null && node.IsVisibleInTree)
                {
                    SelectedNode = node;
                    ExpandPathToNode(RootItems, node);
                }
                else
                {
                    var firstVisible = FindFirstVisibleNode();
                    if (firstVisible != null)
                        ExpandPathToNode(RootItems, firstVisible);
                    SelectedNode = firstVisible;
                }
            }
            else
            {
                var firstVisible = FindFirstVisibleNode();
                if (firstVisible != null)
                    ExpandPathToNode(RootItems, firstVisible);
                SelectedNode = firstVisible;
            }

            // Wait for the first selected node content to be built so the main view
            // appears quickly and predictably before background warmup starts.
            await AwaitCurrentContentUpdateAsync().ConfigureAwait(false);

            // Heavy startup maintenance runs in the background after first content is visible.
            _ = RunDeferredStartupWarmupAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ViewModel] LoadData Error: {ex}");
        }
        finally
        {
            IsLibraryLoading = false;
            _loadDataTcs.TrySetResult(true);
        }
    }

    private async Task RunDeferredStartupWarmupAsync()
    {
        try
        {
            await RescanAllAssetsAsync().ConfigureAwait(false);
            await UiThreadHelper.InvokeAsync(() => SortAllNodesRecursive(RootItems)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupWarmup] Failed: {ex.Message}");
        }
    }

    private bool SanitizeSelectionState()
    {
        bool changed = false;

        MediaNode? selectedNode = null;
        if (_currentSettings.LastSelectedNodeId is { Length: > 0 } nodeId)
        {
            selectedNode = FindNodeById(RootItems, nodeId);
            if (selectedNode is null)
            {
                _currentSettings.LastSelectedNodeId = null;
                changed = true;
            }
        }

        if (selectedNode is null)
        {
            if (_currentSettings.LastSelectedMediaId is { Length: > 0 })
            {
                _currentSettings.LastSelectedMediaId = null;
                changed = true;
            }
        }
        else if (_currentSettings.LastSelectedMediaId is { Length: > 0 } mediaId)
        {
            if (!IsMediaIdInNodeSubtree(selectedNode, mediaId))
            {
                _currentSettings.LastSelectedMediaId = null;
                changed = true;
            }
        }

        if (_currentSettings.LastBigModeNavigationPath is { Count: > 0 } path)
        {
            if (!IsBigModePathValid(path, out var pathEndNode, out var currentLevel))
            {
                _currentSettings.LastBigModeNavigationPath = null;
                _currentSettings.LastBigModeSelectedNodeId = null;
                _currentSettings.LastBigModeWasItemView = false;
                changed = true;
            }
            else
            {
                var itemId = _currentSettings.LastBigModeSelectedNodeId;

                if (_currentSettings.LastBigModeWasItemView)
                {
                    if (pathEndNode is null || string.IsNullOrWhiteSpace(itemId) ||
                        pathEndNode.Items.All(i => i.Id == itemId))
                    {
                        _currentSettings.LastBigModeWasItemView = false;
                        _currentSettings.LastBigModeSelectedNodeId = pathEndNode?.Id;
                        changed = true;
                    }
                }
                else if (itemId is { Length: > 0 })
                {
                    if (currentLevel.All(n => n.Id != itemId))
                    {
                        _currentSettings.LastBigModeSelectedNodeId = null;
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private bool IsMediaIdInNodeSubtree(MediaNode node, string mediaId)
    {
        if (node.Items.Any(i => i.Id == mediaId))
            return true;

        foreach (var child in node.Children)
        {
            if (IsMediaIdInNodeSubtree(child, mediaId))
                return true;
        }

        return false;
    }

    private bool IsBigModePathValid(IReadOnlyList<string> path, out MediaNode? pathEndNode,
        out ObservableCollection<MediaNode> currentLevel)
    {
        pathEndNode = null;
        currentLevel = RootItems;

        foreach (var nodeId in path)
        {
            var next = currentLevel.FirstOrDefault(n => n.Id == nodeId);
            if (next == null)
                return false;

            pathEndNode = next;
            currentLevel = next.Children;
        }

        return true;
    }

    // Helper to make UpdateContent awaitable (wraps the background task in an awaitable Task).
    private async Task UpdateContentAsync()
    {
        UpdateContent();  // Start the existing background task.

        await AwaitCurrentContentUpdateAsync().ConfigureAwait(false);
    }

    private async Task AwaitCurrentContentUpdateAsync()
    {
        var tcs = _updateContentTcs;
        if (tcs == null) return;

        var timeoutTask = Task.Delay(5000);
        var completed = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);
        if (completed == timeoutTask)
        {
            Debug.WriteLine("[DEBUG] UpdateContentAsync: Timeout - content update not completed.");
            return;
        }

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Expected when a newer UpdateContent call supersedes this one.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DEBUG] UpdateContentAsync: Failed - {ex.Message}");
        }
    }
    
    public async Task SaveData()
    {
        // SaveData is a “strong” save: when someone calls it explicitly,
        // we persist the library (if dirty) + settings (immediately).
        // Serialization happens on the UI thread to avoid cross-thread collection access.
        await SaveLibraryIfDirtyAsync(force: false).ConfigureAwait(false);
        var json = await UiThreadHelper.InvokeAsync(() => _settingsService.Serialize(_currentSettings))
            .ConfigureAwait(false);
        await _settingsService.SaveJsonAsync(json).ConfigureAwait(false);
    }

    private void MarkLibraryDirty()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(MarkLibraryDirty, DispatcherPriority.Background);
            return;
        }

        _isLibraryDirty = true;
        _libraryDirtyVersion++;
        UpdateLibraryGameCounters();

        DebouncedSaveLibrary();
    }

    private void MarkLibraryDirtyAndSaveSoon()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(MarkLibraryDirtyAndSaveSoon, DispatcherPriority.Background);
            return;
        }

        MarkLibraryDirty();
        var version = _libraryDirtyVersion;
        _ = SaveLibraryIfDirtyAsync(force: false, expectedVersion: version);
    }

    private void DebouncedSaveLibrary()
    {
        _saveLibraryCts?.Cancel();
        _saveLibraryCts?.Dispose();
        _saveLibraryCts = new CancellationTokenSource();

        var token = _saveLibraryCts.Token;
        var myVersion = _libraryDirtyVersion;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_saveLibraryDebounce, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                await SaveLibraryIfDirtyAsync(force: false, expectedVersion: myVersion).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected during debounce
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Library] Debounced save failed: {ex.Message}");
            }
        }, token);
    }

    private async Task SaveLibraryIfDirtyAsync(bool force, int? expectedVersion = null)
    {
        if (!force && !_isLibraryDirty) return;

        // When called from a debounced run, but a newer change already bumped the version,
        // skip this save (the next debounce run will handle it).
        if (expectedVersion.HasValue && expectedVersion.Value != _libraryDirtyVersion)
            return;

        // Capture the version we are about to save; if it changes during IO we keep the library dirty.
        var saveVersion = expectedVersion ?? _libraryDirtyVersion;

        if (_currentSettings.PreferPortableLaunchPaths)
        {
            var migrated = 0;
            await UiThreadHelper.InvokeAsync(() =>
            {
                migrated = LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(RootItems);
            }).ConfigureAwait(false);

            if (migrated > 0)
                Debug.WriteLine($"[Library] Migrated {migrated} launch paths to LibraryRelative.");
        }

        try
        {
            // Snapshot on UI thread, serialize in background.
            var snapshot = await UiThreadHelper.InvokeAsync(() => _dataService.CreateSnapshot(RootItems))
                .ConfigureAwait(false);
            var json = await Task.Run(() => _dataService.Serialize(snapshot)).ConfigureAwait(false);
            await _dataService.SaveJsonAsync(json).ConfigureAwait(false);

            // Only mark the library as clean if no new changes happened during this save.
            if (_libraryDirtyVersion == saveVersion)
                _isLibraryDirty = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Library] Save failed: {ex.Message}");
            // Keep dirty=true so a later attempt can retry the save.
            _isLibraryDirty = true;
        }
    }

    private void ResetLibraryChangeTracking()
    {
        if (_dirtyTrackedRoots != null)
        {
            _dirtyTrackedRoots.CollectionChanged -= OnRootItemsChanged;
            foreach (var node in _dirtyTrackedRoots)
                UntrackNodeRecursive(node);
        }

        _dirtyTrackedRoots = RootItems;
        if (_dirtyTrackedRoots == null)
            return;

        _dirtyTrackedRoots.CollectionChanged += OnRootItemsChanged;
        foreach (var node in _dirtyTrackedRoots)
            TrackNodeRecursive(node);
    }

    private void StopLibraryChangeTracking()
    {
        if (_dirtyTrackedRoots != null)
        {
            _dirtyTrackedRoots.CollectionChanged -= OnRootItemsChanged;
            foreach (var node in _dirtyTrackedRoots)
                UntrackNodeRecursive(node);
        }

        _dirtyTrackedRoots = null;
        _dirtyTrackedItems.Clear();
        _dirtyTrackedNodes.Clear();
    }

    private void TrackNodeRecursive(MediaNode node)
    {
        if (!_dirtyTrackedNodes.Add(node))
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
        if (!_dirtyTrackedNodes.Remove(node))
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
        if (!_dirtyTrackedItems.Add(item))
            return;

        item.PropertyChanged += OnItemPropertyChanged;
    }

    private void UntrackItem(MediaItem item)
    {
        if (!_dirtyTrackedItems.Remove(item))
            return;

        item.PropertyChanged -= OnItemPropertyChanged;
    }

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

        RefreshTreeVisibility();
        OnPropertyChanged(nameof(ShowEmptyLibraryHint));
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

        RefreshTreeVisibility();
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

        RefreshTreeVisibility();
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not MediaItem)
            return;

        var isProtectionProperty = e.PropertyName == nameof(MediaItem.IsProtected);
        var skipDirtyTracking = isProtectionProperty;

        if (!skipDirtyTracking &&
            (string.IsNullOrWhiteSpace(e.PropertyName) ||
             DirtyTrackedItemProperties.Contains(e.PropertyName)))
        {
            MarkLibraryDirtyAndSaveSoon();
        }

        if (isProtectionProperty)
        {
            if (_isApplyingProtectionChanges)
                return;

            ScheduleParentalProtectionRefresh();
        }

        // If assets of the currently selected item change, refresh the wallpaper (and related resolved paths).
        if (sender is MediaItem item &&
            !string.IsNullOrWhiteSpace(e.PropertyName) &&
            (e.PropertyName == nameof(MediaItem.PrimaryWallpaperPath) ||
             e.PropertyName == nameof(MediaItem.PrimaryScreenshotPath) ||
             e.PropertyName == nameof(MediaItem.PrimaryLogoPath) ||
             e.PropertyName == nameof(MediaItem.PrimaryVideoPath) ||
             e.PropertyName == nameof(MediaItem.PrimaryMarqueePath)))
        {
            var selected = GetCurrentSelectedItem();
            if (!ReferenceEquals(item, selected))
                return;

            if (UiThreadHelper.CheckAccess())
            {
                OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            }
            else
            {
                UiThreadHelper.Post(() =>
                {
                    OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
                    OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
                    OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
                    OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
                });
            }
        }
    }
    
    private async void SaveSettingsOnly()
    {
        _saveSettingsCts?.Cancel();
        _saveSettingsCts?.Dispose();
        _saveSettingsCts = new CancellationTokenSource();

        var token = _saveSettingsCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_saveSettingsDebounce, token);
                token.ThrowIfCancellationRequested();

                // Serialize on UI thread to avoid cross-thread collection access.
                var json = await UiThreadHelper.InvokeAsync(() => _settingsService.Serialize(_currentSettings))
                    .ConfigureAwait(false);
                await _settingsService.SaveJsonAsync(json).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Debounce cancellation: expected.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] Debounced save failed: {ex.Message}");
            }
        }, token);
    }

    private string? ResolveSelectedItemAsset(AssetType type)
    {
        var item = GetCurrentSelectedItem();
        if (item == null)
            return null;

        var node = FindParentNode(RootItems, item) ?? SelectedNode;
        return AssetResolver.ResolveAssetPath(item, node, type);
    }

    private MediaItem? GetCurrentSelectedItem()
    {
        var item = _currentMediaAreaVm?.SelectedMediaItem;
        if (item != null)
            return item;

        if (SelectedNodeContent is SearchAreaViewModel searchVm)
            return searchVm.SelectedMediaItem;

        return null;
    }

    /// <summary>
    /// Flushes pending saves (best effort) and then performs cleanup.
    /// Must be awaited from the UI during window closing to avoid deadlocks.
    /// </summary>
    public async Task FlushAndCleanupAsync()
    {
        // Stop playback immediately; saving can take a moment.
        _audioService.StopMusic();

        // Cancel pending debounces so we don't race with our final flush.
        _saveSettingsCts?.Cancel();
        _saveLibraryCts?.Cancel();
        _parentalRefreshCts?.Cancel();

        try
        {
            await SaveLibraryIfDirtyAsync(force: true).ConfigureAwait(false);
            // Serialize on UI thread to avoid cross-thread collection access.
            var json = await UiThreadHelper.InvokeAsync(() => _settingsService.Serialize(_currentSettings))
                .ConfigureAwait(false);
            await _settingsService.SaveJsonAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shutdown] Final save failed: {ex.Message}");
            // best effort: still continue cleanup so the app can exit
        }
        finally
        {
            try
            {
                Cleanup();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Shutdown] Cleanup failed: {ex}");
            }    
        }
    }
    
    /// <summary>
    /// Fast, non-blocking cleanup. No IO here (no saving).
    /// Safe to call from Exit handlers too.
    /// </summary>
    public void Cleanup()
    {
        // Guard against double-cleanup (can happen when Closing triggers Close(), then desktop.Exit runs)
        if (Interlocked.Exchange(ref _cleanupOnce, 1) == 1)
            return;
        
        _audioService.StopMusic();
        _audioService.MusicPlaybackEnded -= OnMusicPlaybackEnded;
        
        _fileService.LibraryChanged -= MarkLibraryDirty;
        StopLibraryChangeTracking();
        
        // Detach content VM handlers to avoid leaks.
        DetachSearchAreaHandlers();
        DetachMediaAreaHandlers();
        
        _saveSettingsCts?.Cancel();
        _saveSettingsCts?.Dispose();
        _saveSettingsCts = null;
        
        _saveLibraryCts?.Cancel();
        _saveLibraryCts?.Dispose();
        _saveLibraryCts = null;

        _parentalRefreshCts?.Cancel();
        _parentalRefreshCts?.Dispose();
        _parentalRefreshCts = null;

        _updateContentCts?.Cancel();
        _updateContentCts?.Dispose();
        _updateContentCts = null;
        _updateContentTcs?.TrySetCanceled();
        _updateContentTcs = null;
        
        _gamepadService.OnGuide -= _onGuidePressed;
        _gamepadService.StopMonitoring();
    }

    private void DetachMediaAreaHandlers()
    {
        if (_currentMediaAreaVm == null)
            return;

        // Unsubscribe events to avoid repeated invocations after content switches.
        _currentMediaAreaVm.RequestPlay -= OnMediaAreaRequestPlay;
        _currentMediaAreaVm.PropertyChanged -= OnMediaAreaPropertyChanged;
        _currentMediaAreaVm.FilteredItems.CollectionChanged -= OnMediaAreaFilteredItemsChanged;
        _currentMediaAreaVm.Dispose();

        _currentMediaAreaVm = null;
    }

    private void DetachSearchAreaHandlers()
    {
        if (_currentSearchAreaVm == null)
            return;

        _currentSearchAreaVm.RequestPlay -= OnSearchAreaRequestPlay;
        _currentSearchAreaVm.PropertyChanged -= OnSearchAreaPropertyChanged;
        _currentSearchAreaVm.SearchResults.CollectionChanged -= OnSearchAreaResultsChanged;
        _currentSearchAreaVm.Dispose();
        _currentSearchAreaVm = null;
    }

    private void AttachSearchAreaHandlers(SearchAreaViewModel searchVm)
    {
        _currentSearchAreaVm = searchVm;
        searchVm.RequestPlay += OnSearchAreaRequestPlay;
        searchVm.PropertyChanged += OnSearchAreaPropertyChanged;
        searchVm.SearchResults.CollectionChanged += OnSearchAreaResultsChanged;
    }

    private void OnSearchAreaRequestPlay(MediaItem item)
    {
        _ = PlayMediaAsync(item);
    }

    private void OnSearchAreaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not SearchAreaViewModel searchVm)
            return;

        if (e.PropertyName == nameof(SearchAreaViewModel.ItemWidth))
        {
            ItemWidth = searchVm.ItemWidth;
            SaveSettingsOnly();
            return;
        }

        if (e.PropertyName != nameof(SearchAreaViewModel.SelectedMediaItem))
            return;

        var item = searchVm.SelectedMediaItem;

        OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
        OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));

        if (!_currentSettings.EnableSelectionMusicPreview)
        {
            _audioService.StopMusic();
            return;
        }

        var musicAsset = item?.GetPrimaryAssetPath(AssetType.Music);
        if (!string.IsNullOrEmpty(musicAsset))
        {
            var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(musicAsset);
            if (!string.IsNullOrWhiteSpace(fullPath))
                _ = _audioService.PlayMusicAsync(fullPath);
            else
                _audioService.StopMusic();
        }
        else
            _audioService.StopMusic();
    }

    private void OnSearchAreaResultsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateLibraryGameCounters();
    }

    private void OnMediaAreaFilteredItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateLibraryGameCounters();
    }

    private void UpdateLibraryGameCounters()
    {
        var shownCount = _currentMediaAreaVm?.FilteredItems.Count
                         ?? _currentSearchAreaVm?.SearchResults.Count
                         ?? 0;
        var totalCount = CountAllGames(RootItems);

        if (_currentShownGameCount != shownCount)
        {
            _currentShownGameCount = shownCount;
            OnPropertyChanged(nameof(CurrentShownGameCount));
            OnPropertyChanged(nameof(LibraryGameCountSummary));
        }

        if (_totalLibraryGameCount != totalCount)
        {
            _totalLibraryGameCount = totalCount;
            OnPropertyChanged(nameof(TotalLibraryGameCount));
            OnPropertyChanged(nameof(LibraryGameCountSummary));
            OnPropertyChanged(nameof(ShowLibraryGameCountSummary));
        }
    }

    private static int CountAllGames(IEnumerable<MediaNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            total += node.Items.Count;
            if (node.Children.Count > 0)
                total += CountAllGames(node.Children);
        }

        return total;
    }

    private void OnMediaAreaRequestPlay(MediaItem item)
    {
        _ = PlayMediaAsync(item);
    }

    private void OnMusicPlaybackEnded(string filePath)
    {
        // Process exit happens on a background thread. Marshal to UI thread before touching VM state.
        UiThreadHelper.Post(() => _ = RestartSelectionMusicAsync());
    }

    private async Task RestartSelectionMusicAsync()
    {
        if (!_currentSettings.EnableSelectionMusicPreview)
            return;

        var mediaVm = _currentMediaAreaVm;
        var item = mediaVm?.SelectedMediaItem;

        if (mediaVm == null || item == null)
            return;

        var musicPath = ResolveSelectionMusicPath(mediaVm, item);
        if (string.IsNullOrWhiteSpace(musicPath))
        {
            _audioService.StopMusic();
            return;
        }

        var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(musicPath);
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            _audioService.StopMusic();
            return;
        }

        await _audioService.PlayMusicAsync(fullPath);
    }

    private void OnMediaAreaPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (sender is not MediaAreaViewModel mediaVm)
            return;

        if (args.PropertyName == nameof(MediaAreaViewModel.ItemWidth))
        {
            ItemWidth = mediaVm.ItemWidth;
            SaveSettingsOnly();
            return;
        }

        if (args.PropertyName == nameof(MediaAreaViewModel.SelectedMediaItem))
        {
            var item = mediaVm.SelectedMediaItem;
            _currentSettings.LastSelectedMediaId = item?.Id;
            SaveSettingsOnly();

            if (item != null)
                _lastSelectedMediaByNodeId[mediaVm.Node.Id] = item.Id;

            if (SelectedNode != null)
                UpdateBigModeStateFromCoreSelection(SelectedNode, item);

            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));

            // Respect user preference for automatic selection-based music preview
            if (item != null && _currentSettings.EnableSelectionMusicPreview)
            {
                var musicPath = ResolveSelectionMusicPath(mediaVm, item);
                if (!string.IsNullOrEmpty(musicPath))
                {
                    var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(musicPath);
                    if (!string.IsNullOrWhiteSpace(fullPath))
                    {
                        _ = _audioService.PlayMusicAsync(fullPath);
                        return;
                    }
                }
            }

            // Either no item, no music asset, or preview disabled -> ensure music is stopped
            _audioService.StopMusic();
        }
    }

    private string? ResolveSelectionMusicPath(MediaAreaViewModel mediaVm, MediaItem item)
    {
        if (!IsRandomizeMusicActive(mediaVm.Node))
            return item.GetPrimaryAssetPath(AssetType.Music);

        var musicAssets = item.Assets
            .Where(a => a.Type == AssetType.Music && !string.IsNullOrWhiteSpace(a.RelativePath))
            .Select(a => a.RelativePath)
            .ToList();

        if (musicAssets.Count == 0)
            return null;

        if (musicAssets.Count == 1)
            return musicAssets[0];

        var current = item.GetPrimaryAssetPath(AssetType.Music);
        var candidates = musicAssets;

        if (!string.IsNullOrWhiteSpace(current))
        {
            candidates = musicAssets
                .Where(path => !string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var picked = RandomHelper.PickRandom(candidates) ?? RandomHelper.PickRandom(musicAssets);
        if (!string.IsNullOrWhiteSpace(picked))
            item.SetActiveAsset(AssetType.Music, picked);

        return picked;
    }

    /// <summary>
    /// Opens the given manual/document asset with the system's default viewer.
    /// Best-effort only: invalid assets or missing files are ignored silently
    /// </summary>
    /// <param name="asset">Manual asset to open (Type must be Manual).</param>
    private void OpenManual(MediaAsset? asset)
    {
        if (asset is not { Type: AssetType.Manual } ||
            string.IsNullOrWhiteSpace(asset.RelativePath))
        {
            return;
        }

        try
        {
            var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(asset.RelativePath);
            if (!string.IsNullOrWhiteSpace(fullPath))
                _documentService.OpenDocument(fullPath);
        }
        catch
        {
            // Best-effort: opening manuals must never crash the UI.
        }
    }
    
    /// <summary>
    /// Converts all absolute launch file paths that are located under AppPaths.DataRoot
    /// into LibraryRelative paths, then persists the updated library
    /// This is intended as a one-time operation when switching to a portable setup
    /// </summary>
    private async Task ConvertLaunchPathsToPortableAsync()
    {
        if (RootItems.Count == 0)
            return;

        try
        {
            int migrated = 0;
            ObservableCollection<MediaNode>? snapshot = null;

            await UiThreadHelper.InvokeAsync(() =>
            {
                migrated = LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(RootItems);
                if (migrated > 0)
                    snapshot = _dataService.CreateSnapshot(RootItems);
            });

            if (migrated <= 0)
            {
                Console.WriteLine("[Migration] No launch file paths needed conversion.");
                return;
            }

            Console.WriteLine($"[Migration] Converted {migrated} launch file paths to LibraryRelative.");
            var json = await Task.Run(() => _dataService.Serialize(snapshot!)).ConfigureAwait(false);
            await _dataService.SaveJsonAsync(json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Migration] ConvertLaunchPathsToPortableAsync failed: {ex}");
        }
    }
    
    // --- Content Logic (The heart of the UI) ---
    private void UpdateContent()
    {
        _audioService.StopMusic();
    
        // Cancel any previously running content update.
        _updateContentCts?.Cancel();
        _updateContentCts?.Dispose();
        _updateContentCts = new CancellationTokenSource();
        var token = _updateContentCts.Token;

        _updateContentTcs?.TrySetCanceled();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _updateContentTcs = tcs;

        // Capture current filters before we dispose the old VM (so edits don't reset UI filters).
        var previousVm = _currentMediaAreaVm;
        var previousNodeId = previousVm?.Node?.Id;
        var previousSearchText = previousVm?.SearchText ?? string.Empty;
        var previousOnlyFavorites = previousVm?.OnlyFavorites ?? false;
        var previousStatus = previousVm?.SelectedStatus;

        // Any time we rebuild content, detach handlers from the previous VM.
        DetachSearchAreaHandlers();
        DetachMediaAreaHandlers();
        
        if (SelectedNode is null)
        {
            SelectedNodeContent = null;
            tcs.TrySetResult(true);
            return;
        }

        // Capture the node we want to load to prevent race conditions
        var nodeToLoad = SelectedNode;
        var filterProtected = IsParentalFilterActive;

        // Run the heavy collection logic in a background task
        var updateTask = Task.Run(async () =>
        {
            try
            {
                if (token.IsCancellationRequested) return;

                // Build a lightweight description of what we want to display:
                // - the flat item list for this node (including children)
                // - and the randomization plan for cover/wallpaper/music.
                var (allItems, randomizationPlan) =
                    await BuildDisplayItemsWithRandomizationAsync(nodeToLoad, token, filterProtected);

                if (token.IsCancellationRequested) return;

                // Create the display node used by MediaAreaViewModel.
                var displayNode = new MediaNode(nodeToLoad.Name, nodeToLoad.Type)
                {
                    Id = nodeToLoad.Id,
                    Items = new ObservableCollection<MediaItem>(allItems),
                    Assets = nodeToLoad.Assets,
                    LogoFallbackEnabled = nodeToLoad.LogoFallbackEnabled,
                    WallpaperFallbackEnabled = nodeToLoad.WallpaperFallbackEnabled,
                    VideoFallbackEnabled = nodeToLoad.VideoFallbackEnabled,
                    MarqueeFallbackEnabled = nodeToLoad.MarqueeFallbackEnabled
                };

                // Switch to UI thread once (apply randomization + build VM)
                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }
                    if (SelectedNode != nodeToLoad)
                    {
                        tcs.TrySetCanceled();
                        return;
                    }

                    ApplyRandomizationPlan(randomizationPlan);

                    var mediaVm = new MediaAreaViewModel(displayNode, ItemWidth);

                    if (previousVm != null && previousNodeId == nodeToLoad.Id)
                    {
                        mediaVm.OnlyFavorites = previousOnlyFavorites;
                        mediaVm.SelectedStatus = previousStatus;
                        mediaVm.SearchText = previousSearchText;
                    }

                    _currentMediaAreaVm = mediaVm;
                    mediaVm.RequestPlay += OnMediaAreaRequestPlay;
                    mediaVm.PropertyChanged += OnMediaAreaPropertyChanged;
                    mediaVm.FilteredItems.CollectionChanged += OnMediaAreaFilteredItemsChanged;

                    string? itemIdToSelect = null;
                    var hadNodeSelection = false;

                    if (_lastSelectedMediaByNodeId.TryGetValue(nodeToLoad.Id, out var cachedId))
                    {
                        itemIdToSelect = cachedId;
                        hadNodeSelection = true;
                    }
                    else if (!string.IsNullOrEmpty(_currentSettings.LastSelectedMediaId))
                    {
                        itemIdToSelect = _currentSettings.LastSelectedMediaId;
                    }

                    if (!string.IsNullOrEmpty(itemIdToSelect))
                    {
                        var itemToSelect = displayNode.Items
                            .FirstOrDefault(i => i.Id == itemIdToSelect);
                        if (itemToSelect != null)
                        {
                            mediaVm.SelectedMediaItem = itemToSelect;
                        }
                        else if (hadNodeSelection)
                        {
                            _lastSelectedMediaByNodeId.Remove(nodeToLoad.Id);
                        }
                    }

                    SelectedNodeContent = mediaVm;
                    tcs.TrySetResult(true);
                });
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                throw;
            }
        }, token);
        
        updateTask.ContinueWith(t =>
        {
            if (t.IsCanceled)
            {
                tcs.TrySetCanceled();
                return;
            }

            if (t.Exception == null) return;

            tcs.TrySetException(t.Exception);
            Debug.WriteLine($"[UpdateContent] Background task failed: {t.Exception}");
        }, TaskContinuationOptions.ExecuteSynchronously);
    }
    
    /// <summary>
    /// Builds the flat item list for the given node and precomputes
    /// which cover, wallpaper and music assets should be active for
    /// each item according to the node's randomization flags.
    /// This method does no UI work and can run off the UI thread.
    /// </summary>
    private sealed record AssetSnapshot(AssetType Type, string RelativePath);

    private async Task<(List<MediaItem> Items,
            List<(MediaItem Item, string? Cover, string? Wallpaper, string? Music)> RandomizationPlan)>
        BuildDisplayItemsWithRandomizationAsync(MediaNode node, CancellationToken token, bool filterProtected)
    {
        // 1. Collect items (recursive) using UI-thread snapshots for safety.
        var itemList = new List<MediaItem>();
        await CollectItemsRecursiveSnapshotAsync(node, itemList, token, filterProtected);

        token.ThrowIfCancellationRequested();

        // Globally sort by SortTitle (fallback: Title) so aggregated views
        // keep a user-defined series order across subcategories.
        itemList.Sort(MediaSortHelper.DisplayOrderComparer);

        // 2. Randomization logic (covers/wallpapers/music)
        bool randomizeMusic = false;
        bool randomizeCovers = false;

        // Snapshot assets on the UI thread so background work never enumerates UI-bound collections.
        var assetSnapshots = new List<(MediaItem Item, List<AssetSnapshot> Assets)>(itemList.Count);
        await UiThreadHelper.InvokeAsync(() =>
        {
            randomizeMusic = IsRandomizeMusicActive(node);
            randomizeCovers = IsRandomizeActive(node);

            if (!randomizeCovers && !randomizeMusic)
                return;

            foreach (var item in itemList)
            {
                var assets = item.Assets
                    .Select(a => new AssetSnapshot(a.Type, a.RelativePath))
                    .ToList();

                assetSnapshots.Add((item, assets));
            }
        });

        var randomizationPlan =
            new List<(MediaItem Item, string? Cover, string? Wallpaper, string? Music)>(
                capacity: itemList.Count);

        if (!randomizeCovers && !randomizeMusic)
        {
            // Ensure any previous randomization overrides are cleared.
            for (int i = 0; i < itemList.Count; i++)
                randomizationPlan.Add((itemList[i], null, null, null));

            return (itemList, randomizationPlan);
        }

        foreach (var snapshot in assetSnapshots)
        {
            token.ThrowIfCancellationRequested();

            var item = snapshot.Item;
            var assets = snapshot.Assets;

            List<AssetSnapshot>? covers = null;
            List<AssetSnapshot>? wallpapers = null;
            List<AssetSnapshot>? music = null;

            foreach (var asset in assets)
            {
                if (randomizeCovers)
                {
                    if (asset.Type == AssetType.Cover)
                        (covers ??= new()).Add(asset);
                    else if (asset.Type == AssetType.Wallpaper)
                        (wallpapers ??= new()).Add(asset);
                }

                if (randomizeMusic && asset.Type == AssetType.Music)
                    (music ??= new()).Add(asset);
            }

            string? coverWinner = null;
            string? wallpaperWinner = null;
            string? musicWinner = null;

            if (randomizeCovers)
            {
                if (covers is { Count: > 1 })
                    coverWinner = RandomHelper.PickRandom(covers)?.RelativePath;

                if (wallpapers is { Count: > 1 })
                    wallpaperWinner = RandomHelper.PickRandom(wallpapers)?.RelativePath;
            }

            if (randomizeMusic && music is { Count: > 1 })
                musicWinner = RandomHelper.PickRandom(music)?.RelativePath;

            randomizationPlan.Add((item, coverWinner, wallpaperWinner, musicWinner));
        }

        return (itemList, randomizationPlan);
    }

    private async Task CollectItemsRecursiveSnapshotAsync(
        MediaNode node,
        List<MediaItem> targetList,
        CancellationToken token,
        bool filterProtected)
    {
        token.ThrowIfCancellationRequested();

        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            items = node.Items.ToList();
            children = node.Children.ToList();
        });

        if (filterProtected)
            targetList.AddRange(items.Where(item => !item.IsProtected));
        else
            targetList.AddRange(items);

        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            await CollectItemsRecursiveSnapshotAsync(child, targetList, token, filterProtected);
        }
    }

    /// <summary>
    /// Applies the precomputed randomization winners to the given items.
    /// Must be called on the UI thread because it mutates model objects
    /// that are bound directly into the view.
    /// </summary>
    private static void ApplyRandomizationPlan(
        List<(MediaItem Item, string? Cover, string? Wallpaper, string? Music)> randomizationPlan)
    {
        foreach (var plan in randomizationPlan)
        {
            plan.Item.ResetActiveAssets();

            if (!string.IsNullOrEmpty(plan.Cover))
                plan.Item.SetActiveAsset(AssetType.Cover, plan.Cover);

            if (!string.IsNullOrEmpty(plan.Wallpaper))
                plan.Item.SetActiveAsset(AssetType.Wallpaper, plan.Wallpaper);

            if (!string.IsNullOrEmpty(plan.Music))
                plan.Item.SetActiveAsset(AssetType.Music, plan.Music);
        }
    }
}
