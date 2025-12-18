using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    // --- Commands ---
    // Using IAsyncRelayCommand allows the UI to bind to IsRunning properties if needed
    public IAsyncRelayCommand<MediaNode?> AddCategoryCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> AddMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> DeleteCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> SetCoverCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> SetLogoCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> SetWallpaperCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> SetMusicCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> EditMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> DeleteMediaCommand { get; private set; } = null!;
    
    // PlayCommand is special, it fires and forgets mostly, but async is better for UI responsiveness
    public IAsyncRelayCommand<MediaItem?> PlayCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand OpenSettingsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> EditNodeCommand { get; private set; } = null!;
    public IRelayCommand ToggleThemeCommand { get; private set; } = null!; // Sync is fine here
    
    public IAsyncRelayCommand<MediaNode?> ImportRomsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportSteamCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportGogCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> ScrapeMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ScrapeNodeCommand { get; private set; } = null!;
    public IRelayCommand OpenSearchCommand { get; private set; } = null!;

    // Command to enter the Big Picture / Themed mode
    public IRelayCommand EnterBigModeCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        // Replaced RelayCommand with AsyncRelayCommand to handle Tasks properly
        // and avoid "async void" pitfalls.
        
        AddCategoryCommand = new AsyncRelayCommand<MediaNode?>(AddCategoryAsync);
        AddMediaCommand = new AsyncRelayCommand<MediaNode?>(AddMediaAsync);
        DeleteCommand = new AsyncRelayCommand<MediaNode?>(DeleteNodeAsync);
        
        SetCoverCommand = new AsyncRelayCommand<MediaItem?>(SetCoverAsync);
        SetLogoCommand = new AsyncRelayCommand<MediaItem?>(SetLogoAsync);
        SetWallpaperCommand = new AsyncRelayCommand<MediaItem?>(SetWallpaperAsync);
        SetMusicCommand = new AsyncRelayCommand<MediaItem?>(SetMusicAsync);
        
        EditMediaCommand = new AsyncRelayCommand<MediaItem?>(EditMediaAsync);
        DeleteMediaCommand = new AsyncRelayCommand<MediaItem?>(DeleteMediaAsync);
        PlayCommand = new AsyncRelayCommand<MediaItem?>(PlayMediaAsync);
        
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        EditNodeCommand = new AsyncRelayCommand<MediaNode?>(EditNodeAsync);
        
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        
        ImportRomsCommand = new AsyncRelayCommand<MediaNode?>(ImportRomsAsync);
        ImportSteamCommand = new AsyncRelayCommand<MediaNode?>(ImportSteamAsync);
        ImportGogCommand = new AsyncRelayCommand<MediaNode?>(ImportGogAsync);
        
        ScrapeMediaCommand = new AsyncRelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new AsyncRelayCommand<MediaNode?>(ScrapeNodeAsync);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
        
        EnterBigModeCommand = new RelayCommand(EnterBigMode);
    }

    // --- Basic Actions ---

    private void EnterBigMode()
    {
        Debug.WriteLine("[CoreApp] EnterBigMode requested.");

        // Stop music immediately to avoid overlap and to keep the UI responsive.
        _audioService.StopMusic();

        var initialThemePath = GetEffectiveThemePath(SelectedNode);
        var initialTheme = ThemeLoader.LoadTheme(initialThemePath);

        var bigVm = new BigModeViewModel(
            RootItems,
            _currentSettings,
            initialTheme,
            _soundEffectService,
            _gamepadService);

        var host = new BigModeHostView
        {
            DataContext = bigVm,
            Focusable = true
        };

        // Show the host first, then inject the theme view (more stable attach/layout behavior).
        FullScreenContent = host;
        host.SetThemeContent(initialTheme.View, initialTheme);
        host.Focus();

        // Prevent out-of-order theme swaps when the user navigates quickly.
        var themeSwapGeneration = 0;
        var currentThemePath = initialThemePath;

        async Task SwapThemeIfNeededAsync()
        {
            var newThemePath = GetEffectiveThemePath(bigVm.ThemeContextNode);
            if (newThemePath == currentThemePath)
                return;

            currentThemePath = newThemePath;
            var myGeneration = ++themeSwapGeneration;

            // Marshal to UI thread (theme swap touches visual tree / VM state).
            await UiThreadHelper.InvokeAsync(async () =>
            {
                if (myGeneration != themeSwapGeneration)
                    return;

                await bigVm.PrepareForThemeSwapAsync();

                if (myGeneration != themeSwapGeneration)
                    return;

                var newTheme = ThemeLoader.LoadTheme(newThemePath);
                bigVm.UpdateTheme(newTheme);

                host.SetThemeContent(newTheme.View, newTheme);
                host.Focus();
            });
        }

        System.ComponentModel.PropertyChangedEventHandler? themeChangedHandler = null;
        themeChangedHandler = (_, args) =>
        {
            if (args.PropertyName != nameof(BigModeViewModel.ThemeContextNode))
                return;

            _ = SwapThemeIfNeededAsync();
        };
        bigVm.PropertyChanged += themeChangedHandler;

        // Close wiring: exit BigMode deterministically + cleanup handlers + sync selection back.
        bigVm.RequestClose += async () =>
        {
            await UiThreadHelper.InvokeAsync(async () =>
            {
                try
                {
                    if (themeChangedHandler != null)
                        bigVm.PropertyChanged -= themeChangedHandler;

                    FullScreenContent = null;

                    bigVm.Dispose();
                    await SyncSelectionFromBigModeAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CoreApp] BigMode close handling failed: {ex}");
                }
            });
        };

        // Cache refresh hook (optional, safe).
        bigVm.InvalidatePreviewCaches(stopCurrentPreview: false);

        if (bigVm.CurrentCategories.Any() && bigVm.SelectedCategory == null)
            bigVm.SelectedCategory = bigVm.CurrentCategories.First();
    }
    
    private void UpdateBigModeStateFromCoreSelection(MediaNode node, MediaItem? selectedItem)
    {
        // Build the root-to-node chain and persist it for BigMode restore.
        var chain = GetNodeChain(node, RootItems);
        _currentSettings.LastBigModeNavigationPath = chain.Select(n => n.Id).ToList();

        if (selectedItem != null)
        {
            _currentSettings.LastBigModeWasItemView = true;
            _currentSettings.LastBigModeSelectedNodeId = selectedItem.Id;
        }
        else
        {
            // If the node is a leaf with items, BigMode should directly open the item list.
            var isLeaf = node.Children.Count == 0;
            var hasItems = node.Items.Count > 0;

            if (isLeaf && hasItems)
            {
                _currentSettings.LastBigModeWasItemView = true;
                _currentSettings.LastBigModeSelectedNodeId = null;
            }
            else
            {
                _currentSettings.LastBigModeWasItemView = false;
                _currentSettings.LastBigModeSelectedNodeId = node.Id;
            }
        }

        SaveSettingsOnly();
    }
    
    public async Task SyncSelectionFromBigModeAsync()
    {
        try
        {
            if (RootItems.Count == 0) return;

            // 1) Ziel-Node bestimmen: letzter Eintrag im BigMode-Navigationspfad
            var targetNodeId = _currentSettings.LastBigModeNavigationPath?.LastOrDefault();
            if (string.IsNullOrWhiteSpace(targetNodeId))
                return;

            var node = FindNodeById(RootItems, targetNodeId);
            if (node == null) return;

            // 2) Tree selektieren + expandieren (UI Thread)
            await UiThreadHelper.InvokeAsync(() =>
            {
                ExpandPathToNode(RootItems, node);
                SelectedNode = node;
            });

            // 3) Warten bis das Grid (SelectedNodeContent) wirklich da ist
            await UpdateContentAsync();

            // 4) Falls BigMode in Item-Ansicht war: Item im Grid selektieren
            if (_currentSettings.LastBigModeWasItemView &&
                !string.IsNullOrWhiteSpace(_currentSettings.LastBigModeSelectedNodeId))
            {
                var itemId = _currentSettings.LastBigModeSelectedNodeId;

                await UiThreadHelper.InvokeAsync(() =>
                {
                    if (SelectedNodeContent is not MediaAreaViewModel mediaVm) return;

                    var item = mediaVm.Node?.Items?.FirstOrDefault(i => i.Id == itemId);
                    if (item != null)
                        mediaVm.SelectedMediaItem = item;
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoreApp] SyncSelectionFromBigModeAsync failed: {ex}");
        }
    }
    
    private async Task AddCategoryAsync(MediaNode? parentNode)
    {
        if (CurrentWindow is not { } owner) return;
        
        try 
        {
            var name = await PromptForName(owner, Strings.Dialog_EnterName_Message);
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (parentNode == null) 
                {
                    RootItems.Add(new MediaNode(name, NodeType.Area));
                }
                else
                {
                    parentNode.Children.Add(new MediaNode(name, NodeType.Group));
                    parentNode.IsExpanded = true; 
                }
                
                MarkLibraryDirty();
                await SaveData();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] AddCategory failed: {ex.Message}");
            // Ideally: Show User Notification
        }
    }

    private async Task DeleteNodeAsync(MediaNode? nodeToDelete)
    {
        if (nodeToDelete == null || CurrentWindow is not { } owner) return;
        
        try
        {
            if (!await ShowConfirmDialog(owner, Strings.Dialog_MsgConfirmDelete)) return;

            if (RootItems.Contains(nodeToDelete)) 
            {
                RootItems.Remove(nodeToDelete);
            }
            else 
            {
                RemoveNodeRecursive(RootItems, nodeToDelete);
            }
            
            MarkLibraryDirty();
            await SaveData();
        }
        catch (Exception ex)
        {
             Debug.WriteLine($"[Error] DeleteNode failed: {ex.Message}");
        }
    }

    private async Task EditNodeAsync(MediaNode? node)
    {
        if (node == null || CurrentWindow is not { } owner) return;
        
        // Calculate Node Path for the FileService
        var nodePath = PathHelper.GetNodePath(node, RootItems);
        
        // Pass _fileService and nodePath to the ViewModel
        var vm = new NodeSettingsViewModel(node, _currentSettings, _fileService, nodePath);
        var dialog = new NodeSettingsView { DataContext = vm };
        
        vm.RequestClose += _ => { dialog.Close(); };
        
        await dialog.ShowDialog(owner);
        
        // NodeSettings kann Eigenschaften/Assets ändern -> als dirty markieren.
        MarkLibraryDirty();
        await SaveData();
    }

    private const string DefaultThemeFolderName = "Default";
    private const string ThemeFileName = "theme.axaml";

    /// <summary>
    /// Resolves the effective theme file path for a given node:
    /// searches upwards (node -> parents) for the first ThemePath assignment,
    /// otherwise returns the default theme.
    /// </summary>
    private string GetEffectiveThemePath(MediaNode? startNode)
    {
        if (startNode != null)
        {
            // Find the chain from root to the node and search bottom-up for an assigned theme.
            var nodeChain = GetNodeChain(startNode, RootItems);
            nodeChain.Reverse();

            foreach (var node in nodeChain)
            {
                if (string.IsNullOrWhiteSpace(node.ThemePath))
                    continue;

                var fullPath = Path.Combine(AppPaths.ThemesRoot, node.ThemePath);

                if (File.Exists(fullPath))
                    return fullPath;

                Debug.WriteLine($"[Theme] Assigned theme file not found: '{fullPath}'.");
            }
        }

        // Fallback: default theme.
        var fallbackThemePath = Path.Combine(AppPaths.ThemesRoot, DefaultThemeFolderName, ThemeFileName);
        Debug.WriteLine($"[Theme] No assigned theme found, using fallback: {fallbackThemePath}");
        return fallbackThemePath;
    }

    private async Task PlayMediaAsync(MediaItem? item)
    {
        if (item == null || SelectedNode == null) return;
        
        // Stop music immediately for responsiveness
        _audioService.StopMusic();

        try 
        {
            EmulatorConfig? emulator = null;
            if (!string.IsNullOrEmpty(item.EmulatorId))
            {
                emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);
            }

            var trueParent = FindParentNode(RootItems, item) ?? SelectedNode;
            var nodePath = PathHelper.GetNodePath(trueParent, RootItems);

            if (emulator == null)
            {
                // Traverse up the tree to find inherited emulator config
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
            
            // C) Native wrapper resolution (global -> node -> item)
            string? wrapperPath = null;
            string? wrapperArgs = null;

            IReadOnlyList<LaunchWrapper>? effectiveWrappers = null;

            if (item.MediaType == MediaType.Native)
            {
                // Item override wins (null=inherits, empty=explicit none)
                if (item.NativeWrappersOverride != null)
                {
                    effectiveWrappers = item.NativeWrappersOverride;
                }
                else
                {
                    // Start with global defaults
                    List<LaunchWrapper>? wrappers = _currentSettings.DefaultNativeWrappers;

                    // Node override (nearest wins)
                    var chain = GetNodeChain(trueParent, RootItems);
                    chain.Reverse();

                    foreach (var node in chain)
                    {
                        if (node.NativeWrappersOverride != null)
                        {
                            wrappers = node.NativeWrappersOverride;
                            break;
                        }
                    }

                    effectiveWrappers = wrappers;
                }
            }

            await _launcherService.LaunchAsync(
                item,
                emulator,
                nodePath,
                nativeWrappers: effectiveWrappers);
            
            // Resume background music after game exit (if applicable)
            if (SelectedNodeContent is MediaAreaViewModel vm && 
                vm.SelectedMediaItem == item)
            {
                // Musikpfad über AssetSystem holen
                var relativeMusicPath = item.GetPrimaryAssetPath(AssetType.Music);
                
                if (!string.IsNullOrEmpty(relativeMusicPath))
                {
                    var musicPath = AppPaths.ResolveDataPath(relativeMusicPath);
                    // Fire and forget music playback
                    _ = _audioService.PlayMusicAsync(musicPath);
                }
            }
            
            // Stats wurden evtl. aktualisiert -> Library ist dirty
            MarkLibraryDirty();
            await SaveData(); // Update stats (playtime, last played)
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] PlayMedia failed: {ex.Message}");
        }
    }

    private async Task DeleteMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        try
        {
            if (!await ShowConfirmDialog(owner, Strings.Dialog_MsgConfirmDelete)) return;

            if (item == (SelectedNodeContent as MediaAreaViewModel)?.SelectedMediaItem) 
            {
                _audioService.StopMusic();
            }
            
            var parentNode = FindParentNode(RootItems, item);
            if (parentNode != null)
            {
                parentNode.Items.Remove(item);
                
                MarkLibraryDirty();
                await SaveData();
                
                UpdateContent();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] DeleteMedia failed: {ex.Message}");
        }
    }

    // --- Dialog Helpers ---

    private async Task<bool> ShowConfirmDialog(Window owner, string message)
    {
        var dialog = new ConfirmView { DataContext = message };
        var result = await dialog.ShowDialog<bool>(owner);
        return result;
    }

    private async Task<string?> PromptForName(Window owner, string message)
    {
        var dialog = new NamePromptView { DataContext = new NamePromptViewModel(message, message) };
        var result = await dialog.ShowDialog<bool>(owner);
        return result && dialog.DataContext is NamePromptViewModel vm ? vm.InputText : null;
    }
    
    private async Task OpenSettingsAsync()
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

    // --- Tree Helpers ---
    // Note: Recursive operations on ObservableCollections can be slow for very large trees.
    // For 30k ROMs, flat list structures in memory might be better, but for now we optimize just the recursion.

    private void CollectItemsRecursive(MediaNode node, List<MediaItem> targetList)
    {
        if (node.Items != null)
        {
            targetList.AddRange(node.Items);
        }
        
        if (node.Children != null)
        {
            foreach (var child in node.Children) 
            {
                CollectItemsRecursive(child, targetList);
            }
        }
    }

    private void SortAllNodesRecursive(IEnumerable<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            // Only sort if needed to avoid overhead on every start
            // (Assuming items are added roughly in order or user doesn't care about strict alphabetical all the time)
            SortMediaItems(node.Items);
            SortAllNodesRecursive(node.Children);
        }
    }

    private void SortMediaItems(ObservableCollection<MediaItem> items)
    {
        // Optimization: Sorting an ObservableCollection in place triggers lots of CollectionChanged events.
        // It's better to sort a List and then rebuild the collection if it's massively out of order,
        // but since we want to keep bindings alive, the Move() approach is acceptable unless items > 1000 per node.
        
        var sorted = items.OrderBy(i => i.Title).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var oldIndex = items.IndexOf(sorted[i]);
            if (oldIndex != i) 
            {
                items.Move(oldIndex, i);
            }
        }
    }

    private bool RemoveNodeRecursive(ObservableCollection<MediaNode> nodes, MediaNode nodeToDelete)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Contains(nodeToDelete))
            {
                node.Children.Remove(nodeToDelete);
                return true;
            }
            
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
            
            if (ExpandPathToNode(node.Children, target)) 
            { 
                node.IsExpanded = true; 
                return true; 
            }
        }
        return false;
    }
    
    private MediaNode? FindParentNode(IEnumerable<MediaNode> nodes, MediaItem item)
    {
        // Breadth-first search might be slightly better here if items are usually near the top,
        // but DFS is standard.
        foreach (var node in nodes)
        {
            if (node.Items.Contains(item)) return node;
            var found = FindParentNode(node.Children, item);
            if (found != null) return found;
        }
        return null;
    }

    private List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node == target) return new List<MediaNode> { node };
            
            var chain = GetNodeChain(target, node.Children);
            if (chain.Count > 0) 
            { 
                chain.Insert(0, node); 
                return chain; 
            }
        }
        return new List<MediaNode>();
    }

    // --- Randomization Helpers ---

    private bool IsRandomizeActive(MediaNode targetNode)
    {
        // Reverse chain to find nearest configuration (bottom-up)
        var chain = GetNodeChain(targetNode, RootItems); 
        chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeCovers.HasValue)?.RandomizeCovers ?? false;
    }

    private bool IsRandomizeMusicActive(MediaNode targetNode)
    {
        var chain = GetNodeChain(targetNode, RootItems); 
        chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeMusic.HasValue)?.RandomizeMusic ?? false;
    }

    // --- Helper for UpdateContent ---
    
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
}