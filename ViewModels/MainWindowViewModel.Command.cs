using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using Retromind.Services.RetroAchievements;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private const int BulkSortedInsertThreshold = 32;

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
    public IAsyncRelayCommand<MediaNode?> AddEmptyMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> AddGogMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> DeleteCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> SetCoverCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> SetLogoCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> SetWallpaperCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> SetMusicCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> EditMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand BulkEditSelectedMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> TestPlayMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ViewLastLaunchLogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> MoveMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> DeleteMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ToggleItemProtectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ToggleNodeProtectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand ToggleParentalLockCommand { get; private set; } = null!;
    public IAsyncRelayCommand ChangeParentalPasswordCommand { get; private set; } = null!;
    
    // PlayCommand is special, it fires and forgets mostly, but async is better for UI responsiveness
    public IAsyncRelayCommand<MediaItem?> PlayCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> CheckGogUpdatesCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ReinstallGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> UpdateGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> UninstallGogCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand OpenSettingsCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenStatisticsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> EditNodeCommand { get; private set; } = null!;
    public IRelayCommand ToggleThemeCommand { get; private set; } = null!; // Sync is fine here
    
    public IAsyncRelayCommand<MediaNode?> ImportRomsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportSteamCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportEpicCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> IdentifyNodeWithRetroAchievementsCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> ScrapeMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ScrapeNodeCommand { get; private set; } = null!;
    public IRelayCommand OpenSearchCommand { get; private set; } = null!;
    public IRelayCommand<string?> SaveSearchTermCommand { get; private set; } = null!;
    public IRelayCommand<string?> RemoveSavedSearchTermCommand { get; private set; } = null!;

    // Command to enter the Big Picture / Themed mode
    public IRelayCommand EnterBigModeCommand { get; private set; } = null!;
    // Command to attach new manuals/documents directly from the main grid context menu
    public IAsyncRelayCommand<MediaItem?> AddManualToMediaCommand { get; private set; } = null!;

    public string GogMediaMenuText => T("Gog.Media.AddMenu", "Add GOG media");
    public string TestPlayMediaMenuText => T("Ctx.Media.TestLaunch", "Test launch (without tracking)");
    public string ViewLastLaunchLogMenuText => T("Launch.ViewLastLog", "View last launch log");
    public string MoveMediaMenuText => T("Ctx.Media.Move", "Move to category...");
    public string BulkEditSelectedMediaText => T("BulkEdit.Selection.Edit", "Edit selected...");
    public string RetroAchievementsSearchAllText => T(
        "RetroAchievements_SearchAll",
        "Search RetroAchievements (All)...");
    public string StatisticsButtonToolTip => T("Statistics.Title", "Library statistics");
    public string GogCheckUpdatesMenuText => T("Gog.Update.CheckNow", "Check for GOG updates");
    public string GogUpdateMenuText => T("Button_Update", "Update");
    public string GogReinstallMenuText => T("Gog.Media.ReinstallMenu", "Reinstall / Switch Version");
    public string GogUninstallMenuText => T("Gog.Uninstall.ContextMenu", "Uninstall");

    private void InitializeCommands()
    {
        // Replaced RelayCommand with AsyncRelayCommand to handle Tasks properly
        // and avoid "async void" pitfalls
        
        AddCategoryCommand = new AsyncRelayCommand<MediaNode?>(AddCategoryAsync, _ => !_libraryLoadFailed);
        AddMediaCommand = new AsyncRelayCommand<MediaNode?>(AddMediaAsync, CanOperateOnNode);
        AddEmptyMediaCommand = new AsyncRelayCommand<MediaNode?>(AddEmptyMediaAsync, CanOperateOnNode);
        AddGogMediaCommand = new AsyncRelayCommand<MediaNode?>(AddGogMediaAsync, CanOperateOnNode);
        DeleteCommand = new AsyncRelayCommand<MediaNode?>(DeleteNodeAsync);
        
        SetCoverCommand = new AsyncRelayCommand<MediaItem?>(SetCoverAsync);
        SetLogoCommand = new AsyncRelayCommand<MediaItem?>(SetLogoAsync);
        SetWallpaperCommand = new AsyncRelayCommand<MediaItem?>(SetWallpaperAsync);
        SetMusicCommand = new AsyncRelayCommand<MediaItem?>(SetMusicAsync);
        
        EditMediaCommand = new AsyncRelayCommand<MediaItem?>(EditMediaAsync);
        BulkEditSelectedMediaCommand = new AsyncRelayCommand(BulkEditSelectedMediaAsync);
        TestPlayMediaCommand = new AsyncRelayCommand<MediaItem?>(TestPlayMediaAsync, CanTestPlayMedia);
        ViewLastLaunchLogCommand = new AsyncRelayCommand<MediaItem?>(
            ViewLastLaunchLogAsync,
            item => item != null && _launchLogService.HasLog(item.Id));
        MoveMediaCommand = new AsyncRelayCommand<MediaItem?>(MoveMediaAsync);
        DeleteMediaCommand = new AsyncRelayCommand<MediaItem?>(DeleteMediaAsync);
        ToggleItemProtectionCommand = new AsyncRelayCommand<MediaItem?>(ToggleItemProtectionAsync);
        ToggleNodeProtectionCommand = new AsyncRelayCommand<MediaNode?>(ToggleNodeProtectionAsync);
        ToggleParentalLockCommand = new AsyncRelayCommand(ToggleParentalLockAsync);
        ChangeParentalPasswordCommand = new AsyncRelayCommand(ChangeParentalPasswordAsync);
        PlayCommand = new AsyncRelayCommand<MediaItem?>(item => PlayMediaAsync(item), CanPlayMedia);
        CheckGogUpdatesCommand = new AsyncRelayCommand<MediaItem?>(CheckGogUpdatesNowAsync, CanCheckGogUpdatesForItem);
        ReinstallGogCommand = new AsyncRelayCommand<MediaItem?>(ReinstallGogMediaAsync, CanReinstallGogMedia);
        UpdateGogCommand = new AsyncRelayCommand<MediaItem?>(UpdateGogMediaAsync, CanUpdateGogMedia);
        UninstallGogCommand = new AsyncRelayCommand<MediaItem?>(UninstallGogMediaAsync, CanUninstallGogMedia);
        
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        OpenStatisticsCommand = new AsyncRelayCommand(OpenStatisticsAsync);
        OpenManualCommand = new RelayCommand<MediaAsset?>(OpenManual);
        EditNodeCommand = new AsyncRelayCommand<MediaNode?>(EditNodeAsync);
        
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        
        ImportRomsCommand = new AsyncRelayCommand<MediaNode?>(ImportRomsAsync, CanOperateOnNode);
        ImportSteamCommand = new AsyncRelayCommand<MediaNode?>(ImportSteamAsync, CanOperateOnNode);
        ImportEpicCommand = new AsyncRelayCommand<MediaNode?>(ImportEpicAsync, CanOperateOnNode);
        IdentifyNodeWithRetroAchievementsCommand = new AsyncRelayCommand<MediaNode?>(
            IdentifyNodeWithRetroAchievementsAsync,
            CanIdentifyNodeWithRetroAchievements);
        
        ScrapeMediaCommand = new AsyncRelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new AsyncRelayCommand<MediaNode?>(ScrapeNodeAsync, CanOperateOnNode);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
        SaveSearchTermCommand = new RelayCommand<string?>(SaveSearchTerm);
        RemoveSavedSearchTermCommand = new RelayCommand<string?>(RemoveSavedSearchTerm);
        
        EnterBigModeCommand = new RelayCommand(EnterBigMode, () => !_libraryLoadFailed);
        
        AddManualToMediaCommand = new AsyncRelayCommand<MediaItem?>(AddManualToMediaAsync);
    }

    private async Task OpenStatisticsAsync()
    {
        if (CurrentWindow is not { } owner)
            return;

        var viewModel = new LibraryStatisticsViewModel(
            RootItems,
            isParentalFilterActive: IsParentalFilterActive);
        var dialog = new LibraryStatisticsView { DataContext = viewModel };
        await dialog.ShowDialog(owner);

        if (viewModel.NavigationTarget is { } target)
            await NavigateToStatisticsItemAsync(target);
        else if (viewModel.FilterRequest is { } filterRequest)
            await ApplyStatisticsFilterAsync(filterRequest);
    }

    private async Task ApplyStatisticsFilterAsync(LibraryStatisticsFilterRequest request)
    {
        // Applying a statistics filter deliberately clears the active item selection.
        // A freshly opened search already has a null selection, so no selection-change
        // notification would otherwise stop music from the previously selected item.
        _audioService.StopMusic();

        var (query, favoritesOnly) = request.Kind switch
        {
            LibraryStatisticsFilterKind.Favorites => (string.Empty, true),
            LibraryStatisticsFilterKind.Completed => ("status:completed", false),
            LibraryStatisticsFilterKind.InProgress => ("status:incomplete played:true", false),
            LibraryStatisticsFilterKind.Abandoned => ("status:abandoned", false),
            LibraryStatisticsFilterKind.NeverStarted => ("status:incomplete played:false", false),
            _ => (string.Empty, false)
        };

        if (request.ScopeNode == null)
        {
            await UiThreadHelper.InvokeAsync(() =>
            {
                if (SelectedNodeContent is not SearchAreaViewModel)
                    OpenIntegratedSearch();

                if (_currentSearchAreaVm == null)
                    return;

                _pendingGlobalSearchSelectionItemId = null;
                _currentSearchAreaVm.SelectedMediaItem = null;
                _currentSearchAreaVm.ApplyScopeSelection(RootItems.Select(root => root.Id).ToArray());
                _currentSearchAreaVm.SearchYear = string.Empty;
                _currentSearchAreaVm.SelectedStatus = null;
                _currentSearchAreaVm.SearchText = query;
                _currentSearchAreaVm.OnlyFavorites = favoritesOnly;
            });
            return;
        }

        await UiThreadHelper.InvokeAsync(() =>
        {
            _searchUiState.SharedSearchText = query;
            _searchUiState.SharedOnlyFavorites = favoritesOnly;
            if (_currentMediaAreaVm != null)
            {
                _currentMediaAreaVm.SearchText = query;
                _currentMediaAreaVm.OnlyFavorites = favoritesOnly;
                _currentMediaAreaVm.SelectedStatus = null;
            }

            RememberNodeDeselection(request.ScopeNode.Id);
            _currentSettings.LastSelectedMediaId = null;
            ExpandPathToNode(RootItems, request.ScopeNode);

            if (ReferenceEquals(SelectedNode, request.ScopeNode))
                UpdateContent();
            else
                SelectedNode = request.ScopeNode;
        });

        // The branch above always starts exactly one refresh: either explicitly
        // for the current node or through the SelectedNode setter.
        await AwaitCurrentContentUpdateAsync();
    }

    private async Task NavigateToStatisticsItemAsync(MediaItem item)
    {
        var targetNode = FindParentNode(RootItems, item);
        if (targetNode == null)
            return;

        await UiThreadHelper.InvokeAsync(() =>
        {
            // A statistics result must remain visible after navigation, regardless
            // of filters that were active in the previous node or global search.
            _searchUiState.SharedSearchText = string.Empty;
            _searchUiState.SharedOnlyFavorites = false;
            if (_currentMediaAreaVm != null)
            {
                _currentMediaAreaVm.SearchText = string.Empty;
                _currentMediaAreaVm.OnlyFavorites = false;
                _currentMediaAreaVm.SelectedStatus = null;
            }

            RememberNodeSelection(targetNode.Id, item.Id);
            _currentSettings.LastSelectedMediaId = item.Id;
            ExpandPathToNode(RootItems, targetNode);

            if (ReferenceEquals(SelectedNode, targetNode))
                UpdateContent();
            else
                SelectedNode = targetNode;
        });

        // The branch above always starts exactly one refresh: either explicitly
        // for the current node or through the SelectedNode setter.
        await AwaitCurrentContentUpdateAsync();
        await UiThreadHelper.InvokeAsync(() =>
        {
            if (SelectedNodeContent is not MediaAreaViewModel mediaViewModel)
                return;

            var target = mediaViewModel.Node.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (target != null)
                mediaViewModel.SelectedMediaItem = target;
        });
    }

    partial void OnIsLaunchInProgressChanged(bool value)
    {
        NotifyPlayAvailabilityChanged();
    }

    public bool CanPlaySelectedMedia => CanPlayMedia(GetCurrentSelectedItem());
    public bool AreSingleItemActionsEnabled => !IsMultiSelectModeActive;

    public string PlaySelectedMediaButtonText
    {
        get
        {
            var item = GetCurrentSelectedItem();
            if (ShouldOfferInstallForItem(item))
                return T("Button_Install", "Install");
            return Strings.Button_Play;
        }
    }
    public string GogUpdateAvailableBadgeTooltip => T("Gog.Update.BadgeTooltip", "GOG update available");

    private void NotifyPlayAvailabilityChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        TestPlayMediaCommand.NotifyCanExecuteChanged();
        CheckGogUpdatesCommand.NotifyCanExecuteChanged();
        ReinstallGogCommand.NotifyCanExecuteChanged();
        UpdateGogCommand.NotifyCanExecuteChanged();
        UninstallGogCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPlaySelectedMedia));
        OnPropertyChanged(nameof(PlaySelectedMediaButtonText));
    }

    private bool CanPlayMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || IsMultiSelectModeActive || item == null)
            return false;

        if (ShouldOfferInstallForItem(item))
            return true;

        if ((item.MediaType == MediaType.Native || item.MediaType == MediaType.Emulator) &&
            !string.IsNullOrWhiteSpace(item.LauncherPath))
        {
            return true;
        }

        var primaryLaunchPath = item.GetPrimaryLaunchPath();
        return !string.IsNullOrWhiteSpace(primaryLaunchPath);
    }

    private bool IsMultiSelectModeActive =>
        _currentMediaAreaVm?.IsMultiSelectMode == true ||
        _currentSearchAreaVm?.IsMultiSelectMode == true;

    private bool CanTestPlayMedia(MediaItem? item)
        => CanPlayMedia(item) && !ShouldOfferInstallForItem(item);

    private bool CanReinstallGogMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || item == null)
            return false;

        // If no playable config exists yet, the primary button already acts as "Install".
        return GogMediaItemStateHelper.IsInstalled(item);
    }

    private bool CanUpdateGogMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || item == null)
            return false;

        return ShouldOfferGogUpdateForItem(item);
    }

    private bool CanUninstallGogMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || item == null)
            return false;

        return GogMediaItemStateHelper.CanUninstall(item);
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
        if (_libraryLoadFailed)
            return;

        Debug.WriteLine("[CoreApp] EnterBigMode starting.");

        // Stop music immediately to avoid overlap and to keep the UI responsive.
        _audioService.StopMusic();

        // Ensure we have a valid node selection once the library is loaded.
        if (SelectedNode == null)
            SelectedNode = FindFirstVisibleNode();

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
        
        // BigMode starts on its virtual root. That level has its own presentation
        // setting and must not borrow the selected top-level node's theme.
        var initialThemePath = GetEffectiveThemePath(null);
        var initialTheme = ThemeLoader.LoadTheme(initialThemePath);

        var bigVm = new BigModeViewModel(
            RootItems,
            _currentSettings,
            initialTheme,
            _soundEffectService,
            _gamepadService,
            _retroAchievementsProgressService,
            _retroAchievementsBadgeService,
            IsParentalFilterActive);
        _activeBigModeViewModel = bigVm;

        var host = new BigModeHostView
        {
            DataContext = bigVm,
            Focusable = true,
            Opacity = 0,
            IsHitTestVisible = false
        };

        // Only a tracked session has a reliable game-end boundary. Restoring
        // focus after an untracked protocol launch could steal it from a game
        // which is still starting through Steam or Heroic.
        bigVm.RequestPlay += item => PlayMediaFromBigModeAsync(item, bigVm, host);

        // Attach the host while hidden so fullscreen sizing, virtualized carousel
        // realization and initial artwork loading can settle without a visible rebuild.
        FullScreenContent = host;
        host.SetThemeContent(initialTheme.View, initialTheme);
        host.RevealAfterInitialLayout();

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
                    if (ReferenceEquals(_activeBigModeViewModel, bigVm))
                        _activeBigModeViewModel = null;

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

    private async Task PlayMediaFromBigModeAsync(
        MediaItem item,
        BigModeViewModel bigVm,
        BigModeHostView host)
    {
        var result = await PlayMediaAsync(item);
        if (result?.WasSessionTracked == true)
            await RestoreBigModeAfterLaunchAsync(bigVm, host);
    }

    private async Task RestoreBigModeAfterLaunchAsync(BigModeViewModel bigVm, BigModeHostView host)
    {
        await UiThreadHelper.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_activeBigModeViewModel, bigVm) ||
                !ReferenceEquals(FullScreenContent, host) ||
                CurrentWindow is not { } window)
            {
                return;
            }

            if (window.WindowState != WindowState.FullScreen)
                window.WindowState = WindowState.FullScreen;

            window.Activate();
            host.Focus();
        }, DispatcherPriority.Input);
    }
    
    private void UpdateBigModeStateFromCoreSelection(MediaNode node, MediaItem? selectedItem)
    {
        // Reverse chain to find the nearest configuration (bottom-up).
        var chain = PathHelper.GetNodeChain(node, RootItems, matchById: true);
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
                if (!string.IsNullOrWhiteSpace(desiredItemId))
                    RememberNodeSelection(node.Id, desiredItemId);

                if (ReferenceEquals(SelectedNode, node))
                    UpdateContent();
                else
                    SelectedNode = node;
            });

            // 3) Wait until the grid (SelectedNodeContent) is actually there.
            await AwaitCurrentContentUpdateAsync();

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
        AddCategoryCommand.NotifyCanExecuteChanged();
        EnterBigModeCommand.NotifyCanExecuteChanged();
        AddMediaCommand.NotifyCanExecuteChanged();
        AddEmptyMediaCommand.NotifyCanExecuteChanged();
        AddGogMediaCommand.NotifyCanExecuteChanged();
        ImportRomsCommand.NotifyCanExecuteChanged();
        ImportSteamCommand.NotifyCanExecuteChanged();
        ImportEpicCommand.NotifyCanExecuteChanged();
        ScrapeNodeCommand.NotifyCanExecuteChanged();
        IdentifyNodeWithRetroAchievementsCommand.NotifyCanExecuteChanged();
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
                    parentNode.Children.Add(new MediaNode(name, NodeType.Group)
                    {
                        AutoProtectNewChildren = parentNode.AutoProtectNewChildren
                    });
                    parentNode.IsExpanded = true; 
                }
                
                RefreshTreeVisibility();
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
            _libraryTracker.MarkDirty();
            await SaveData();
            NotifyNodeCommandsCanExecuteChanged();
            
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
    /// Resolves the effective theme file path for a navigation context. A null
    /// context represents the virtual library root and uses its dedicated setting.
    /// Node contexts search upwards (node -> parents) for the first ThemePath
    /// assignment, otherwise returning the default theme.
    /// 
    /// Special case: the System Host theme ("System/theme.axaml") is only applied
    /// to the node on which it is explicitly set and is not inherited by child
    /// nodes. This allows using System Host for the top-level "systems" view,
    /// while leaf nodes (e.g. platforms) fall back to the Default theme unless
    /// they specify their own BigMode theme.
    /// </summary>
    private string GetEffectiveThemePath(MediaNode? startNode)
    {
        if (startNode == null)
        {
            var rootThemePath = _currentSettings.RootBigModeThemePath;
            if (!string.IsNullOrWhiteSpace(rootThemePath))
            {
                var rootThemeFullPath = Path.GetFullPath(Path.Combine(AppPaths.ThemesRoot, rootThemePath));
                if (File.Exists(rootThemeFullPath))
                    return rootThemeFullPath;

                Debug.WriteLine($"[Theme] Assigned root theme file not found: '{rootThemeFullPath}'.");
            }
        }

        if (startNode != null)
        {
            // Find the chain from root to the node and search bottom-up for an assigned theme.
            var nodeChain = PathHelper.GetNodeChain(startNode, RootItems, matchById: true);
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

    private Task TestPlayMediaAsync(MediaItem? item)
        => PlayMediaAsync(item, recordStatistics: false);

    private async Task<LaunchResult?> PlayMediaAsync(MediaItem? item, bool recordStatistics = true)
    {
        if (item == null)
            return null;

        if (!CanPlayMedia(item))
            return null;

        // Global launch guard: ignore additional requests while one is in progress.
        if (IsLaunchInProgress)
            return null;

        IsLaunchInProgress = true;
        
        // Stop music immediately for responsiveness
        _audioService.StopMusic();

        try
        {
            if (ShouldOfferInstallForItem(item))
            {
                await InstallGogItemAsync(item);
                return null;
            }

            EmulatorConfig? emulator = null;
            if (item.MediaType == MediaType.Emulator && !string.IsNullOrEmpty(item.EmulatorId))
            {
                emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);
            }

            var trueParent = FindParentNode(RootItems, item) ?? SelectedNode;
            if (trueParent == null) return null;

            var nodePath = PathHelper.GetNodePath(trueParent, RootItems);

            if (item.MediaType == MediaType.Emulator &&
                emulator == null &&
                string.IsNullOrWhiteSpace(item.LauncherPath))
            {
                // Traverse up the tree to find inherited emulator config
                var nodeChain = PathHelper.GetNodeChain(trueParent, RootItems, matchById: true);
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

            var runnerValidationError = GetRunnerValidationError(item, emulator);
            if (runnerValidationError != null)
            {
                Debug.WriteLine($"[Launch] Runner validation failed for '{item.Title}': {runnerValidationError}");
                await _launchLogService.TryWritePreflightFailureAsync(
                    item,
                    emulator,
                    _currentSettings,
                    recordStatistics,
                    runnerValidationError);
                ViewLastLaunchLogCommand.NotifyCanExecuteChanged();
                if (CurrentWindow is { } owner)
                {
                    var format = T(
                        "Launch.FailedFormat",
                        "\"{0}\" could not be started.\n\n{1}\n\nPlease check the launch file, emulator/runner, wrapper, and permissions.");
                    await ShowInfoDialog(owner, string.Format(format, item.Title, runnerValidationError));
                }

                return null;
            }

            // Native wrapper resolution (emulator -> node -> item)
            IReadOnlyList<LaunchWrapper>? effectiveWrappers = null;

            if (item.MediaType == MediaType.Native || item.MediaType == MediaType.Emulator)
            {
                effectiveWrappers = LaunchInheritanceResolver.ResolveNativeWrappers(
                    emulator,
                    trueParent,
                    RootItems,
                    item.NativeWrappersOverride,
                    matchNodesById: true);
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

            var launchResult = await _launcherService.LaunchAsync(
                item,
                emulator,
                nodePath,
                nativeWrappers: effectiveWrappers,
                environmentOverrides: effectiveEnvironment,
                usePlaylistForMultiDisc: emulator?.UsePlaylistForMultiDisc == true,
                recordStatistics: recordStatistics);
            ViewLastLaunchLogCommand.NotifyCanExecuteChanged();

            if (!launchResult.IsStarted)
            {
                Debug.WriteLine($"[Launch] Failed to start '{item.Title}': {launchResult.ErrorMessage}");
                if (CurrentWindow is { } owner)
                {
                    var format = T(
                        "Launch.FailedFormat",
                        "\"{0}\" could not be started.\n\n{1}\n\nPlease check the launch file, emulator/runner, wrapper, and permissions.");
                    await ShowInfoDialog(owner, string.Format(format, item.Title, launchResult.ErrorMessage));
                }
            }
            else if (launchResult.MissingWatchedProcessName is { } missingProcessName)
            {
                Debug.WriteLine(
                    $"[Launch] Expected process '{missingProcessName}' did not appear after starting '{item.Title}'.");
                if (CurrentWindow is { } owner)
                {
                    var format = T(
                        "Launch.WatchedProcessNotFoundFormat",
                        "The launcher for \"{0}\" was started, but the expected process \"{1}\" did not appear.\n\nThe game may not have started, or the configured process name may be incorrect.");
                    var message = AppendLaunchConsoleOutput(
                        string.Format(format, item.Title, missingProcessName),
                        launchResult.ConsoleOutput);
                    await ShowInfoDialog(owner, message);
                }
            }
            else if (launchResult.Outcome == LaunchOutcome.ExitedEarly && launchResult.ExitCode is { } exitCode)
            {
                Debug.WriteLine($"[Launch] '{item.Title}' exited early with code {exitCode}.");
                if (CurrentWindow is { } owner)
                {
                    var format = T(
                        "Launch.ExitedEarlyFormat",
                        "\"{0}\" ended shortly after launch with exit code {1}.\n\nThe program may not have started correctly.");
                    var message = AppendLaunchConsoleOutput(
                        string.Format(format, item.Title, exitCode),
                        launchResult.ConsoleOutput);
                    await ShowInfoDialog(owner, message);
                }
            }

            // Resume selection music after game exit in either desktop content view.
            if (ReferenceEquals(GetCurrentSelectedItem(), item))
            {
                var contextNode = GetSelectionMusicContextNode(item);
                await PlaySelectionMusicAsync(item, contextNode);
            }

            if (recordStatistics)
            {
                _libraryTracker.MarkDirty();
                await SaveData();
            }

            if (recordStatistics &&
                launchResult.Outcome == LaunchOutcome.Started &&
                launchResult.WasSessionTracked &&
                item.RetroAchievementsGame?.GameId > 0)
            {
                var activeBigMode = _activeBigModeViewModel;
                if (activeBigMode != null && ReferenceEquals(activeBigMode.SelectedItem, item))
                {
                    activeBigMode.RefreshRetroAchievementsAfterTrackedSession(item);
                }
                else if (ReferenceEquals(GetCurrentSelectedItem(), item))
                {
                    _ = RetroAchievementsProgress.SelectItemAsync(item, forceRefresh: true);
                }
            }

            return launchResult;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] PlayMedia failed: {ex.Message}");
            return null;
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private static string AppendLaunchConsoleOutput(string message, string? consoleOutput)
    {
        if (string.IsNullOrWhiteSpace(consoleOutput))
            return message;

        var header = T("Launch.ConsoleOutputHeader", "Console output:");
        return $"{message}{Environment.NewLine}{Environment.NewLine}{header}{Environment.NewLine}{consoleOutput}";
    }

    private async Task ViewLastLaunchLogAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner)
            return;

        await ShowLastLaunchLogAsync(item, owner);
    }

    private async Task ShowLastLaunchLogAsync(MediaItem item, Window owner)
    {
        var logText = await _launchLogService.TryReadAsync(item.Id);
        if (string.IsNullOrWhiteSpace(logText))
        {
            ViewLastLaunchLogCommand.NotifyCanExecuteChanged();
            await ShowInfoDialog(
                owner,
                T("Launch.LastLogUnavailable", "No launch log is available for this item."),
                showCopyButton: false);
            return;
        }

        var titleFormat = T("Launch.LastLogTitleFormat", "Last launch log - {0}");
        var logViewModel = new ProcessLogViewModel(string.Format(titleFormat, item.Title))
        {
            LogText = logText,
            IsRunning = false
        };
        logViewModel.MarkFinished();

        var logView = new ProcessLogView { DataContext = logViewModel };
        logView.Show(owner);
    }

    private async Task ReinstallGogMediaAsync(MediaItem? item)
    {
        if (!CanReinstallGogMedia(item) || item == null)
            return;

        if (IsLaunchInProgress)
            return;

        IsLaunchInProgress = true;
        try
        {
            await InstallGogItemAsync(item, operation: GogInstallOperation.Reinstall);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] ReinstallGogMedia failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private async Task UpdateGogMediaAsync(MediaItem? item)
    {
        if (!CanUpdateGogMedia(item) || item == null)
            return;

        if (IsLaunchInProgress)
            return;

        IsLaunchInProgress = true;
        try
        {
            await InstallGogItemAsync(item, operation: GogInstallOperation.Update);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] UpdateGogMedia failed: {ex.Message}");
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private async Task<bool> RunGogInstallFromEditorAsync(
        MediaItem item,
        Window owner,
        bool requireAvailableUpdate)
    {
        if (IsLaunchInProgress ||
            GogMediaItemStateHelper.TryGetGameId(item) == null ||
            (requireAvailableUpdate && !GogMediaItemStateHelper.HasUpdateAvailable(item)))
        {
            return false;
        }

        IsLaunchInProgress = true;
        try
        {
            var operation = requireAvailableUpdate
                ? GogInstallOperation.Update
                : GogMediaItemStateHelper.IsInstalled(item)
                    ? GogInstallOperation.Reinstall
                    : GogInstallOperation.Install;
            return await InstallGogItemAsync(
                item,
                owner,
                operation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] GOG install from media editor failed: {ex.Message}");
            return false;
        }
        finally
        {
            IsLaunchInProgress = false;
        }
    }

    private async Task UninstallGogMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner)
            return;

        _ = await RunGogUninstallAsync(item, owner);
    }

    private async Task<bool> RunGogUninstallAsync(MediaItem item, Window owner)
    {
        if (!CanUninstallGogMedia(item))
            return false;

        var confirmMessage = string.Format(
            Strings.Gog_Uninstall_ConfirmMessage,
            item.Title);

        var confirmed = await ShowConfirmDialog(owner, confirmMessage);
        if (!confirmed)
            return false;

        IsLaunchInProgress = true;
        try
        {
            await _gogInstallService.UninstallGogGameAsync(item);

            // after successfull deinstall: reload the library
            _libraryTracker.MarkDirty();
            await SaveData();
            NotifyPlayAvailabilityChanged();

            await ShowInfoDialog(owner, Strings.Gog_Uninstall_Success);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] UninstallGogMedia failed: {ex.Message}");
            await ShowInfoDialog(owner,
                string.Format(Strings.Gog_Uninstall_FailedWithMessage, ex.Message));
            return false;
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
        var env = LaunchInheritanceResolver.ResolveEnvironmentOverrides(
            _currentSettings,
            emulator,
            parentNode,
            RootItems,
            item.EnvironmentOverrides,
            item.RunnerVersionId,
            matchNodesById: true);

        return env.Count > 0 ? env : null;
    }

    private string? GetRunnerValidationError(MediaItem item, EmulatorConfig? emulator)
    {
        var runnerId = !string.IsNullOrWhiteSpace(item.RunnerVersionId)
            ? item.RunnerVersionId
            : emulator?.DefaultRunnerVersionId;
        if (string.IsNullOrWhiteSpace(runnerId))
            return null;

        var isAvailable = RunnerVersionEnvironmentHelper.TryFindAvailableRunnerVersion(
            _currentSettings,
            runnerId,
            out var runner,
            out var resolvedPath);
        if (runner == null)
        {
            return T(
                "Launch.RunnerAssignmentMissing",
                "The runner configured for this game or emulator no longer exists in Settings. Select another runner in Settings -> Runner.");
        }

        if (isAvailable)
            return null;

        var format = T(
            "Launch.RunnerUnavailableFormat",
            "The configured runner '{0}' was not found or is incomplete at:\n{1}\n\nSelect or install it again in Settings -> Runner.");
        return string.Format(format, runner.Name, resolvedPath);
    }

    private async Task DeleteMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        
        try
        {
            if (!await ShowConfirmDialog(owner, Strings.Dialog_MsgConfirmDelete)) return;

            if (ReferenceEquals(item, GetCurrentSelectedItem()))
            {
                if (_currentSearchAreaVm is { } searchVm)
                    searchVm.SelectedMediaItem = null;
                else
                    _audioService.StopMusic();
            }
            
            var parentNode = FindParentNode(RootItems, item);
            if (parentNode != null)
            {
                parentNode.Items.Remove(item);
                await SaveData();
                _launchLogService.TryDelete(item.Id);
                ViewLastLaunchLogCommand.NotifyCanExecuteChanged();
                
                RefreshContentAfterMediaCollectionChange();
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

    private async Task ShowInfoDialog(Window owner, string message, bool showCopyButton = true)
    {
        await UiThreadHelper.InvokeAsync(async () =>
        {
            var dialog = new InfoView
            {
                DataContext = message,
                ShowCopyButton = showCopyButton
            };
            await dialog.ShowDialog<bool>(owner);
        });
    }

    private async Task<string?> PromptForName(
        Window owner,
        string message,
        NamePromptViewModel.NamePromptValidator? validator = null,
        string? initialText = null)
    {
        var viewModel = new NamePromptViewModel(message, message, validator);
        if (initialText != null)
            viewModel.InputText = initialText;

        var dialog = new NamePromptView { DataContext = viewModel };
        var result = await dialog.ShowDialog<bool>(owner);
        return result ? viewModel.InputText : null;
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

            var suggestion = PathHelper.BuildUniqueNodeNameSuggestion(trimmed, existingRaw, existingSanitized);

            return new NamePromptViewModel.NamePromptValidationResult(
                false,
                Strings.Dialog_NamePrompt_DuplicateOrCollision,
                suggestion);
        };
    }

    private async Task OpenSettingsAsync()
    {
        if (CurrentWindow is not { } owner) return;

        var portableHomeWasEnabled = _currentSettings.UsePortableHomeInAppImage;
        var settingsVm = new SettingsViewModel(
            _currentSettings,
            _settingsService,
            _retroAchievementsAccountService,
            RootItems);
        await settingsVm.InitializeRetroAchievementsAsync();
        settingsVm.RequestRunnerVersionRemovalConfirmation += runner =>
        {
            var message = runner.SourceType == RunnerVersionSourceType.ManagedDownload
                ? string.Format(
                    Strings.ResourceManager.GetString("Settings_RunnerVersionRemoveManagedConfirmFormat", Strings.Culture)
                    ?? "Remove {0} and permanently delete its downloaded files?",
                    runner.Name)
                : string.Format(
                    Strings.ResourceManager.GetString("Settings_RunnerVersionRemoveExternalConfirmFormat", Strings.Culture)
                    ?? "Remove {0} from Retromind? Its external files will be kept.",
                    runner.Name);

            return ShowConfirmDialog(owner, message);
        };
        settingsVm.RequestRunnerVersionReplacementConfirmation += (source, replacement) =>
        {
            var format = Strings.ResourceManager.GetString(
                             "Settings_RunnerVersionReplaceConfirmFormat",
                             Strings.Culture)
                         ?? "Replace all game assignments and emulator defaults from {0} with {1}?\n\n{0} remains installed and registered.";

            return ShowConfirmDialog(owner, string.Format(format, source.Name, replacement.Name));
        };
        settingsVm.RequestRunnerVersionAssignmentPersistence += async () =>
        {
            if (settingsVm.LibraryModified)
                _libraryTracker.MarkDirty();

            return await SaveData();
        };
        var dialog = new SettingsView
        {
            DataContext = settingsVm
        };

        settingsVm.RequestClose += () => { dialog.Close(); };
        settingsVm.RequestSortPreviewRefresh += UpdateContent;
        settingsVm.RequestOpenMetadataBackups += async () =>
        {
            var restored = await OpenMetadataBackupsAsync(dialog);
            if (!restored)
                return;

            dialog.Close();
            UiThreadHelper.Post(owner.Close, DispatcherPriority.Background);
        };
        dialog.Closed += (_, _) => settingsVm.Dispose();
    
        // Allow the settings dialog to request a one-time portable migration
        settingsVm.RequestPortableMigration += async () =>
        {
            var confirmed = await ShowConfirmDialog(owner, Strings.Settings_ConfirmConvertLaunchPaths);
            if (!confirmed)
                return;

            await ConvertLaunchPathsToPortableAsync();
        };

        settingsVm.RequestParentalPasswordChange += async () =>
        {
            await ChangeParentalPasswordAsync(owner);
        };

        await dialog.ShowDialog(owner);

        // A metadata restore deliberately replaced the persisted files and requested
        // application shutdown. Never let the settings-dialog continuation save the
        // preceding in-memory settings over that restored state.
        if (_closeWithoutPersistenceAfterRestore)
            return;

        if (settingsVm.LibraryModified)
            _libraryTracker.MarkDirty();

        if (settingsVm.IsSaved)
        {
            OnPropertyChanged(nameof(ShowStoreBadges));
            _metadataService.ClearProviderCache();
            NotifyNodeCommandsCanExecuteChanged();
            var settingsSaved = await SaveData();
            if (settingsSaved && !_settingsService.HasLoadFailure)
            {
                var portableHomeIsEnabled = _currentSettings.UsePortableHomeInAppImage;
                await RetroAchievementsGameCatalogService.MigrateCacheLocationAsync(
                    portableHomeWasEnabled,
                    portableHomeIsEnabled);
                var cacheLocationChanged =
                    _retroAchievementsCachePathProvider.UsePortableHome(portableHomeIsEnabled);
                await RetroAchievementsProgress.SelectItemAsync(
                    GetCurrentSelectedItem(),
                    forceRefresh: cacheLocationChanged);
            }
        }
        else if (settingsVm.LibraryModified)
        {
            // Confirmed runner removal can alter item-level runner overrides even
            // when the remaining dialog edits are discarded.
            await SaveData();
        }
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
                Patterns = new[] { "*.pdf", "*.cbz", "*.txt", "*.md", "*.rtf", "*.html", "*.htm", "*.jpg", "*.jpeg", "*.png" }
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

                    _libraryTracker.MarkDirty();
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

    private static int FindSortedInsertIndex(ObservableCollection<MediaItem> items, MediaItem candidate, int minIndex = 0)
    {
        var low = Math.Clamp(minIndex, 0, items.Count);
        var high = items.Count;

        // Upper-bound insertion by display order:
        // SortTitle (fallback: Title). New equal items are placed after existing ones.
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            var compare = MediaSortHelper.CompareForDisplayOrder(items[mid], candidate);

            if (compare <= 0)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    private static bool IsSortedByDisplayOrder(IReadOnlyList<MediaItem> items)
    {
        if (items.Count <= 1)
            return true;

        for (var i = 1; i < items.Count; i++)
        {
            if (MediaSortHelper.CompareForDisplayOrder(items[i - 1], items[i]) > 0)
                return false;
        }

        return true;
    }

    private static void InsertMediaItemSorted(ObservableCollection<MediaItem> items, MediaItem item)
    {
        if (items.Count == 0)
        {
            items.Add(item);
            return;
        }

        var insertIndex = FindSortedInsertIndex(items, item);
        items.Insert(insertIndex, item);
    }

    private void InsertMediaItemsOptimized(ObservableCollection<MediaItem> items, IReadOnlyList<MediaItem> newItems)
    {
        if (newItems.Count == 0)
            return;

        // Safety net: if older data/path left this collection unsorted, repair once before
        // binary insert to keep order correctness.
        if (!IsSortedByDisplayOrder(items))
            SortMediaItems(items);

        if (newItems.Count < BulkSortedInsertThreshold)
        {
            foreach (var item in newItems)
                InsertMediaItemSorted(items, item);
            return;
        }

        // Bulk path:
        // sort incoming items first and then keep a monotonic lower bound for binary search.
        // This reduces comparisons/scans for large imports while preserving stable insertion.
        var orderedNewItems = newItems
            .OrderBy(item => item, MediaSortHelper.DisplayOrderComparer)
            .ToList();

        var lowerBound = 0;
        foreach (var item in orderedNewItems)
        {
            var insertIndex = FindSortedInsertIndex(items, item, lowerBound);
            items.Insert(insertIndex, item);
            lowerBound = insertIndex + 1;
        }
    }

    private void SortMediaItems(ObservableCollection<MediaItem> items)
    {
        // Optimization: Sorting an ObservableCollection in place triggers lots of CollectionChanged events.
        // It's better to sort a List and then rebuild the collection if it's massively out of order,
        // but since we want to keep bindings alive, the Move() approach is acceptable unless items > 1000 per node.
        
        var sorted = items.OrderBy(i => i, MediaSortHelper.DisplayOrderComparer).ToList();
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

        var sourceChain = PathHelper.GetNodeChain(sourceNode, RootItems, matchById: true);
        if (sourceChain.Count == 0)
            return false;

        var targetChain = PathHelper.GetNodeChain(targetNode, RootItems, matchById: true);
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

            var oldFolder = PathHelper.ResolveNodeFolder(oldPathSegments, AppPaths.LibraryRoot);
            var newFolder = PathHelper.ResolveNodeFolder(newPathSegments, AppPaths.LibraryRoot);

            if (!string.Equals(oldFolder, newFolder, StringComparison.Ordinal))
            {
                try
                {
                    var hasAssets = NodeAssetFolderHelper.HasAnyAssetFolders(oldFolder);
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
                        var renamedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (!NodeAssetFolderHelper.MoveAssetFoldersRecursive(sourceNode, oldPathSegments, newPathSegments, renamedFiles))
                            return false;

                        var oldRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, oldFolder);
                        var newRelativePrefix = Path.GetRelativePath(AppPaths.DataRoot, newFolder);

                        NodeAssetFolderHelper.UpdateAssetPathsRecursive(sourceNode, oldRelativePrefix, newRelativePrefix, renamedFiles);
                    }
                    else
                    {
                        NodeAssetFolderHelper.DeleteDirectoryIfEmpty(oldFolder);
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
        if (parentChanged)
            await SaveData().ConfigureAwait(false);
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

    private static void MergeNodes(MediaNode source, MediaNode target)
    {
        if (ReferenceEquals(source, target))
            return;

        var existingAssets = new HashSet<(AssetType Type, string Path)>(target.Assets.Count);
        foreach (var asset in target.Assets)
        {
            var path = NodeAssetFolderHelper.NormalizeRelativePath(asset.RelativePath ?? string.Empty);
            existingAssets.Add((asset.Type, path));
        }

        foreach (var asset in source.Assets.ToList())
        {
            var path = NodeAssetFolderHelper.NormalizeRelativePath(asset.RelativePath ?? string.Empty);
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

    // --- Randomization Helpers ---

    private bool IsRandomizeActive(MediaNode targetNode)
    {
        // Reverse chain to find nearest configuration (bottom-up)
        var chain = PathHelper.GetNodeChain(targetNode, RootItems, matchById: true); 
        chain.Reverse();
        return chain.FirstOrDefault(n => n.RandomizeCovers.HasValue)?.RandomizeCovers ?? false;
    }

    private bool IsRandomizeMusicActive(MediaNode targetNode)
    {
        var chain = PathHelper.GetNodeChain(targetNode, RootItems, matchById: true); 
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
