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
    
        InitializeCommands();
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
            // PERFORMANCE: We do this on the local list BEFORE creating ObservableCollection or binding to UI.
            // This avoids thousands of Dispatcher.Invoke calls.
            
            bool randomizeMusic = IsRandomizeMusicActive(nodeToLoad);
            bool randomizeCovers = IsRandomizeActive(nodeToLoad);
            
            if (randomizeCovers || randomizeMusic)
            {
                // Get node path once
                var nodePath = PathHelper.GetNodePath(nodeToLoad, RootItems);
            
                foreach (var item in itemList)
                {
                    if (token.IsCancellationRequested) return;
                
                    // Covers Randomization
                    if (randomizeCovers) 
                    {
                        var validCovers = _fileService.GetAvailableAssets(item, nodePath, MediaFileType.Cover);
                        var rndImg = RandomHelper.PickRandom(validCovers);
                        
                        // It is safe to modify 'item' here because it is not yet bound to the active UI in this context
                        // (itemList is a local list, though MediaItem instances might be shared, 
                        // but usually they are not displayed in DetailView while switching nodes)
                        if (rndImg != null && rndImg != item.CoverPath)
                        {
                            item.CoverPath = rndImg;
                        }
                    }

                    // Music Randomization
                    if (randomizeMusic)
                    {
                        var validMusic = _fileService.GetAvailableAssets(item, nodePath, MediaFileType.Music);
                        var rndAudio = RandomHelper.PickRandom(validMusic);
        
                        if (rndAudio != null && rndAudio != item.MusicPath)
                        {
                            item.MusicPath = rndAudio;
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
                            
                        // Play Music on selection
                        if (item != null && !string.IsNullOrEmpty(item.MusicPath))
                        {
                            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath);
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