using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
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
    private static readonly AssetType[] AssetFolderTypes = Enum.GetValues(typeof(AssetType))
        .Cast<AssetType>()
        .Where(type => type != AssetType.Unknown)
        .ToArray();

    private static readonly Regex AssetFileRegex = new Regex(
        @"^(.+)_(Wallpaper|Cover|Logo|Video|Marquee|Music|Banner|Bezel|ControlPanel|Manual)_(\d+)\..*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    public IAsyncRelayCommand<MediaNode?> ImportEpicCommand { get; private set; } = null!;
    
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
        AddMediaCommand = new AsyncRelayCommand<MediaNode?>(AddMediaAsync, CanOperateOnNode);
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
        
        ImportRomsCommand = new AsyncRelayCommand<MediaNode?>(ImportRomsAsync, CanOperateOnNode);
        ImportSteamCommand = new AsyncRelayCommand<MediaNode?>(ImportSteamAsync, CanOperateOnNode);
        ImportGogCommand = new AsyncRelayCommand<MediaNode?>(ImportGogAsync, CanOperateOnNode);
        ImportEpicCommand = new AsyncRelayCommand<MediaNode?>(ImportEpicAsync, CanOperateOnNode);
        
        ScrapeMediaCommand = new AsyncRelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new AsyncRelayCommand<MediaNode?>(ScrapeNodeAsync, CanOperateOnNode);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
        
        EnterBigModeCommand = new RelayCommand(EnterBigMode);
        
        AddManualToMediaCommand = new AsyncRelayCommand<MediaItem?>(AddManualToMediaAsync);
    }

    // --- Basic Actions ---

    private void EnterBigMode()
    {
        Debug.WriteLine("[CoreApp] EnterBigMode requested.");

        if (!_loadDataTcs.Task.IsCompleted)
        {
            if (Interlocked.Exchange(ref _pendingBigModeEntry, 1) == 1)
                return;

            _ = EnterBigModeAfterLoadAsync();
            return;
        }

        EnterBigModeCore();
    }

    private async Task EnterBigModeAfterLoadAsync()
    {
        try
        {
            await _loadDataTcs.Task.ConfigureAwait(false);
        }
        catch
        {
            // best-effort: still attempt to enter BigMode
        }

        await UiThreadHelper.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _pendingBigModeEntry, 0);
            EnterBigModeCore();
        });
    }

    private void EnterBigModeCore()
    {
        Debug.WriteLine("[CoreApp] EnterBigMode starting.");

        // Stop music immediately to avoid overlap and to keep the UI responsive.
        _audioService.StopMusic();

        // Ensure we have a valid node selection once the library is loaded.
        if (SelectedNode == null && RootItems.Count > 0)
            SelectedNode = RootItems[0];

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

        // Ensure initial theme matches the current BigMode context (e.g. root selection).
        _ = SwapThemeIfNeededAsync();

        // Close wiring: exit BigMode deterministically + cleanup handlers + sync selection back.
        bigVm.RequestClose += async () =>
        {
            await UiThreadHelper.InvokeAsync(async () =>
            {
                try
                {
                    if (themeChangedHandler != null)
                        bigVm.PropertyChanged -= themeChangedHandler;

                    // Capture BigMode selection before the visual tree is torn down.
                    bigVm.SaveState();

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

            // 1) Determine target node: prefer explicit last selected node, then item id fallback, then BigMode path.
            var targetNodeId = _currentSettings.LastSelectedNodeId;

            MediaNode? node = null;
            if (!string.IsNullOrWhiteSpace(targetNodeId))
                node = FindNodeById(RootItems, targetNodeId);

            if (node == null && !string.IsNullOrWhiteSpace(_currentSettings.LastSelectedMediaId))
            {
                if (TryFindNodeByMediaId(RootItems, _currentSettings.LastSelectedMediaId!, out var nodeByItem)
                    && nodeByItem is not null)
                {
                    node = nodeByItem;
                    targetNodeId = nodeByItem.Id;
                }
            }

            if (node == null && _currentSettings.LastBigModeNavigationPath is { Count: > 0 })
            {
                var fallbackId = _currentSettings.LastBigModeNavigationPath.LastOrDefault();
                if (!string.IsNullOrWhiteSpace(fallbackId))
                    node = FindNodeById(RootItems, fallbackId);
            }

            if (node == null)
                return;

            // Capture the intended item selection BEFORE UpdateContent may overwrite settings.
            var desiredItemId = _currentSettings.LastSelectedMediaId;
            if (string.IsNullOrWhiteSpace(desiredItemId))
                desiredItemId = _currentSettings.LastBigModeSelectedNodeId;

            // 2) Tree select + expand (UI Thread)
            await UiThreadHelper.InvokeAsync(() =>
            {
                ExpandPathToNode(RootItems, node);
                SelectedNode = node;

                if (!string.IsNullOrWhiteSpace(desiredItemId))
                    _lastSelectedMediaByNodeId[node.Id] = desiredItemId;
            });

            // 3) Wait until the grid (SelectedNodeContent) is actually there.
            await UpdateContentAsync();

            // 4) Select item in the grid if we have a concrete item id.
            // Do not rely on LastBigModeWasItemView alone; some themes can end up desyncing it.
            if (string.IsNullOrWhiteSpace(desiredItemId))
                return;

            await UiThreadHelper.InvokeAsync(() =>
            {
                if (SelectedNodeContent is not MediaAreaViewModel mediaVm) return;

                var item = mediaVm.Node?.Items?.FirstOrDefault(i => i.Id == desiredItemId);
                if (item != null)
                    mediaVm.SelectedMediaItem = item;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoreApp] SyncSelectionFromBigModeAsync failed: {ex}");
        }
    }

    private static bool TryFindNodeByMediaId(IEnumerable<MediaNode> nodes, string mediaId, out MediaNode? node)
    {
        foreach (var current in nodes)
        {
            if (current.Items.Any(i => i.Id == mediaId))
            {
                node = current;
                return true;
            }

            if (current.Children is { Count: > 0 } && TryFindNodeByMediaId(current.Children, mediaId, out node))
                return true;
        }

        node = null;
        return false;
    }

    private static bool NamesCollide(MediaNode left, MediaNode right)
    {
        if (left == null || right == null)
            return false;

        var leftName = left.Name?.Trim() ?? string.Empty;
        var rightName = right.Name?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(leftName) || string.IsNullOrWhiteSpace(rightName))
            return false;

        if (string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase))
            return true;

        var leftSanitized = PathHelper.SanitizePathSegment(leftName);
        var rightSanitized = PathHelper.SanitizePathSegment(rightName);

        return string.Equals(leftSanitized, rightSanitized, StringComparison.OrdinalIgnoreCase);
    }

    private static MediaNode? FindNameCollision(IEnumerable<MediaNode> siblings, MediaNode sourceNode)
    {
        var sourceName = sourceNode.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceName))
            return null;

        var sourceSanitized = PathHelper.SanitizePathSegment(sourceName);

        foreach (var node in siblings)
        {
            if (ReferenceEquals(node, sourceNode))
                continue;

            var candidateName = node.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidateName))
                continue;

            if (string.Equals(candidateName, sourceName, StringComparison.OrdinalIgnoreCase))
                return node;

            var candidateSanitized = PathHelper.SanitizePathSegment(candidateName);
            if (string.Equals(candidateSanitized, sourceSanitized, StringComparison.OrdinalIgnoreCase))
                return node;
        }

        return null;
    }

    private bool CanOperateOnNode(MediaNode? node)
        => node != null || SelectedNode != null;

    private void NotifyNodeCommandsCanExecuteChanged()
    {
        AddMediaCommand.NotifyCanExecuteChanged();
        ImportRomsCommand.NotifyCanExecuteChanged();
        ImportSteamCommand.NotifyCanExecuteChanged();
        ImportGogCommand.NotifyCanExecuteChanged();
        ImportEpicCommand.NotifyCanExecuteChanged();
        ScrapeNodeCommand.NotifyCanExecuteChanged();
    }
    
    private async Task AddCategoryAsync(MediaNode? parentNode)
    {
        if (CurrentWindow is not { } owner) return;
        
        try 
        {
            var siblings = parentNode == null ? RootItems : parentNode.Children;
            var validator = CreateNodeNameValidator(siblings);
            var name = await PromptForName(owner, Strings.Dialog_EnterName_Message, validator);
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
        var wasSelected = SelectedNode == node;
        
        var vm = new NodeSettingsViewModel(
            node,
            RootItems,
            _currentSettings,
            _fileService,
            nodePath,
            message => ShowConfirmDialog(owner, message));
        
        var dialog = new NodeSettingsView { DataContext = vm };
        
        var saved = false;
        vm.RequestClose += result =>
        {
            saved = result;
            dialog.Close();
        };
        
        await dialog.ShowDialog(owner);
        
        // NodeSettings can change properties/assets -> mark as dirty
        if (saved)
        {
            MarkLibraryDirty();
            await SaveData();
            
            if (wasSelected)
            {
                SelectedNode = node;
                UpdateContent();
            }
            else if (IsNodeInCurrentView(node))
            {
                UpdateContent();
            }
        }
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
        if (item == null) return;

        // Global launch guard: ignore additional requests while one is in progress.
        if (IsLaunchInProgress)
            return;

        IsLaunchInProgress = true;
        
        // Stop music immediately for responsiveness
        _audioService.StopMusic();

        try
        {
            EmulatorConfig? emulator = null;
            if (item.MediaType == MediaType.Emulator && !string.IsNullOrEmpty(item.EmulatorId))
            {
                emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);
            }

            var trueParent = FindParentNode(RootItems, item) ?? SelectedNode;
            if (trueParent == null) return;

            var nodePath = PathHelper.GetNodePath(trueParent, RootItems);

            if (item.MediaType == MediaType.Emulator &&
                emulator == null &&
                string.IsNullOrWhiteSpace(item.LauncherPath))
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

            if (item.MediaType == MediaType.Native || item.MediaType == MediaType.Emulator)
            {
                List<LaunchWrapper>? wrappers = null;

                // 1) Emulator level (if available)
                if (emulator != null)
                {
                    switch (emulator.NativeWrapperMode)
                    {
                        case EmulatorConfig.WrapperMode.Inherit:
                            // Inherit from global defaults (may be null)
                            wrappers = _currentSettings.DefaultNativeWrappers != null
                                ? new List<LaunchWrapper>(_currentSettings.DefaultNativeWrappers)
                                : new List<LaunchWrapper>();
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
                    wrappers = _currentSettings.DefaultNativeWrappers != null
                        ? new List<LaunchWrapper>(_currentSettings.DefaultNativeWrappers)
                        : new List<LaunchWrapper>();
                }

                // 2) Node level (nearest node in chain; tri-state over null/empty/non-empty)
                List<LaunchWrapper>? nodeWrappers = null;
                bool nodeOverrideFound = false;
                var chain = GetNodeChain(trueParent, RootItems);
                chain.Reverse(); // Leaf (trueParent) zuerst

                foreach (var node in chain)
                {
                    if (node.NativeWrappersOverride == null)
                    {
                        // Inherit -> Do nothing, the next level decides
                        continue;
                    }

                    nodeOverrideFound = true;

                    // Empty list => explicitly "no node wrappers" (but keep emulator/global)
                    // Non-empty => Override
                    nodeWrappers = node.NativeWrappersOverride.Count == 0
                        ? new List<LaunchWrapper>()
                        : new List<LaunchWrapper>(node.NativeWrappersOverride);
                    break;
                }

                if (nodeOverrideFound && nodeWrappers != null && nodeWrappers.Count > 0)
                {
                    var baseWrappers = wrappers ?? new List<LaunchWrapper>();
                    var merged = new List<LaunchWrapper>(nodeWrappers.Count + baseWrappers.Count);
                    merged.AddRange(nodeWrappers);
                    merged.AddRange(baseWrappers);
                    wrappers = merged;
                }

                // 3) Item level (always wins, tri-state over zero/empty/non-empty)
                if (item.NativeWrappersOverride != null)
                {
                    wrappers = item.NativeWrappersOverride;
                }

                effectiveWrappers = wrappers;
            }

            if (effectiveWrappers is { Count: > 0 })
            {
                var wrapperText = string.Join(" -> ", effectiveWrappers.Select(w =>
                    string.IsNullOrWhiteSpace(w.Args)
                        ? w.Path
                        : $"{w.Path} {w.Args}"));
                Debug.WriteLine($"[Launch] Wrappers: {wrapperText}");
            }

            var effectiveEnvironment = ResolveEffectiveEnvironmentOverrides(item, emulator, trueParent);

            await _launcherService.LaunchAsync(
                item,
                emulator,
                nodePath,
                nativeWrappers: effectiveWrappers,
                environmentOverrides: effectiveEnvironment,
                usePlaylistForMultiDisc: emulator?.UsePlaylistForMultiDisc == true);

            // Resume background music after game exit (if applicable)
            if (SelectedNodeContent is MediaAreaViewModel vm &&
                vm.SelectedMediaItem == item)
            {
                if (!_currentSettings.EnableSelectionMusicPreview)
                    return;

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

    private Dictionary<string, string>? ResolveEffectiveEnvironmentOverrides(
        MediaItem item,
        EmulatorConfig? emulator,
        MediaNode parentNode)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);

        if (emulator?.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in emulator.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                env[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        // Node-level inheritance (nearest override wins, tri-state via null/empty/non-empty).
        var chain = GetNodeChain(parentNode, RootItems);
        chain.Reverse(); // Leaf (parent) first

        foreach (var node in chain)
        {
            if (node.EnvironmentOverrides == null)
                continue;

            if (node.EnvironmentOverrides.Count > 0)
            {
                foreach (var kv in node.EnvironmentOverrides)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        continue;

                    env[kv.Key.Trim()] = kv.Value ?? string.Empty;
                }
            }

            break;
        }

        if (item.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in item.EnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                env[kv.Key.Trim()] = kv.Value ?? string.Empty;
            }
        }

        return env.Count > 0 ? env : null;
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

    private async Task<string?> PromptForName(
        Window owner,
        string message,
        NamePromptViewModel.NamePromptValidator? validator = null)
    {
        var dialog = new NamePromptView { DataContext = new NamePromptViewModel(message, message, validator) };
        var result = await dialog.ShowDialog<bool>(owner);
        return result && dialog.DataContext is NamePromptViewModel vm ? vm.InputText : null;
    }

    private static NamePromptViewModel.NamePromptValidator CreateNodeNameValidator(
        IEnumerable<MediaNode> siblings)
    {
        var existingRaw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingSanitized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in siblings)
        {
            var name = node.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            existingRaw.Add(name);
            existingSanitized.Add(PathHelper.SanitizePathSegment(name));
        }

        return input =>
        {
            var trimmed = input?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
                return new NamePromptViewModel.NamePromptValidationResult(
                    false,
                    Strings.Dialog_NamePrompt_EmptyName);

            var sanitized = PathHelper.SanitizePathSegment(trimmed);
            var hasCollision = existingRaw.Contains(trimmed) || existingSanitized.Contains(sanitized);

            if (!hasCollision)
                return new NamePromptViewModel.NamePromptValidationResult(true);

            var suggestion = BuildUniqueNameSuggestion(trimmed, existingRaw, existingSanitized);

            return new NamePromptViewModel.NamePromptValidationResult(
                false,
                Strings.Dialog_NamePrompt_DuplicateOrCollision,
                suggestion);
        };
    }

    private static string BuildUniqueNameSuggestion(
        string baseName,
        HashSet<string> existingRaw,
        HashSet<string> existingSanitized)
    {
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (existingRaw.Contains(candidate))
                continue;

            var sanitizedCandidate = PathHelper.SanitizePathSegment(candidate);
            if (existingSanitized.Contains(sanitizedCandidate))
                continue;

            return candidate;
        }

        return string.Empty;
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
        dialog.Closed += (_, _) => settingsVm.Dispose();
    
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
        MediaNode? mergeTarget = null;
        if (dropPosition == NodeDropPosition.Inside)
        {
            if (NamesCollide(targetNode, sourceNode))
            {
                if (CurrentWindow is not { } owner)
                    return false;

                var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeFormat, targetNode.Name);
                if (!await ShowConfirmDialog(owner, mergeMessage))
                    return false;

                mergeTarget = targetNode;
            }
            else
            {
                var destinationCollection = newParent == null ? RootItems : newParent.Children;
                mergeTarget = FindNameCollision(destinationCollection, sourceNode);
                if (mergeTarget != null)
                {
                    if (CurrentWindow is not { } owner)
                        return false;

                    var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeFormat, mergeTarget.Name);
                    if (!await ShowConfirmDialog(owner, mergeMessage))
                        return false;
                }
            }
        }
        else if (parentChanged)
        {
            var destinationCollection = newParent == null ? RootItems : newParent.Children;
            var collision = FindNameCollision(destinationCollection, sourceNode);
            if (collision != null)
            {
                if (CurrentWindow is not { } owner)
                    return false;

                var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeFormat, collision.Name);
                if (!await ShowConfirmDialog(owner, mergeMessage))
                    return false;

                mergeTarget = collision;
            }
        }

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
            var newPathSegments = mergeTarget != null
                ? PathHelper.GetNodePath(mergeTarget, RootItems)
                : BuildNodePathSegments(newParent, sourceNode.Name);

            var oldFolder = ResolveNodeFolder(oldPathSegments);
            var newFolder = ResolveNodeFolder(newPathSegments);

            if (!string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var hasAssets = HasAnyAssetFolders(oldFolder);
                    if (hasAssets && Directory.Exists(newFolder))
                    {
                        var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeAssetsFormat, sourceNode.Name);
                        if (CurrentWindow is not { } owner)
                            return false;
                        if (!await ShowConfirmDialog(owner, mergeMessage))
                            return false;
                    }

                    if (hasAssets)
                    {
                        var renamedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (!MoveAssetFoldersRecursive(sourceNode, oldPathSegments, newPathSegments, renamedFiles))
                            return false;

                        var oldRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, oldFolder);
                        var newRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, newFolder);

                        UpdateAssetPathsRecursive(sourceNode, oldRelativePrefix, newRelativePrefix, renamedFiles);
                    }
                    else
                    {
                        TryDeleteDirectory(oldFolder);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DragDrop] Failed to move node assets: {ex.Message}");
                    return false;
                }
            }
        }

        if (mergeTarget != null && parentChanged)
        {
            sourceCollection.RemoveAt(sourceIndex);
            MergeNodes(sourceNode, mergeTarget);
            SelectedNode = mergeTarget;
        }
        else if (dropPosition == NodeDropPosition.Inside)
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

        if (mergeTarget == null)
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

    private static void UpdateAssetPathsRecursive(
        MediaNode node,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        UpdateNodeAssetPaths(node, oldPrefix, newPrefix, renamedFiles);

        foreach (var item in node.Items)
        {
            UpdateAssetPaths(item.Assets, oldPrefix, newPrefix, renamedFiles);

            item.ResetActiveAssets();
            item.NotifyAssetPathsChanged();
        }

        foreach (var child in node.Children)
        {
            UpdateAssetPathsRecursive(child, oldPrefix, newPrefix, renamedFiles);
        }
    }

    private static void UpdateNodeAssetPaths(
        MediaNode node,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        var activeByType = new Dictionary<AssetType, string?>();
        foreach (AssetType type in Enum.GetValues(typeof(AssetType)))
        {
            if (type == AssetType.Unknown)
                continue;

            activeByType[type] = node.GetPrimaryAssetPath(type);
        }

        UpdateAssetPaths(node.Assets, oldPrefix, newPrefix, renamedFiles);

        foreach (var kvp in activeByType)
        {
            var activePath = kvp.Value;
            if (string.IsNullOrWhiteSpace(activePath))
                continue;

            var updated = TryMapRenamedPath(activePath, renamedFiles, out var mapped)
                ? mapped
                : ReplaceRelativePrefix(activePath, oldPrefix, newPrefix);
            if (!string.Equals(updated, activePath, StringComparison.OrdinalIgnoreCase))
                node.SetActiveAsset(kvp.Key, updated);
        }
    }

    private static void UpdateAssetPaths(
        IEnumerable<MediaAsset> assets,
        string oldPrefix,
        string newPrefix,
        IReadOnlyDictionary<string, string>? renamedFiles)
    {
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.RelativePath))
                continue;

            if (TryMapRenamedPath(asset.RelativePath, renamedFiles, out var mapped))
                asset.RelativePath = mapped;
            else
                asset.RelativePath = ReplaceRelativePrefix(asset.RelativePath, oldPrefix, newPrefix);
        }
    }

    private static bool TryMapRenamedPath(
        string path,
        IReadOnlyDictionary<string, string>? renamedFiles,
        out string mapped)
    {
        mapped = string.Empty;

        if (renamedFiles == null || renamedFiles.Count == 0)
            return false;

        var normalized = NormalizeRelativePath(path);
        if (!renamedFiles.TryGetValue(normalized, out var mappedValue))
            return false;

        mapped = mappedValue;
        return true;
    }

    private static void MergeNodes(MediaNode source, MediaNode target)
    {
        if (ReferenceEquals(source, target))
            return;

        var existingAssets = new HashSet<(AssetType Type, string Path)>(target.Assets.Count);
        foreach (var asset in target.Assets)
        {
            var path = NormalizeRelativePath(asset.RelativePath ?? string.Empty);
            existingAssets.Add((asset.Type, path));
        }

        foreach (var asset in source.Assets.ToList())
        {
            var path = NormalizeRelativePath(asset.RelativePath ?? string.Empty);
            if (existingAssets.Add((asset.Type, path)))
                target.Assets.Add(asset);
        }

        foreach (var item in source.Items.ToList())
            target.Items.Add(item);

        foreach (var child in source.Children.ToList())
            target.Children.Add(child);

        source.Assets.Clear();
        source.Items.Clear();
        source.Children.Clear();
    }

    private static bool HasAnyAssetFolders(string nodeFolder)
    {
        if (string.IsNullOrWhiteSpace(nodeFolder))
            return false;

        if (!Directory.Exists(nodeFolder))
            return false;

        foreach (var type in AssetFolderTypes)
        {
            var folder = Path.Combine(nodeFolder, type.ToString());
            if (Directory.Exists(folder))
                return true;
        }

        return false;
    }

    private static bool MoveAssetFoldersRecursive(
        MediaNode node,
        List<string> oldBaseSegments,
        List<string> newBaseSegments,
        Dictionary<string, string> renamedFiles)
    {
        var relativeSegments = new List<string>();
        return MoveAssetFoldersRecursive(node, oldBaseSegments, newBaseSegments, relativeSegments, renamedFiles);
    }

    private static bool MoveAssetFoldersRecursive(
        MediaNode node,
        IReadOnlyList<string> oldBaseSegments,
        IReadOnlyList<string> newBaseSegments,
        List<string> relativeSegments,
        Dictionary<string, string> renamedFiles)
    {
        var oldSegments = new List<string>(oldBaseSegments.Count + relativeSegments.Count);
        oldSegments.AddRange(oldBaseSegments);
        oldSegments.AddRange(relativeSegments);

        var newSegments = new List<string>(newBaseSegments.Count + relativeSegments.Count);
        newSegments.AddRange(newBaseSegments);
        newSegments.AddRange(relativeSegments);

        if (!MoveAssetFoldersForNode(oldSegments, newSegments, renamedFiles))
            return false;

        foreach (var child in node.Children)
        {
            relativeSegments.Add(child.Name);
            if (!MoveAssetFoldersRecursive(child, oldBaseSegments, newBaseSegments, relativeSegments, renamedFiles))
                return false;
            relativeSegments.RemoveAt(relativeSegments.Count - 1);
        }

        var oldFolder = ResolveNodeFolder(oldSegments);
        TryDeleteDirectory(oldFolder);

        return true;
    }

    private static bool MoveAssetFoldersForNode(
        List<string> oldSegments,
        List<string> newSegments,
        Dictionary<string, string> renamedFiles)
    {
        var oldFolder = ResolveNodeFolder(oldSegments);
        if (!Directory.Exists(oldFolder))
            return true;

        var newFolder = ResolveNodeFolder(newSegments);

        foreach (var type in AssetFolderTypes)
        {
            var oldTypeFolder = Path.Combine(oldFolder, type.ToString());
            if (!Directory.Exists(oldTypeFolder))
                continue;

            var newTypeFolder = Path.Combine(newFolder, type.ToString());

            if (!Directory.Exists(newTypeFolder))
            {
                var newParentDir = Path.GetDirectoryName(newTypeFolder);
                if (!string.IsNullOrWhiteSpace(newParentDir) && !Directory.Exists(newParentDir))
                    Directory.CreateDirectory(newParentDir);

                Directory.Move(oldTypeFolder, newTypeFolder);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(oldTypeFolder))
            {
                var fileName = Path.GetFileName(file);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var targetPath = Path.Combine(newTypeFolder, fileName);
                if (File.Exists(targetPath))
                {
                    targetPath = GetRenumberedAssetPath(newTypeFolder, fileName);
                    var oldRelative = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, file));
                    var newRelative = NormalizeRelativePath(Path.GetRelativePath(AppPaths.DataRoot, targetPath));
                    if (!string.Equals(oldRelative, newRelative, StringComparison.OrdinalIgnoreCase))
                        renamedFiles[oldRelative] = newRelative;
                }

                File.Move(file, targetPath);
            }

            TryDeleteDirectory(oldTypeFolder);
        }

        return true;
    }

    private static string GetRenumberedAssetPath(string targetFolder, string fileName)
    {
        var match = AssetFileRegex.Match(fileName);
        if (match.Success)
        {
            var baseTitle = match.Groups[1].Value;
            var typeToken = match.Groups[2].Value;
            var extension = Path.GetExtension(fileName);
            var prefix = $"{baseTitle}_{typeToken}_";
            var next = GetNextAssetNumber(targetFolder, prefix);
            return GetUniqueNameWithPrefix(targetFolder, prefix, extension, next);
        }

        return GetFallbackRenamedPath(targetFolder, fileName);
    }

    private static int GetNextAssetNumber(string targetFolder, string prefix)
    {
        var max = 0;
        foreach (var file in Directory.EnumerateFiles(targetFolder))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = name.Substring(prefix.Length);
            var dotIndex = remainder.IndexOf('.');
            if (dotIndex <= 0)
                continue;

            var numberPart = remainder.Substring(0, dotIndex);
            if (int.TryParse(numberPart, out var number) && number > max)
                max = number;
        }

        return max + 1;
    }

    private static string GetUniqueNameWithPrefix(string targetFolder, string prefix, string extension, int startNumber)
    {
        var counter = Math.Max(startNumber, 1);
        while (true)
        {
            var name = $"{prefix}{counter:D2}{extension}";
            var candidate = Path.Combine(targetFolder, name);
            if (!File.Exists(candidate))
                return candidate;

            counter++;
        }
    }

    private static string GetFallbackRenamedPath(string targetFolder, string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (true)
        {
            var candidateName = $"{baseName}_Moved_{counter:D2}{extension}";
            var candidatePath = Path.Combine(targetFolder, candidateName);
            if (!File.Exists(candidatePath))
                return candidatePath;

            counter++;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;

            if (Directory.EnumerateFileSystemEntries(path).Any())
                return;

            Directory.Delete(path);
        }
        catch
        {
            // best effort cleanup
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
        {
            var normalizedNewWithSlash = normalizedNew.EndsWith("/", StringComparison.Ordinal)
                ? normalizedNew
                : normalizedNew + "/";
            return normalizedNewWithSlash + normalizedPath.Substring(oldWithSlash.Length);
        }

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
            if (ReferenceEquals(node, target) || node.Id == target.Id)
                return new List<MediaNode> { node };
            
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
