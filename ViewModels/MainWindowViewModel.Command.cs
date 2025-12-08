using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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
        // We need a valid selection to show something
        // If SelectedNodeContent is not MediaAreaViewModel (e.g. Settings or Search), we might fallback to Root or show nothing
        if (SelectedNodeContent is not MediaAreaViewModel mediaVM) 
            return;

        // Path to the external theme file. 
        // Ensure this directory exists in your output folder!
        string themePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Themes", "Default.axaml");
            
        // Create the ViewModel that acts as the interface for the theme
        // We pass the items from the currently active view
        var bigVm = new BigModeViewModel(mediaVM.FilteredItems, SelectedNode?.Name ?? "Library");

        // --- CONNECT LOGIC ---
        // Wenn der BigMode "Play" sagt, nutzen wir unseren existierenden PlayMediaAsync Befehl
        bigVm.RequestPlay += (item) => 
        {
            // Fire & Forget Async
            _ = PlayMediaAsync(item);
        };
        
        // --- CONTROLLER WIRING START ---
        // Wir abonnieren die Events des Services und leiten sie an das ViewModel weiter
        
        Action onUp = () => Dispatcher.UIThread.Post(() => bigVm.SelectPreviousCommand.Execute(null));
        Action onDown = () => Dispatcher.UIThread.Post(() => bigVm.SelectNextCommand.Execute(null));
        Action onSelect = () => Dispatcher.UIThread.Post(() => bigVm.PlayCurrentCommand.Execute(null));
        Action onBack = () => Dispatcher.UIThread.Post(() => bigVm.ExitBigModeCommand.Execute(null));

        _gamepadService.OnUp += onUp;
        _gamepadService.OnDown += onDown;
        _gamepadService.OnSelect += onSelect;
        _gamepadService.OnBack += onBack;
        // --- CONTROLLER WIRING END ---
        
        // Cleanup beim Schließen
        bigVm.RequestClose += () => 
        { 
            // Controller Events abhängen
            _gamepadService.OnUp -= onUp;
            _gamepadService.OnDown -= onDown;
            _gamepadService.OnSelect -= onSelect;
            _gamepadService.OnBack -= onBack;

            // UI ausblenden
            FullScreenContent = null;
            
            Task.Run(() => 
            {
                try { bigVm.Dispose(); }
                catch { /* Ignorieren, falls beim Beenden was hakt */ }
            });
        };
            
        // Also wire up the Play command inside the Big Mode to our main Play logic
        // Note: BigModeViewModel has its own PlayCurrentCommand, but it needs to call our main logic
        // We can achieve this by passing an Action or hooking up events. 
        // For now, let's keep it simple in BigModeViewModel, but ideally it calls back here.
        // Let's improve BigModeViewModel to accept a play callback or event later. 
        // For this iteration, we just show the UI.

        // Load the actual UI from the .axaml file
        var view = ThemeLoader.LoadTheme(themePath);
            
        // Bind the DataContext
        view.DataContext = bigVm;
        
        view.Focusable = true; 

        // Show the overlay
        FullScreenContent = view;
        
        // OPTIMIERUNG 2: Echter Delay für XWayland
        // Wir geben dem Fenstersystem kurz Zeit, das Handle bereitzustellen.
        // Dispatcher.Post allein reicht manchmal nicht aus.
        Task.Run(async () => 
        {
            // 250ms warten (nicht spürbar für User, aber ewig für den Computer)
            await Task.Delay(250); 
                
            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                // Nur weitermachen, wenn wir noch im Big Mode sind
                if (FullScreenContent == view)
                {
                    // 1. FOKUS ZURÜCKHOLEN!
                    // Das ist entscheidend, falls das Video-Fenster den Fokus geklaut hat.
                    view.Focus(); 

                    // 2. Video starten (erst Item wählen)
                    if (bigVm.Items.Count > 0)
                    {
                        bigVm.SelectedItem = bigVm.Items[0];
                    }
                }
            });
        });
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
        await SaveData();
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
            
            await _launcherService.LaunchAsync(item, emulator, nodePath);
            
            // Resume background music after game exit (if applicable)
            if (SelectedNodeContent is MediaAreaViewModel vm && 
                vm.SelectedMediaItem == item && 
                !string.IsNullOrEmpty(item.MusicPath))
            {
                var musicPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath);
                // Fire and forget music playback (no await needed for UI flow)
                _ = _audioService.PlayMusicAsync(musicPath);
            }
            
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