using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    
    // Mockable StorageProvider for Unit Tests
    public IStorageProvider? StorageProvider { get; set; }
    
    // Command to open per-item manuals/documents with the system viewer.
    public IRelayCommand<MediaAsset?> OpenManualCommand { get; private set; } = null!;

    private DateTime _lastGuideHandledUtc = DateTime.MinValue;

    // Debounced Settings Save
    private CancellationTokenSource? _saveSettingsCts;
    private readonly TimeSpan _saveSettingsDebounce = TimeSpan.FromMilliseconds(500);

    // --- Library Dirty Tracking + Debounced Library Save (NEW) ---
    private bool _isLibraryDirty;
    private int _libraryDirtyVersion;
    
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
        set => SetProperty(ref _rootItems, value);
    }

    public MediaNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                UpdateContent();
            
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
        set => SetProperty(ref _selectedNodeContent, value);
    }

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

    public IBrush PanelBackground => IsDarkTheme ? new SolidColorBrush(Color.Parse("#CC252526")) : new SolidColorBrush(Color.Parse("#CCF5F5F5"));
    public IBrush TextColor => IsDarkTheme ? Brushes.White : Brushes.Black;
    public IBrush WindowBackground => IsDarkTheme ? new SolidColorBrush(Color.Parse("#252526")) : Brushes.WhiteSmoke;

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
        try 
        {
            RootItems = await _dataService.LoadAsync();
            Debug.WriteLine("[DEBUG] LoadData: RootItems loaded. Count = " + RootItems.Count);
        
            _isLibraryDirty = false;
            _libraryDirtyVersion = 0;
            
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(PanelBackground));
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(WindowBackground));
    
            if (Application.Current != null)
                Application.Current.RequestedThemeVariant = _currentSettings.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

            await RescanAllAssetsAsync();
            SortAllNodesRecursive(RootItems);

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
                if (node != null) { SelectedNode = node; ExpandPathToNode(RootItems, node); }
                else if (RootItems.Count > 0) SelectedNode = RootItems[0];
            }
            else if (RootItems.Count > 0) SelectedNode = RootItems[0];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ViewModel] LoadData Error: {ex}");
        }
    }

    private bool SanitizeSelectionState()
    {
        bool changed = false;

        MediaNode? selectedNode = null;
        if (!string.IsNullOrWhiteSpace(_currentSettings.LastSelectedNodeId))
        {
            selectedNode = FindNodeById(RootItems, _currentSettings.LastSelectedNodeId);
            if (selectedNode == null)
            {
                _currentSettings.LastSelectedNodeId = null;
                changed = true;
            }
        }

        if (selectedNode == null)
        {
            if (!string.IsNullOrWhiteSpace(_currentSettings.LastSelectedMediaId))
            {
                _currentSettings.LastSelectedMediaId = null;
                changed = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_currentSettings.LastSelectedMediaId))
        {
            if (!IsMediaIdInNodeSubtree(selectedNode, _currentSettings.LastSelectedMediaId!))
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
                if (_currentSettings.LastBigModeWasItemView)
                {
                    var itemId = _currentSettings.LastBigModeSelectedNodeId;
                    if (pathEndNode == null || string.IsNullOrWhiteSpace(itemId) ||
                        !pathEndNode.Items.Any(i => i.Id == itemId))
                    {
                        _currentSettings.LastBigModeWasItemView = false;
                        _currentSettings.LastBigModeSelectedNodeId = pathEndNode?.Id;
                        changed = true;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(_currentSettings.LastBigModeSelectedNodeId))
                {
                    if (!currentLevel.Any(n => n.Id == _currentSettings.LastBigModeSelectedNodeId))
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
    
        // Wait until SelectedNodeContent is set (simple polling with timeout).
        int timeout = 5000;  // 5 seconds max wait.
        int delay = 100;
        while (SelectedNodeContent == null && timeout > 0)
        {
            await Task.Delay(delay);
            timeout -= delay;
        }
    
        if (SelectedNodeContent == null)
        {
            Debug.WriteLine("[DEBUG] UpdateContentAsync: Timeout - SelectedNodeContent not set.");
        }
    }
    
    public async Task SaveData()
    {
        // SaveData is a “strong” save: when someone calls it explicitly,
        // we persist the library (if dirty) + settings (immediately).
        await SaveLibraryIfDirtyAsync(force: false).ConfigureAwait(false);
        await _settingsService.SaveAsync(_currentSettings).ConfigureAwait(false);
    }

    private void MarkLibraryDirty()
    {
        _isLibraryDirty = true;
        _libraryDirtyVersion++;

        DebouncedSaveLibrary();
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
                if (token.IsCancellationRequested) return;

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
            await _dataService.SaveAsync(RootItems).ConfigureAwait(false);

            // Only mark the library as clean if no new changes happened during this save.
            if (expectedVersion.HasValue)
            {
                if (expectedVersion.Value == _libraryDirtyVersion)
                    _isLibraryDirty = false;
            }
            else
            {
                // Force/normal path: after a successful save we are clean.
                _isLibraryDirty = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Library] Save failed: {ex.Message}");
            // Keep dirty=true so a later attempt can retry the save.
            _isLibraryDirty = true;
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
                if (token.IsCancellationRequested) return;

                await _settingsService.SaveAsync(_currentSettings);
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

        try
        {
            await SaveLibraryIfDirtyAsync(force: true).ConfigureAwait(false);
            await _settingsService.SaveAsync(_currentSettings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Shutdown] Final save failed: {ex.Message}");
            // best effort: still continue cleanup so the app can exit
        }

        Cleanup();
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
        
        _fileService.LibraryChanged -= MarkLibraryDirty;
        
        // Detach content VM handlers to avoid leaks.
        DetachMediaAreaHandlers();
        
        _saveSettingsCts?.Cancel();
        _saveSettingsCts?.Dispose();
        _saveSettingsCts = null;
        
        _saveLibraryCts?.Cancel();
        _saveLibraryCts?.Dispose();
        _saveLibraryCts = null;
        
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

        _currentMediaAreaVm = null;
    }

    private void OnMediaAreaRequestPlay(MediaItem item)
    {
        _ = PlayMediaAsync(item);
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

            if (SelectedNode != null)
                UpdateBigModeStateFromCoreSelection(SelectedNode, item);

            // Respect user preference for automatic selection-based music preview
            if (item != null && _currentSettings.EnableSelectionMusicPreview)
            {
                var musicPath = item.GetPrimaryAssetPath(AssetType.Music);
                if (!string.IsNullOrEmpty(musicPath))
                {
                    var fullPath = AppPaths.ResolveDataPath(musicPath);
                    _ = _audioService.PlayMusicAsync(fullPath);
                    return;
                }
            }

            // Either no item, no music asset, or preview disabled -> ensure music is stopped
            _audioService.StopMusic();
        }
    }

    /// <summary>
    /// Opens the given manual/document asset with the system's default viewer.
    /// Best-effort only: invalid assets or missing files are ignored silently
    /// </summary>
    /// <param name="asset">Manual asset to open (Type must be Manual).</param>
    private void OpenManual(MediaAsset? asset)
    {
        if (asset == null)
            return;

        if (asset.Type != AssetType.Manual)
            return;

        if (string.IsNullOrWhiteSpace(asset.RelativePath))
            return;

        try
        {
            var fullPath = AppPaths.ResolveDataPath(asset.RelativePath);
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
    public async Task ConvertLaunchPathsToPortableAsync()
    {
        if (RootItems == null || RootItems.Count == 0)
            return;

        try
        {
            var migrated = LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(RootItems);
            if (migrated <= 0)
            {
                Debug.WriteLine("[Migration] No launch file paths needed conversion.");
                return;
            }

            Debug.WriteLine($"[Migration] Converted {migrated} launch file paths to LibraryRelative.");
            await _dataService.SaveAsync(RootItems);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Migration] ConvertLaunchPathsToPortableAsync failed: {ex}");
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

        // Any time we rebuild content, detach handlers from the previous VM.
        DetachMediaAreaHandlers();
        
        if (SelectedNode is null)
        {
            SelectedNodeContent = null;
            return;
        }

        // Capture the node we want to load to prevent race conditions
        var nodeToLoad = SelectedNode;

        // Run the heavy collection logic in a background task
        var updateTask = Task.Run(async () =>
        {
            if (token.IsCancellationRequested) return;

            // Build a lightweight description of what we want to display:
            // - the flat item list for this node (including children)
            // - and the randomization plan for cover/wallpaper/music.
            var (allItems, randomizationPlan) =
                await BuildDisplayItemsWithRandomizationAsync(nodeToLoad, token);

            if (token.IsCancellationRequested) return;

            // Create the display node used by MediaAreaViewModel.
            var displayNode = new MediaNode(nodeToLoad.Name, nodeToLoad.Type)
            {
                Id = nodeToLoad.Id,
                Items = new ObservableCollection<MediaItem>(allItems)
            };

            // Switch to UI thread once (apply randomization + build VM)
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested) return;
                if (SelectedNode != nodeToLoad) return;

                ApplyRandomizationPlan(randomizationPlan);

                var mediaVm = new MediaAreaViewModel(displayNode, ItemWidth);

                _currentMediaAreaVm = mediaVm;
                mediaVm.RequestPlay += OnMediaAreaRequestPlay;
                mediaVm.PropertyChanged += OnMediaAreaPropertyChanged;

                if (!string.IsNullOrEmpty(_currentSettings.LastSelectedMediaId))
                {
                    var itemToSelect = displayNode.Items
                        .FirstOrDefault(i => i.Id == _currentSettings.LastSelectedMediaId);
                    if (itemToSelect != null)
                        mediaVm.SelectedMediaItem = itemToSelect;
                }

                SelectedNodeContent = mediaVm;
            });
        }, token);
        
        updateTask.ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (t.Exception == null) return;

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
        BuildDisplayItemsWithRandomizationAsync(MediaNode node, CancellationToken token)
    {
        // 1. Collect items (recursive) using UI-thread snapshots for safety.
        var itemList = new List<MediaItem>();
        await CollectItemsRecursiveSnapshotAsync(node, itemList, token);

        token.ThrowIfCancellationRequested();

        // Globally sort all collected items by title so aggregated views (root/areas)
        // are truly alphabetical across all subcategories
        itemList.Sort(static (a, b) =>
            string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

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
            return (itemList, randomizationPlan);

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

    private async Task CollectItemsRecursiveSnapshotAsync(MediaNode node, List<MediaItem> targetList, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        List<MediaItem> items = new();
        List<MediaNode> children = new();

        await UiThreadHelper.InvokeAsync(() =>
        {
            items = node.Items.ToList();
            children = node.Children.ToList();
        });

        targetList.AddRange(items);

        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            await CollectItemsRecursiveSnapshotAsync(child, targetList, token);
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
