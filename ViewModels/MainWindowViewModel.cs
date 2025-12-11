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

    // Token Source for cancelling old content loading tasks
    private CancellationTokenSource? _updateContentCts;
    
    // Merker für den Start-Modus
    private bool _startInBigMode = false;
    
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
    
    // Mockable StorageProvider for Unit Tests
    public IStorageProvider? StorageProvider { get; set; }

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
        _currentSettings = preloadedSettings;
        
        // JOKER: SDL zwingen, auch Hintergrund-Events und unbekannte Geräte zu akzeptieren
        Environment.SetEnvironmentVariable("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        Environment.SetEnvironmentVariable("SDL_LINUX_JOYSTICK_DEADZONES", "1");

        // Service erstellen & starten
        _gamepadService = new GamepadService();
        _gamepadService.StartMonitoring();
    
        InitializeCommands();
    }

    // Neue Methode, um den Modus von außen zu setzen (bevor LoadData läuft)
    public void SetStartMode(bool bigMode)
    {
        _startInBigMode = bigMode;
    }
    
    // --- Persistence & Lifecycle ---

    public async Task LoadData()
    {
        try 
        {
            RootItems = await _dataService.LoadAsync();
        
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
            
            // Automatischer Start in den Big Mode
            if (_startInBigMode)
            {
                // Wir warten kurz, damit das UI "atmen" kann und SelectedNode gesetzt ist
                await Dispatcher.UIThread.InvokeAsync(() => 
                {
                    EnterBigModeCommand.Execute(null);
                }, DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ViewModel] LoadData Error: {ex}");
        }
    }

    public async Task SaveData()
    {
        await _dataService.SaveAsync(RootItems);
        await _settingsService.SaveAsync(_currentSettings);
    }

    private async void SaveSettingsOnly()
    {
        await _settingsService.SaveAsync(_currentSettings);
    }

    public void Cleanup()
    {
        _audioService.StopMusic();
    }

    // --- Content Logic (The heart of the UI) ---
    private void UpdateContent()
    {
        _audioService.StopMusic();
    
        // Cancel old task if running
        _updateContentCts?.Cancel();
        _updateContentCts = new CancellationTokenSource();
        var token = _updateContentCts.Token;

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
        
            // 1. Collect Items (Heavy recursion)
            var itemList = new System.Collections.Generic.List<MediaItem>();
            CollectItemsRecursive(nodeToLoad, itemList); 
            
            if (token.IsCancellationRequested) return;

            // 2. Randomization Logic (Covers/Music)
            
            bool randomizeMusic = IsRandomizeMusicActive(nodeToLoad);
            bool randomizeCovers = IsRandomizeActive(nodeToLoad);
            
            foreach (var item in itemList)
            {
                if (token.IsCancellationRequested) return;

                // Erst mal alles zurücksetzen (Standard-Bild)
                // Da wir das im Background machen und ResetActiveAssets jetzt Dispatcher nutzt,
                // feuert es UI Events korrekt asynchron.
                item.ResetActiveAssets(); 

                if (randomizeCovers) 
                {
                    // --- COVERS ---
                    var covers = item.Assets.Where(a => a.Type == AssetType.Cover).ToList();
                    
                    // Nur randomisieren wenn Auswahl da ist (> 1)
                    if (covers.Count > 1)
                    {
                        var winner = RandomHelper.PickRandom(covers);
                        if (winner != null)
                        {
                            item.SetActiveAsset(AssetType.Cover, winner.RelativePath);
                        }
                    }

                    // --- WALLPAPERS (NEU) ---
                    var wallpapers = item.Assets.Where(a => a.Type == AssetType.Wallpaper).ToList();
                    
                    if (wallpapers.Count > 1)
                    {
                        var winner = RandomHelper.PickRandom(wallpapers);
                        if (winner != null)
                        {
                            item.SetActiveAsset(AssetType.Wallpaper, winner.RelativePath);
                        }
                    }
                }

                if (randomizeMusic)
                {
                    var musicFiles = item.Assets.Where(a => a.Type == AssetType.Music).ToList();
                    if (musicFiles.Count > 1)
                    {
                        var winner = RandomHelper.PickRandom(musicFiles);
                        if (winner != null)
                        {
                            item.SetActiveAsset(AssetType.Music, winner.RelativePath);
                        }
                    }
                }
            }
        
            if (token.IsCancellationRequested) return;

            // 3. Prepare Display Data
            var allItems = new ObservableCollection<MediaItem>(itemList);
            
            var displayNode = new MediaNode(nodeToLoad.Name, nodeToLoad.Type)
            {
                Id = nodeToLoad.Id,
                Items = allItems
            };

            // 4. Switch to UI Thread ONE TIME to update the View
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Guard Clause: If the user selected a different node while we were working, abort.
                if (SelectedNode != nodeToLoad) return;

                var mediaVm = new MediaAreaViewModel(displayNode, ItemWidth);

                // Wire up events
                mediaVm.RequestPlay += item => 
                { 
                     // Fire and forget is acceptable here for UI event binding
                     _ = PlayMediaAsync(item); 
                };
                
                mediaVm.PropertyChanged += (sender, args) =>
                {
                    // Persist Zoom
                    if (args.PropertyName == nameof(MediaAreaViewModel.ItemWidth))
                    {
                        ItemWidth = mediaVm.ItemWidth;
                        SaveSettingsOnly();
                    }

                    // Handle Selection Change in the Grid
                    if (args.PropertyName == nameof(MediaAreaViewModel.SelectedMediaItem))
                    {
                        var item = mediaVm.SelectedMediaItem;
                        _currentSettings.LastSelectedMediaId = item?.Id;
                        SaveSettingsOnly();
                            
                        // Play Music on selection - NEU via AssetSystem
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
                };

                // Restore last selected media item
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