using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    
    // Keeps the currently active content VM so we can detach event handlers (prevents leaks).
    private MediaAreaViewModel? _currentMediaAreaVm;
    
    // Mockable StorageProvider for Unit Tests
    public IStorageProvider? StorageProvider { get; set; }

    private DateTime _lastGuideHandledUtc = DateTime.MinValue;

    // Debounced Settings Save
    private CancellationTokenSource? _saveSettingsCts;
    private readonly TimeSpan _saveSettingsDebounce = TimeSpan.FromMilliseconds(500);

    // --- Library Dirty Tracking + Debounced Library Save (NEW) ---
    private bool _isLibraryDirty;
    private int _libraryDirtyVersion;

    private CancellationTokenSource? _saveLibraryCts;
    private readonly TimeSpan _saveLibraryDebounce = TimeSpan.FromMilliseconds(800);
    
    public bool ShouldIgnoreBackKeyTemporarily()
        => (DateTime.UtcNow - _lastGuideHandledUtc) < TimeSpan.FromMilliseconds(600);
    
    // Helper to access the current window for dialogs
    private Window? CurrentWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

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
                    
                    // BigMode-State spiegeln (CoreApp -> BigMode)
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
        AppSettings preloadedSettings) 
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
        _currentSettings = preloadedSettings;
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
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
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

    // Helper, um UpdateContent awaitable zu machen (wandelt den Background-Task in awaitable Task um)
    private async Task UpdateContentAsync()
    {
        UpdateContent();  // Starte den bestehenden Background-Task
    
        // Warte, bis SelectedNodeContent gesetzt ist (einfaches Polling mit Timeout)
        int timeout = 5000;  // 5 Sekunden Max-Wartezeit
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
        // SaveData ist „stark“: wenn jemand es explizit aufruft,
        // speichern wir die Library (nur wenn dirty) + Settings (sofort).
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

        // Wenn wir aus einem Debounce-Run kommen, aber inzwischen neue Änderungen passiert sind,
        // sparen wir uns den Save (der nächste Debounce-Lauf übernimmt).
        if (expectedVersion.HasValue && expectedVersion.Value != _libraryDirtyVersion)
            return;

        try
        {
            await _dataService.SaveAsync(RootItems).ConfigureAwait(false);

            // Nur „clean“ setzen, wenn seit dem Start dieses Saves nichts Neues passiert ist.
            if (expectedVersion.HasValue)
            {
                if (expectedVersion.Value == _libraryDirtyVersion)
                    _isLibraryDirty = false;
            }
            else
            {
                // Force/normal path: nach Save ist clean.
                _isLibraryDirty = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Library] Save failed: {ex.Message}");
            // Dirty bleibt true, damit wir später nochmal versuchen können.
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

        // Unwire events to avoid repeated invocations after content switches.
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

            var musicPath = item?.GetPrimaryAssetPath(AssetType.Music);
            if (!string.IsNullOrEmpty(musicPath))
            {
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, musicPath);
                _ = _audioService.PlayMusicAsync(fullPath);
            }
            else
            {
                _audioService.StopMusic();
            }
        }
    }
    
    // --- Content Logic (The heart of the UI) ---
    private void UpdateContent()
    {
        _audioService.StopMusic();
    
        // Cancel old task if running
        _updateContentCts?.Cancel();
        _updateContentCts = new CancellationTokenSource();
        var token = _updateContentCts.Token;

        // Any time we rebuild content, detach handlers from the previous VM.
        DetachMediaAreaHandlers();
        
        if (SelectedNode is null || SelectedNode.Type == NodeType.Area)
        {
            SelectedNodeContent = null;
            return;
        }

        // Capture the node we want to load to prevent race conditions
        var nodeToLoad = SelectedNode;

        // Run the heavy collection logic in a background task
        Task.Run(async () => 
        {
            if (token.IsCancellationRequested) return;
        
            // 1. Collect items (recursive)
            var itemList = new System.Collections.Generic.List<MediaItem>();
            CollectItemsRecursive(nodeToLoad, itemList); 
            
            if (token.IsCancellationRequested) return;

            // 2. Randomization Logic (covers/wallpapers/music)
            bool randomizeMusic = IsRandomizeMusicActive(nodeToLoad);
            bool randomizeCovers = IsRandomizeActive(nodeToLoad);
            
            foreach (var item in itemList)
            {
                if (token.IsCancellationRequested) return;

                item.ResetActiveAssets(); 

                if (!randomizeCovers && !randomizeMusic)
                    continue;

                System.Collections.Generic.List<MediaAsset>? covers = null;
                System.Collections.Generic.List<MediaAsset>? wallpapers = null;
                System.Collections.Generic.List<MediaAsset>? music = null;

                // Single pass over assets (avoids multiple LINQ allocations per item).
                foreach (var asset in item.Assets)
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
                
                if (randomizeCovers)
                {
                    if (covers is { Count: > 1 })
                    {
                        var winner = RandomHelper.PickRandom(covers);
                        if (winner != null)
                            item.SetActiveAsset(AssetType.Cover, winner.RelativePath);
                    }

                    if (wallpapers is { Count: > 1 })
                    {
                        var winner = RandomHelper.PickRandom(wallpapers);
                        if (winner != null)
                            item.SetActiveAsset(AssetType.Wallpaper, winner.RelativePath);
                    }
                }

                if (randomizeMusic && music is { Count: > 1 })
                {
                    var winner = RandomHelper.PickRandom(music);
                    if (winner != null)
                        item.SetActiveAsset(AssetType.Music, winner.RelativePath);
                }
            }
        
            if (token.IsCancellationRequested) return;

            // 3. Prepare display node
            var allItems = new ObservableCollection<MediaItem>(itemList);
            
            var displayNode = new MediaNode(nodeToLoad.Name, nodeToLoad.Type)
            {
                Id = nodeToLoad.Id,
                Items = allItems
            };

            // 4. Switch to UI thread once
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedNode != nodeToLoad) return;

                var mediaVm = new MediaAreaViewModel(displayNode, ItemWidth);

                _currentMediaAreaVm = mediaVm;
                mediaVm.RequestPlay += OnMediaAreaRequestPlay;
                mediaVm.PropertyChanged += OnMediaAreaPropertyChanged;
                
                if (!string.IsNullOrEmpty(_currentSettings.LastSelectedMediaId))
                {
                    var itemToSelect = allItems.FirstOrDefault(i => i.Id == _currentSettings.LastSelectedMediaId);
                    if (itemToSelect != null) mediaVm.SelectedMediaItem = itemToSelect;
                }

                SelectedNodeContent = mediaVm;
            });
        });
    }
}