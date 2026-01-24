using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
    public enum NodeDropPosition
    {
        Before,
        Inside,
        After
    }

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
    // Command to attach new manuals/documents directly from the main grid context menu
    public IAsyncRelayCommand<MediaItem?> AddManualToMediaCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        // Replaced RelayCommand with AsyncRelayCommand to handle Tasks properly
        // and avoid "async void" pitfalls
        
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
        OpenManualCommand = new RelayCommand<MediaAsset?>(OpenManual);
        EditNodeCommand = new AsyncRelayCommand<MediaNode?>(EditNodeAsync);
        
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        
        ImportRomsCommand = new AsyncRelayCommand<MediaNode?>(ImportRomsAsync);
        ImportSteamCommand = new AsyncRelayCommand<MediaNode?>(ImportSteamAsync);
        ImportGogCommand = new AsyncRelayCommand<MediaNode?>(ImportGogAsync);
        
        ScrapeMediaCommand = new AsyncRelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new AsyncRelayCommand<MediaNode?>(ScrapeNodeAsync);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
        
        EnterBigModeCommand = new RelayCommand(EnterBigMode);
        
        AddManualToMediaCommand = new AsyncRelayCommand<MediaItem?>(AddManualToMediaAsync);
    }

    // --- Basic Actions ---

    private void EnterBigMode()
    {
        Debug.WriteLine("[CoreApp] EnterBigMode requested.");

        // Stop music immediately to avoid overlap and to keep the UI responsive.
        _audioService.StopMusic();

        // Switch main window to fullscreen while BigMode is active.
        var window = CurrentWindow;
        if (window != null)
        {
            _previousWindowState = window.WindowState;
            if (window.WindowState != WindowState.FullScreen)
            {
                window.WindowState = WindowState.FullScreen;
            }
        }
        
        var initialThemePath = GetEffectiveThemePath(SelectedNode);
        var initialTheme = ThemeLoader.LoadTheme(initialThemePath);

        var bigVm = new BigModeViewModel(
            RootItems,
            _currentSettings,
            initialTheme,
            _soundEffectService,
            _gamepadService);

        // Connect launch requests from BigMode to the central Play logic
        bigVm.RequestPlay += async item => await PlayMediaAsync(item);
        
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

                    // Ensure VLC playback (preview + background video) is fully stopped
                    // before tearing down the visual tree and returning to the core UI.
                    await bigVm.PrepareForThemeSwapAsync();
                    
                    FullScreenContent = null;

                    // Restore the previous window state after leaving BigMode.
                    var window = CurrentWindow;
                    if (window != null &&
                        window.WindowState == WindowState.FullScreen &&
                        _previousWindowState != WindowState.FullScreen)
                    {
                        window.WindowState = _previousWindowState;
                    }
                    
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
        // Reverse chain to find the nearest configuration (bottom-up).
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

            // 1) Determine target node: last entry in the BigMode navigation path
            var targetNodeId = _currentSettings.LastBigModeNavigationPath?.LastOrDefault();
            if (string.IsNullOrWhiteSpace(targetNodeId))
                return;

            var node = FindNodeById(RootItems, targetNodeId);
            if (node == null) return;

            // 2) Tree select + expand (UI Thread)
            await UiThreadHelper.InvokeAsync(() =>
            {
                ExpandPathToNode(RootItems, node);
                SelectedNode = node;
            });

            // 3) Wait until the grid (SelectedNodeContent) is actually there.
            await UpdateContentAsync();

            // 4) If BigMode was in Item View: Select item in the grid
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
        
        // Determine the logical node path (from root to this node),
        // so node-level assets end up in the same folder structure as media items.
        var nodePath = PathHelper.GetNodePath(node, RootItems);
        
        var vm = new NodeSettingsViewModel(
            node,
            RootItems,
            _currentSettings,
            _fileService,
            nodePath);
        
        var dialog = new NodeSettingsView { DataContext = vm };
        
        vm.RequestClose += _ => { dialog.Close(); };
        
        await dialog.ShowDialog(owner);
        
        // NodeSettings can change properties/assets -> mark as dirty
        MarkLibraryDirty();
        await SaveData();
    }

    private const string DefaultThemeFolderName = "Default";
    private const string ThemeFileName = "theme.axaml";

    /// <summary>
    /// Resolves the effective theme file path for a given node:
    /// searches upwards (node -> parents) for the first ThemePath assignment,
    /// otherwise returns the default theme.
    /// 
    /// Special case: the System Host theme ("System/theme.axaml") is only applied
    /// to the node on which it is explicitly set and is not inherited by child
    /// nodes. This allows using System Host for the top-level "systems" view,
    /// while leaf nodes (e.g. platforms) fall back to the Default theme unless
    /// they specify their own BigMode theme.
    /// </summary>
    private string GetEffectiveThemePath(MediaNode? startNode)
    {
        if (startNode != null)
        {
            // Find the chain from root to the node and search bottom-up for an assigned theme.
            var nodeChain = GetNodeChain(startNode, RootItems);
            nodeChain.Reverse();

            // Absolute path of the System Host theme (used as a non-inheritable marker).
            var systemHostThemeFullPath = Path.Combine(AppPaths.ThemesRoot, "System", ThemeFileName);
            systemHostThemeFullPath = Path.GetFullPath(systemHostThemeFullPath);

            foreach (var node in nodeChain)
            {
                if (string.IsNullOrWhiteSpace(node.ThemePath))
                    continue;

                var fullPath = Path.Combine(AppPaths.ThemesRoot, node.ThemePath);
                var fullPathNormalized = Path.GetFullPath(fullPath);

                // Do not inherit the System Host theme from parent nodes.
                // It should only be applied to the node where it is explicitly set.
                if (!ReferenceEquals(node, startNode) &&
                    string.Equals(fullPathNormalized, systemHostThemeFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Skip this assignment and continue searching upwards.
                    Debug.WriteLine($"[Theme] Skipping inherited System Host theme from node '{node.Name}'.");
                    continue;
                }

                if (File.Exists(fullPathNormalized))
                    return fullPathNormalized;

                Debug.WriteLine($"[Theme] Assigned theme file not found: '{fullPathNormalized}'.");
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

        // Global launch guard: ignore additional requests while one is in progress.
        if (IsLaunchInProgress)
            return;

        IsLaunchInProgress = true;
        
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
            IReadOnlyList<LaunchWrapper>? effectiveWrappers = null;

            if (item.MediaType == MediaType.Native)
            {
                List<LaunchWrapper>? wrappers = null;

                // 1) Emulator level (if available)
                if (emulator != null)
                {
                    switch (emulator.NativeWrapperMode)
                    {
                        case EmulatorConfig.WrapperMode.Inherit:
                            // Inherit from global defaults (may be null)
                            wrappers = _currentSettings.DefaultNativeWrappers;
                            break;

                        case EmulatorConfig.WrapperMode.None:
                            // Explicitly no wrappers for this emulator (unless item overrides later)
                            wrappers = new List<LaunchWrapper>();
                            break;

                        case EmulatorConfig.WrapperMode.Override:
                            // Use emulator-level override list (may be empty to mean "none")
                            wrappers = emulator.NativeWrappersOverride != null
                                ? new List<LaunchWrapper>(emulator.NativeWrappersOverride)
                                : new List<LaunchWrapper>();
                            break;
                    }
                }
                else
                {
                    // No emulator: only global defaults as a basis
                    wrappers = _currentSettings.DefaultNativeWrappers;
                }

                // 2) Node level (nearest node in chain; tri-state over null/empty/non-empty)
                var chain = GetNodeChain(trueParent, RootItems);
                chain.Reverse(); // Leaf (trueParent) zuerst

                foreach (var node in chain)
                {
                    if (node.NativeWrappersOverride == null)
                    {
                        // Inherit -> Do nothing, the next level decides
                        continue;
                    }

                    // Empty list => explicitly "no wrappers" at the node level
                    // Non-empty => Override
                    wrappers = node.NativeWrappersOverride;
                    break;
                }

                // 3) Item level (always wins, tri-state over zero/empty/non-empty)
                if (item.NativeWrappersOverride != null)
                {
                    wrappers = item.NativeWrappersOverride;
                }

                effectiveWrappers = wrappers;
            }

            await _launcherService.LaunchAsync(
                item,
                emulator,
                nodePath,
                nativeWrappers: effectiveWrappers,
                usePlaylistForMultiDisc: emulator?.UsePlaylistForMultiDisc == true);

            // Resume background music after game exit (if applicable)
            if (SelectedNodeContent is MediaAreaViewModel vm &&
                vm.SelectedMediaItem == item)
            {
                var relativeMusicPath = item.GetPrimaryAssetPath(AssetType.Music);

                if (!string.IsNullOrEmpty(relativeMusicPath))
                {
                    var musicPath = AppPaths.ResolveDataPath(relativeMusicPath);
                    _ = _audioService.PlayMusicAsync(musicPath);
                }
            }

            MarkLibraryDirty();
            await SaveData();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] PlayMedia failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
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
    
        // Allow the settings dialog to request a one-time portable migration
        settingsVm.RequestPortableMigration += async () =>
        {
            var confirmed = await ShowConfirmDialog(owner, Strings.Settings_ConfirmConvertLaunchPaths);
            if (!confirmed)
                return;

            await ConvertLaunchPathsToPortableAsync();
        };
        
        await dialog.ShowDialog(owner);
        SaveSettingsOnly();
    }

    /// <summary>
    /// Lets the user pick one or more document files (PDF, TXT, etc.) and attaches
    /// them as manual assets to the given media item. Files are copied into the
    /// library using the same rules as other assets (via FileManagementService).
    /// </summary>
    private async Task AddManualToMediaAsync(MediaItem? item)
    {
        if (item == null)
            return;

        // Ensure we have a storage provider. Some host environments may not
        // have set StorageProvider on the view model explicitly yet.
        if (StorageProvider == null && CurrentWindow is { StorageProvider: { } winStorage })
        {
            StorageProvider = winStorage;
        }

        if (StorageProvider == null)
            return;

        // Determine the logical node path for this item so that manuals end up
        // in a folder that matches the tree structure (e.g. Games/PC/Manuals/...).
        var parentNode = FindParentNode(RootItems, item) ?? SelectedNode;
        if (parentNode == null)
            return;

        var nodePath = PathHelper.GetNodePath(parentNode, RootItems);

        // File type filter for typical manual/document formats, including image-based maps
        var fileTypes = new[]
        {
            new FilePickerFileType("Documents")
            {
                Patterns = new[] { "*.pdf", "*.txt", "*.md", "*.rtf", "*.html", "*.htm", "*.jpg", "*.jpeg", "*.png" }
            }
        };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import manual(s)",
            AllowMultiple = true,
            FileTypeFilter = fileTypes
        });

        if (result == null || result.Count == 0)
            return;

        foreach (var file in result)
        {
            try
            {
                var imported = await _fileService.ImportAssetAsync(
                    file.Path.LocalPath,
                    item,
                    nodePath,
                    AssetType.Manual);

                if (imported != null)
                {
                    await UiThreadHelper.InvokeAsync(() =>
                    {
                        item.Assets.Add(imported);
                    });

                    MarkLibraryDirty();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Assets] Failed to import manual for '{item.Title}': {ex.Message}");
            }
        }

        // Persist updated library state (best-effort, debounced by MarkLibraryDirty/SaveData)
        await SaveData();
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

    public async Task<bool> TryMoveNodeAsync(MediaNode sourceNode, MediaNode targetNode, NodeDropPosition dropPosition)
    {
        if (sourceNode == null || targetNode == null)
            return false;

        if (ReferenceEquals(sourceNode, targetNode))
            return false;

        if (IsChildOf(sourceNode, targetNode))
            return false;

        var sourceChain = GetNodeChain(sourceNode, RootItems);
        if (sourceChain.Count == 0)
            return false;

        var targetChain = GetNodeChain(targetNode, RootItems);
        if (targetChain.Count == 0)
            return false;

        var sourceParent = sourceChain.Count > 1 ? sourceChain[^2] : null;
        var targetParent = targetChain.Count > 1 ? targetChain[^2] : null;

        var newParent = dropPosition == NodeDropPosition.Inside ? targetNode : targetParent;
        var parentChanged = !ReferenceEquals(sourceParent, newParent);

        if (parentChanged)
        {
            if (CurrentWindow is not { } owner)
                return false;

            var confirmMessage = string.Format(Strings.Dialog_ConfirmMoveNodeAssetsFormat, sourceNode.Name);
            if (!await ShowConfirmDialog(owner, confirmMessage))
                return false;
        }

        var sourceCollection = sourceParent == null ? RootItems : sourceParent.Children;
        var targetCollection = targetParent == null ? RootItems : targetParent.Children;

        var sourceIndex = sourceCollection.IndexOf(sourceNode);
        var targetIndex = targetCollection.IndexOf(targetNode);

        if (sourceIndex < 0 || targetIndex < 0)
            return false;

        if (parentChanged)
        {
            var oldPathSegments = PathHelper.GetNodePath(sourceNode, RootItems);
            var newPathSegments = BuildNodePathSegments(newParent, sourceNode.Name);

            var oldFolder = ResolveNodeFolder(oldPathSegments);
            var newFolder = ResolveNodeFolder(newPathSegments);

            if (!string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(newFolder))
                    return false;

                try
                {
                    var newParentDir = Path.GetDirectoryName(newFolder);
                    if (!string.IsNullOrWhiteSpace(newParentDir) && !Directory.Exists(newParentDir))
                        Directory.CreateDirectory(newParentDir);

                    if (Directory.Exists(oldFolder))
                        Directory.Move(oldFolder, newFolder);

                    var oldRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, oldFolder);
                    var newRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, newFolder);

                    UpdateAssetPathsRecursive(sourceNode, oldRelativePrefix, newRelativePrefix);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DragDrop] Failed to move node assets: {ex.Message}");
                    return false;
                }
            }
        }

        if (dropPosition == NodeDropPosition.Inside)
        {
            if (ReferenceEquals(sourceParent, targetNode))
                return false;

            if (sourceCollection == targetNode.Children)
                return false;

            sourceCollection.RemoveAt(sourceIndex);
            targetNode.Children.Add(sourceNode);
            targetNode.IsExpanded = true;
        }
        else
        {
            if (ReferenceEquals(sourceCollection, targetCollection))
            {
                if (sourceIndex == targetIndex && dropPosition == NodeDropPosition.Before)
                    return false;

                if (sourceIndex == targetIndex + 1 && dropPosition == NodeDropPosition.After)
                    return false;

                if (sourceIndex < targetIndex)
                    targetIndex -= 1;
            }

            sourceCollection.RemoveAt(sourceIndex);

            var insertIndex = dropPosition == NodeDropPosition.Before ? targetIndex : targetIndex + 1;
            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > targetCollection.Count) insertIndex = targetCollection.Count;

            targetCollection.Insert(insertIndex, sourceNode);
        }

        SelectedNode = sourceNode;
        MarkLibraryDirty();
        if (parentChanged)
            await SaveLibraryIfDirtyAsync(force: true).ConfigureAwait(false);
        return true;
    }

    private List<string> BuildNodePathSegments(MediaNode? parent, string nodeName)
    {
        var segments = new List<string>();
        if (parent != null)
            segments.AddRange(PathHelper.GetNodePath(parent, RootItems));

        segments.Add(nodeName);
        return segments;
    }

    private static string ResolveNodeFolder(List<string> nodePathSegments)
    {
        var rawPath = Path.Combine(AppPaths.LibraryRoot, Path.Combine(nodePathSegments.ToArray()));

        var sanitizedStack = nodePathSegments
            .Select(PathHelper.SanitizePathSegment)
            .ToArray();
        var sanitizedPath = Path.Combine(AppPaths.LibraryRoot, Path.Combine(sanitizedStack));

        if (string.Equals(rawPath, sanitizedPath, StringComparison.Ordinal))
            return rawPath;

        if (Directory.Exists(rawPath))
            return rawPath;

        return sanitizedPath;
    }

    private static void UpdateAssetPathsRecursive(MediaNode node, string oldPrefix, string newPrefix)
    {
        UpdateNodeAssetPaths(node, oldPrefix, newPrefix);

        foreach (var item in node.Items)
        {
            UpdateAssetPaths(item.Assets, oldPrefix, newPrefix);

            if (item.Files != null)
            {
                foreach (var file in item.Files)
                {
                    if (file.Kind != MediaFileKind.LibraryRelative)
                        continue;

                    file.Path = ReplaceRelativePrefix(file.Path, oldPrefix, newPrefix);
                }
            }

            item.ResetActiveAssets();
            item.NotifyAssetPathsChanged();
        }

        foreach (var child in node.Children)
        {
            UpdateAssetPathsRecursive(child, oldPrefix, newPrefix);
        }
    }

    private static void UpdateNodeAssetPaths(MediaNode node, string oldPrefix, string newPrefix)
    {
        var activeByType = new Dictionary<AssetType, string?>();
        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown)
                continue;

            activeByType[type] = node.GetPrimaryAssetPath(type);
        }

        UpdateAssetPaths(node.Assets, oldPrefix, newPrefix);

        foreach (var kvp in activeByType)
        {
            var activePath = kvp.Value;
            if (string.IsNullOrWhiteSpace(activePath))
                continue;

            var updated = ReplaceRelativePrefix(activePath, oldPrefix, newPrefix);
            if (!string.Equals(updated, activePath, StringComparison.OrdinalIgnoreCase))
                node.SetActiveAsset(kvp.Key, updated);
        }
    }

    private static void UpdateAssetPaths(IEnumerable<MediaAsset> assets, string oldPrefix, string newPrefix)
    {
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.RelativePath))
                continue;

            asset.RelativePath = ReplaceRelativePrefix(asset.RelativePath, oldPrefix, newPrefix);
        }
    }

    private static string ReplaceRelativePrefix(string path, string oldPrefix, string newPrefix)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var normalizedPath = NormalizeRelativePath(path);
        var normalizedOld = NormalizeRelativePath(oldPrefix);
        var normalizedNew = NormalizeRelativePath(newPrefix);

        if (string.Equals(normalizedPath, normalizedOld, StringComparison.OrdinalIgnoreCase))
            return normalizedNew;

        var oldWithSlash = normalizedOld.EndsWith("/", StringComparison.Ordinal) ? normalizedOld : normalizedOld + "/";
        if (normalizedPath.StartsWith(oldWithSlash, StringComparison.OrdinalIgnoreCase))
            return normalizedNew + normalizedPath.Substring(oldWithSlash.Length);

        return path;
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim();
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
