using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

/// <summary>
/// Main ViewModel acting as the orchestrator for the application.
/// Manages the navigation tree, selected content, and invokes services for specific tasks.
/// </summary>
public class MainWindowViewModel : ViewModelBase
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

    // --- Commands ---
    public ICommand AddCategoryCommand { get; }
    public ICommand AddMediaCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SetCoverCommand { get; }
    public ICommand SetLogoCommand { get; }
    public ICommand SetWallpaperCommand { get; }
    public ICommand SetMusicCommand { get; }
    public ICommand EditMediaCommand { get; }
    public ICommand DeleteMediaCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand EditNodeCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ImportRomsCommand { get; }
    public ICommand ImportSteamCommand { get; }
    public ICommand ImportGogCommand { get; }
    public ICommand ScrapeMediaCommand { get; }
    public ICommand ScrapeNodeCommand { get; }
    public ICommand OpenSearchCommand { get; }

    // Helper to access the current window for dialogs
    private Window? CurrentWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
    
    // Mockable StorageProvider for Unit Tests
    public IStorageProvider? StorageProvider { get; set; }

    // --- Konstruktor  (Dependency Injection) ---
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
        Console.WriteLine("[ViewModel] Constructor started.");
        
        _audioService = audioService;
        _dataService = dataService;
        _fileService = fileService;
        _importService = importService;
        _launcherService = launcherService;
        _storeService = storeService;
        _settingsService = settingsService;
        _metadataService = metadataService;
        _currentSettings = preloadedSettings;
        
        Console.WriteLine("[ViewModel] Services assigned.");

        // Initialization of Commands
        AddCategoryCommand = new RelayCommand<MediaNode?>(AddCategoryAsync);
        AddMediaCommand = new RelayCommand<MediaNode?>(AddMediaAsync);
        DeleteCommand = new RelayCommand<MediaNode?>(DeleteNodeAsync);
        SetCoverCommand = new RelayCommand<MediaItem?>(SetCoverAsync);
        SetLogoCommand = new RelayCommand<MediaItem?>(SetLogoAsync);
        SetWallpaperCommand = new RelayCommand<MediaItem?>(SetWallpaperAsync);
        SetMusicCommand = new RelayCommand<MediaItem?>(SetMusicAsync);
        EditMediaCommand = new RelayCommand<MediaItem?>(EditMediaAsync);
        DeleteMediaCommand = new RelayCommand<MediaItem?>(DeleteMediaAsync);
        PlayCommand = new RelayCommand<MediaItem?>(PlayMedia);
        OpenSettingsCommand = new RelayCommand(OpenSettingsAsync);
        EditNodeCommand = new RelayCommand<MediaNode?>(EditNodeAsync);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ImportRomsCommand = new RelayCommand<MediaNode?>(ImportRomsAsync);
        ImportSteamCommand = new RelayCommand<MediaNode?>(ImportSteamAsync);
        ImportGogCommand = new RelayCommand<MediaNode?>(ImportGogAsync);
        ScrapeMediaCommand = new RelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new RelayCommand<MediaNode?>(ScrapeNodeAsync);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
        
        Console.WriteLine("[ViewModel] Commands initialized.");

        // WICHTIG: Hier darf KEIN LoadData() stehen!
        // LoadData(); <--- DAS MUSS WEG SEIN.
        
        Console.WriteLine("[ViewModel] Constructor finished.");
    }

    // --- Persistence ---
    
    public async Task LoadData()
    {
        Console.WriteLine("[ViewModel] LoadData started...");
        try 
        {
            RootItems = await _dataService.LoadAsync();
            Console.WriteLine($"[ViewModel] Data loaded. {RootItems.Count} root nodes.");
            
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
            
            Console.WriteLine("[ViewModel] LoadData finished.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ViewModel] LoadData Error: {ex}");
            Console.WriteLine($"[ViewModel] LoadData Error: {ex}");
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

    private async void SetMusicAsync(MediaItem? item) 
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.SelectMusic,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac" } } }
        });

        if (result != null && result.Count == 1)
        {
            _audioService.StopMusic();
            var sourceFile = result[0].Path.LocalPath;
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            var relativePath = _fileService.ImportAsset(sourceFile, item, nodePath, MediaFileType.Music);

            if (!string.IsNullOrEmpty(relativePath))
            {
                item.MusicPath = null;
                item.MusicPath = relativePath;
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
                _audioService.PlayMusic(fullPath);
                await SaveData();
            }
        }
    }

    // --- Scraper Logic ---
    private async void ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return; 
    
        var vm = new ScrapeDialogViewModel(item, _currentSettings, _metadataService);
        vm.OnResultSelected += async (result) => 
        {
            // Simple Conflict Resolution
            bool updateDesc = true;
            if (!string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(result.Description) && item.Description != result.Description)
            {
                var preview = item.Description.Length > 30 ? item.Description.Substring(0, 30) + "..." : item.Description;
                // Note: Using hardcoded strings for prompts to keep it simple, could use Resources here too
                updateDesc = await ShowConfirmDialog(owner, $"Update Description? Current: '{preview}'");
            }
            else if (string.IsNullOrWhiteSpace(result.Description)) updateDesc = false;

            if (updateDesc) item.Description = result.Description;

            bool updateDev = true;
            if (!string.IsNullOrWhiteSpace(item.Developer) && !string.IsNullOrWhiteSpace(result.Developer) && !string.Equals(item.Developer, result.Developer, StringComparison.OrdinalIgnoreCase))
            {
                updateDev = await ShowConfirmDialog(owner, $"Update Developer? Old: {item.Developer}, New: {result.Developer}");
            }
            else if (string.IsNullOrWhiteSpace(result.Developer)) updateDev = false;

            if (updateDev) item.Developer = result.Developer;

            if (item.ReleaseDate.HasValue && result.ReleaseDate.HasValue && item.ReleaseDate.Value.Date != result.ReleaseDate.Value.Date)
            {
                if (await ShowConfirmDialog(owner, $"Update Date? Old: {item.ReleaseDate.Value:d}, New: {result.ReleaseDate.Value:d}"))
                    item.ReleaseDate = result.ReleaseDate;
            }
            else if (!item.ReleaseDate.HasValue && result.ReleaseDate.HasValue) item.ReleaseDate = result.ReleaseDate;

            if (result.Rating.HasValue) item.Rating = result.Rating.Value;
            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre)) item.Genre = result.Genre;
        
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            if (!string.IsNullOrEmpty(result.CoverUrl)) await DownloadAndSetAsset(result.CoverUrl, item, nodePath, MediaFileType.Cover, true);
            if (!string.IsNullOrEmpty(result.WallpaperUrl)) await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, MediaFileType.Wallpaper, true);

            await SaveData();
            if (owner.OwnedWindows.FirstOrDefault(w => w.DataContext == vm) is Window dlg) dlg.Close();
        };

        var dialog = new ScrapeDialogView { DataContext = vm };
        await dialog.ShowDialog(owner);
    }

    private async void ScrapeNodeAsync(MediaNode? node)
    {
        if (SelectedNode != null && node != null && node.Id == SelectedNode.Id && node != SelectedNode) node = SelectedNode;
        if (node == null) node = SelectedNode;
        if (node == null || CurrentWindow is not { } owner) return;

        var vm = new BulkScrapeViewModel(node, _currentSettings, _metadataService);
        vm.OnItemScraped = async (item, result) =>
        {
            var parent = FindParentNode(RootItems, item);
            if (parent == null) return;
            var nodePath = PathHelper.GetNodePath(parent, RootItems);
        
            // Bulk Strategy: Only fill missing data (Safe Mode)
            if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(result.Description)) item.Description = result.Description;
            if (string.IsNullOrWhiteSpace(item.Developer) && !string.IsNullOrWhiteSpace(result.Developer)) item.Developer = result.Developer;
            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre)) item.Genre = result.Genre;
            if (!item.ReleaseDate.HasValue && result.ReleaseDate.HasValue) item.ReleaseDate = result.ReleaseDate;
            if (item.Rating == 0 && result.Rating.HasValue) item.Rating = result.Rating.Value;
        
            if (!string.IsNullOrEmpty(result.CoverUrl))
            {
                bool shouldActivate = string.IsNullOrEmpty(item.CoverPath);
                await DownloadAndSetAsset(result.CoverUrl, item, nodePath, MediaFileType.Cover, shouldActivate);
            }
            if (!string.IsNullOrEmpty(result.WallpaperUrl))
            {
                bool shouldActivate = string.IsNullOrEmpty(item.WallpaperPath);
                await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, MediaFileType.Wallpaper, shouldActivate);
            }
        };
    
        var dialog = new BulkScrapeView { DataContext = vm };
        await dialog.ShowDialog(owner);
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    private async Task DownloadAndSetAsset(string url, MediaItem item, List<string> nodePath, MediaFileType type, bool setAsActive)
    {
        try
        {
            var tempFile = Path.GetTempFileName();
            var ext = Path.GetExtension(url).Split('?')[0];
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var tempPathWithExt = Path.ChangeExtension(tempFile, ext);
            File.Move(tempFile, tempPathWithExt);

            bool success = false;
            if (await AsyncImageHelper.SaveCachedImageAsync(url, tempPathWithExt)) success = true;
            else
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("User-Agent", "Retromind/1.0");
                    var data = await client.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(tempPathWithExt, data);
                    success = true;
                }
                catch (Exception ex) { Debug.WriteLine($"Download Failed: {ex.Message}"); }
            }

            if (success)
            {
                var relativePath = _fileService.ImportAsset(tempPathWithExt, item, nodePath, type);
                if (setAsActive && !string.IsNullOrEmpty(relativePath))
                {
                    if (type == MediaFileType.Cover) item.CoverPath = relativePath;
                    if (type == MediaFileType.Wallpaper) item.WallpaperPath = relativePath;
                    if (type == MediaFileType.Logo) item.LogoPath = relativePath;
                }
            }
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
        }
        catch (Exception ex) { Debug.WriteLine($"Critical Download Error: {ex.Message}"); }
    }

    // --- Import Logic ---
    private async void ImportRomsAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;
        if (SelectedNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) targetNode = SelectedNode;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.CtxImportRoms, 
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var sourcePath = folders[0].Path.LocalPath;

        var defaultExt = "iso,bin,cue,rom,smc,sfc,nes,gb,gba,nds,md,n64,z64,v64,exe,sh";
        var extensionsStr = await PromptForName(owner, "File extensions (comma separated):") ?? defaultExt;
        if (string.IsNullOrWhiteSpace(extensionsStr)) return;

        var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var importedItems = await _importService.ImportFromFolderAsync(sourcePath, extensions);

        if (importedItems.Any())
        {
            foreach (var item in importedItems)
            {
                if (!targetNode.Items.Any(i => i.FilePath == item.FilePath))
                {
                    var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                    
                    // Auto-assign existing assets
                    var existingCover = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Cover);
                    if (existingCover != null) item.CoverPath = existingCover;
                    var existingLogo = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Logo);
                    if (existingLogo != null) item.LogoPath = existingLogo;
                    targetNode.Items.Add(item);
                }
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }
        
    private async void ImportSteamAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportSteamGamesAsync();
        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, "No Steam games found.");
            return;
        }

        if (await ShowConfirmDialog(owner, $"Found {items.Count} Steam games. Import to '{targetNode.Name}'?"))
        {
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title)) targetNode.Items.Add(item);
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    private async void ImportGogAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportHeroicGogAsync();
        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, "No Heroic/GOG installations found.");
            return;
        }

        if (await ShowConfirmDialog(owner, $"Found {items.Count} GOG games. Import?"))
        {
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title)) targetNode.Items.Add(item);
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    // --- Media Management ---
    private async void AddMediaAsync(MediaNode? node)
    {
        var targetNode = node ?? SelectedNode;
        if (SelectedNode != null && targetNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.CtxAddMedia, AllowMultiple = true });

        if (result != null && result.Count > 0)
        {
            foreach (var file in result)
            {
                var rawTitle = Path.GetFileNameWithoutExtension(file.Name);
                var title = await PromptForName(owner, $"{Strings.Title} '{file.Name}':") ?? rawTitle;
                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                var newItem = new MediaItem { Title = title, FilePath = file.Path.LocalPath, MediaType = MediaType.Native };
                targetNode.Items.Add(newItem); 
                var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                var existingCover = _fileService.FindExistingAsset(newItem, nodePath, MediaFileType.Cover);
                if (existingCover != null) newItem.CoverPath = existingCover;
            }
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    private async void EditMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        var inherited = FindInheritedEmulator(item);
        var editVm = new EditMediaViewModel(item, _currentSettings, inherited) { StorageProvider = StorageProvider ?? owner.StorageProvider };
        var dialog = new EditMediaView { DataContext = editVm };
        editVm.RequestClose += saved => { dialog.Close(saved); };
        if (await dialog.ShowDialog<bool>(owner)) await SaveData();
    }

    private async void DeleteMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (!await ShowConfirmDialog(owner, Strings.MsgConfirmDelete)) return;

        if (item == (SelectedNodeContent as MediaAreaViewModel)?.SelectedMediaItem) _audioService.StopMusic();
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode != null)
        {
            parentNode.Items.Remove(item);
            await SaveData();
            UpdateContent();
        }
    }

    // --- Tree Management ---
    private async void AddCategoryAsync(MediaNode? parentNode)
    {
        if (CurrentWindow is not { } owner) return;
        var name = await PromptForName(owner, Strings.MsgEnterName);
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (parentNode == null) RootItems.Add(new MediaNode(name, NodeType.Area));
            else
            {
                parentNode.Children.Add(new MediaNode(name, NodeType.Group));
                parentNode.IsExpanded = true; 
            }
            await SaveData();
        }
    }

    private async void DeleteNodeAsync(MediaNode? nodeToDelete)
    {
        if (nodeToDelete == null || CurrentWindow is not { } owner) return;
        if (!await ShowConfirmDialog(owner, Strings.MsgConfirmDelete)) return;

        if (RootItems.Contains(nodeToDelete)) RootItems.Remove(nodeToDelete);
        else RemoveNodeRecursive(RootItems, nodeToDelete);
        await SaveData();
    }

    private async void EditNodeAsync(MediaNode? node)
    {
        if (node == null || CurrentWindow is not { } owner) return;
        var vm = new NodeSettingsViewModel(node, _currentSettings);
        var dialog = new NodeSettingsView { DataContext = vm };
        vm.RequestClose += saved => { dialog.Close(); };
        await dialog.ShowDialog(owner);
        await SaveData();
    }

    // --- Playback ---
    
    private async void PlayMedia(MediaItem? item)
    {
        if (item == null || SelectedNode == null) return;
        _audioService.StopMusic();

        EmulatorConfig? emulator = null;
        if (!string.IsNullOrEmpty(item.EmulatorId))
            emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);

        var trueParent = FindParentNode(RootItems, item) ?? SelectedNode;
        var nodePath = PathHelper.GetNodePath(trueParent, RootItems);

        if (emulator == null)
        {
            var nodeChain = GetNodeChain(trueParent, RootItems);
            nodeChain.Reverse(); 
            foreach (var node in nodeChain)
            {
                if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                {
                    emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
                    if (emulator != null) break;
                }
            }
        }
        await _launcherService.LaunchAsync(item, emulator, nodePath);
        if (SelectedNodeContent is MediaAreaViewModel vm && vm.SelectedMediaItem == item && !string.IsNullOrEmpty(item.MusicPath))
        {
            _audioService.PlayMusic(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath));
        }
        await SaveData();
    }

    private void OpenIntegratedSearch()
    {
        _selectedNode = null;
        OnPropertyChanged(nameof(SelectedNode));
        var searchVm = new SearchAreaViewModel(RootItems) { ItemWidth = ItemWidth };
        searchVm.RequestPlay += item => { PlayMedia(item); };
        searchVm.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(SearchAreaViewModel.SelectedMediaItem))
            {
                 var item = searchVm.SelectedMediaItem;
                 if (item != null && !string.IsNullOrEmpty(item.MusicPath))
                     _audioService.PlayMusic(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath));
                 else _audioService.StopMusic();
            }
        };
        SelectedNodeContent = searchVm;
    }

    // --- Helpers ---

    // Wrappers for Assets
    private async void SetCoverAsync(MediaItem? item) => await SetAssetAsync(item, Strings.SelectCover, MediaFileType.Cover, (i, p) => i.CoverPath = p);
    private async void SetLogoAsync(MediaItem? item) => await SetAssetAsync(item, Strings.SelectLogo, MediaFileType.Logo, (i, p) => i.LogoPath = p);
    private async void SetWallpaperAsync(MediaItem? item) => await SetAssetAsync(item, Strings.SelectWallpaper, MediaFileType.Wallpaper, (i, p) => i.WallpaperPath = p);
    
    private EmulatorConfig? FindInheritedEmulator(MediaItem item)
    {
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode == null) return null;
        var nodeChain = GetNodeChain(parentNode, RootItems);
        nodeChain.Reverse();
        foreach (var node in nodeChain)
            if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                return _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
        return null;
    }

    private void CollectItemsRecursive(MediaNode node, List<MediaItem> targetList)
    {
        targetList.AddRange(node.Items);
        foreach (var child in node.Children) CollectItemsRecursive(child, targetList);
    }

    private void SortAllNodesRecursive(IEnumerable<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            SortMediaItems(node.Items);
            SortAllNodesRecursive(node.Children);
        }
    }

    private void SortMediaItems(ObservableCollection<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.Title).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (items.IndexOf(sorted[i]) != i) items.Move(items.IndexOf(sorted[i]), i);
        }
    }

    private Task RescanAllAssetsAsync()
    {
        return Task.Run(() => { foreach (var rootNode in RootItems) RescanNodeRecursive(rootNode); });
    }

    private void RescanNodeRecursive(MediaNode node)
    {
        var nodePath = PathHelper.GetNodePath(node, RootItems);
        foreach (var item in node.Items)
        {
            if (string.IsNullOrEmpty(item.CoverPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Cover); if (f != null) item.CoverPath = f; }
            if (string.IsNullOrEmpty(item.LogoPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Logo); if (f != null) item.LogoPath = f; }
            if (string.IsNullOrEmpty(item.WallpaperPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Wallpaper); if (f != null) item.WallpaperPath = f; }
            if (string.IsNullOrEmpty(item.MusicPath)) { var f = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Music); if (f != null) item.MusicPath = f; }
        }
        foreach (var child in node.Children) RescanNodeRecursive(child);
    }

    private bool IsNodeInCurrentView(MediaNode modifiedNode)
    {
        if (SelectedNode == null) return false;
        if (modifiedNode == SelectedNode || modifiedNode.Id == SelectedNode.Id) return true;
        return IsChildOf(SelectedNode, modifiedNode);
    }

    private bool IsChildOf(MediaNode parent, MediaNode potentialChild)
    {
        foreach (var child in parent.Children)
        {
            if (child == potentialChild || child.Id == potentialChild.Id) return true;
            if (IsChildOf(child, potentialChild)) return true;
        }
        return false;
    }

    private List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node == target) return new List<MediaNode> { node };
            var chain = GetNodeChain(target, node.Children);
            if (chain.Count > 0) { chain.Insert(0, node); return chain; }
        }
        return new List<MediaNode>();
    }
        
    private async Task SetAssetAsync(MediaItem? item, string title, MediaFileType type, Action<MediaItem, string> updateAction)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;
        var result = await (StorageProvider ?? owner.StorageProvider).OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = new[] { FilePickerFileTypes.ImageAll } });
        if (result != null && result.Count == 1)
        {
            var relPath = _fileService.ImportAsset(result[0].Path.LocalPath, item, PathHelper.GetNodePath(SelectedNode, RootItems), type);
            if (!string.IsNullOrEmpty(relPath)) { updateAction(item, null!); updateAction(item, relPath); await SaveData(); }
        }
    }

    private bool RemoveNodeRecursive(ObservableCollection<MediaNode> nodes, MediaNode nodeToDelete)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Remove(nodeToDelete)) return true;
            if (RemoveNodeRecursive(node.Children, nodeToDelete)) return true;
        }
        return false;
    }

    private MediaNode? FindNodeById(IEnumerable<MediaNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindNodeById(node.Children, id);
            if (found != null) return found;
        }
        return null;
    }

    private bool ExpandPathToNode(IEnumerable<MediaNode> nodes, MediaNode target)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;
            if (ExpandPathToNode(node.Children, target)) { node.IsExpanded = true; return true; }
        }
        return false;
    }
        
    private MediaNode? FindParentNode(IEnumerable<MediaNode> nodes, MediaItem item)
    {
        foreach (var node in nodes)
        {
            if (node.Items.Contains(item)) return node;
            var found = FindParentNode(node.Children, item);
            if (found != null) return found;
        }
        return null;
    }

    private bool IsRandomizeActive(MediaNode targetNode)
    {
        var chain = GetNodeChain(targetNode, RootItems); chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeCovers.HasValue)?.RandomizeCovers ?? false;
    }

    private bool IsRandomizeMusicActive(MediaNode targetNode)
    {
        var chain = GetNodeChain(targetNode, RootItems); chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeMusic.HasValue)?.RandomizeMusic ?? false;
    }

    private async Task<bool> ShowConfirmDialog(Window owner, string message)
    {
        var dialog = new ConfirmView { DataContext = message };
        return await dialog.ShowDialog<bool>(owner);
    }
    
    private async Task<string?> PromptForName(Window owner, string message)
    {
        var dialog = new NamePromptView { DataContext = new NamePromptViewModel(message, message) };
        var result = await dialog.ShowDialog<bool>(owner);
        return result && dialog.DataContext is NamePromptViewModel vm ? vm.InputText : null;
    }
        
    private async void OpenSettingsAsync()
    {
        if (CurrentWindow is not { } owner) return;

        var settingsVm = new SettingsViewModel(_currentSettings);
        var dialog = new SettingsView
        {
            DataContext = settingsVm
        };

        settingsVm.RequestClose += () => { dialog.Close(); };
        
        await dialog.ShowDialog(owner);
        SaveSettingsOnly();
    }
    
    // --- Content Logic (The heart of the UI) ---
    private void UpdateContent()
    {
        _audioService.StopMusic();

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
            // Collect items into a generic List<T>
            var itemList = new List<MediaItem>();
            CollectItemsRecursive(nodeToLoad, itemList); 
        
            // Prepare data for UI update
            var allItems = new ObservableCollection<MediaItem>(itemList);

            // Create the display node (still on background thread)
            var displayNode = new MediaNode(nodeToLoad.Name, nodeToLoad.Type)
            {
                Id = nodeToLoad.Id,
                Items = allItems
            };

            // Background Randomization Logic (Covers/Music)
            bool randomizeMusic = IsRandomizeMusicActive(nodeToLoad);
            if (IsRandomizeActive(nodeToLoad) || randomizeMusic)
            {
                foreach (var item in allItems)
                {
                    // Covers
                    if (IsRandomizeActive(nodeToLoad)) 
                    {
                        var imgs = MediaSearchHelper.FindPotentialImages(item);
                        var rndImg = RandomHelper.PickRandom(imgs);
                        if (rndImg != null && rndImg != item.CoverPath)
                        {
                            // Property changes must happen on UI thread
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.CoverPath = rndImg);
                        }
                    }

                    // Music
                    if (randomizeMusic)
                    {
                        var audios = MediaSearchHelper.FindPotentialAudio(item);
                        var rndAudio = RandomHelper.PickRandom(audios);
                
                        if (rndAudio != null && rndAudio != item.MusicPath)
                        {
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.MusicPath = rndAudio);
                        }
                    }
                }
            }

            // Switch back to UI Thread to update the View
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Guard Clause: If the user selected a different node while we were working, abort.
                if (SelectedNode != nodeToLoad) return;

                var mediaVm = new MediaAreaViewModel(displayNode, ItemWidth);

                // Wire up events
                mediaVm.RequestPlay += item => { PlayMedia(item); };
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
                            _audioService.PlayMusic(fullPath);
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