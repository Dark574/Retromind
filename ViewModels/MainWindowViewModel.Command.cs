using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
using Retromind.Services.BigMode;
using Retromind.Services.Stores.Epic;
using Retromind.Views;

namespace Retromind.ViewModels;

public partial class MainWindowViewModel
{
    private const int BulkSortedInsertThreshold = 32;
    private static readonly TimeSpan DelegatedLaunchGuardDuration = TimeSpan.FromSeconds(10);
    private HelpGuidesView? _helpGuidesView;

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
    public IAsyncRelayCommand<MediaNode?> DeleteCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> EditMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand BulkEditSelectedMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> TestPlayMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ViewLastLaunchLogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> MoveMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> DeleteMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ToggleItemProtectionCommand { get; private set; } = null!;
    public IAsyncRelayCommand ToggleParentalLockCommand { get; private set; } = null!;
    public IAsyncRelayCommand ChangeParentalPasswordCommand { get; private set; } = null!;
    
    // PlayCommand is special, it fires and forgets mostly, but async is better for UI responsiveness
    public IAsyncRelayCommand<MediaItem?> PlayCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> InstallStoreItemCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> CheckGogUpdatesCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ReinstallGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> UpdateGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> UninstallGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> CheckEpicUpdatesCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> UpdateEpicCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> RepairEpicCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> UninstallEpicCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ManageEpicDlcsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> ManageGogDlcsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> LinkMediaItemToGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> LinkMediaItemToEpicCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand OpenSettingsCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenSetupSettingsCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenStatisticsCommand { get; private set; } = null!;
    public IRelayCommand RetryMediaViewCommand { get; private set; } = null!;
    public IRelayCommand OpenHelpGuidesCommand { get; private set; } = null!;
    public IRelayCommand CloseGettingStartedCommand { get; private set; } = null!;
    public IAsyncRelayCommand CreateFirstCategoryCommand { get; private set; } = null!;
    public IAsyncRelayCommand OpenMetadataBackupsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> EditNodeCommand { get; private set; } = null!;
    public IRelayCommand ToggleThemeCommand { get; private set; } = null!; // Sync is fine here
    
    public IAsyncRelayCommand<MediaNode?> ImportRomsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportSteamCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportEpicCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ImportGogCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> IdentifyNodeWithRetroAchievementsCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaItem?> IdentifyMediaWithRetroAchievementsCommand { get; private set; } = null!;
    
    public IAsyncRelayCommand<MediaItem?> ScrapeMediaCommand { get; private set; } = null!;
    public IAsyncRelayCommand<MediaNode?> ScrapeNodeCommand { get; private set; } = null!;
    public IRelayCommand OpenSearchCommand { get; private set; } = null!;
    public IRelayCommand<string?> SaveSearchTermCommand { get; private set; } = null!;
    public IRelayCommand<string?> RemoveSavedSearchTermCommand { get; private set; } = null!;

    // Command to enter the Big Picture / Themed mode
    public IRelayCommand EnterBigModeCommand { get; private set; } = null!;
    // Command to attach new manuals/documents directly from the main grid context menu
    public IAsyncRelayCommand<MediaItem?> AddManualToMediaCommand { get; private set; } = null!;

    public string ImportGogText => T("Gog.Media.AddMenu", "Import GOG games...");
    public string TestPlayMediaMenuText => T("Ctx.Media.TestLaunch", "Test launch (without tracking)");
    public string ViewLastLaunchLogMenuText => T("Launch.ViewLastLog", "View last launch log");
    public string MoveMediaMenuText => T("Ctx.Media.Move", "Move to category...");
    public string BulkEditSelectedMediaText => T("BulkEdit.Selection.Edit", "Edit selected...");
    public string RetroAchievementsSearchAllText => T(
        "RetroAchievements_SearchAll",
        "Search RetroAchievements (All)...");
    public string RetroAchievementsSearchItemText => T(
        "RetroAchievements_SearchItem",
        "Search RetroAchievements...");
    public string MetadataSearchItemText => T(
        "Metadata.SearchMetadataMenu",
        "Search metadata...");
    public string StatisticsButtonToolTip => T("Statistics.Title", "Library statistics");
    public string GogCheckUpdatesMenuText => T("Gog.Update.CheckNow", "Check for GOG updates");
    public string GogUpdateMenuText => T("Button_Update", "Update");
    public string GogReinstallMenuText => T("Button_ReinstallOrSwitchVersion", "Reinstall / Switch Version");
    public string GogUninstallMenuText => T("Gog.Uninstall.ContextMenu", "Uninstall");
    public string EpicCheckUpdatesMenuText => T("Epic.Update.CheckNow", "Check for Epic Games updates");
    public string EpicUpdateMenuText => T("Button_Update", "Update");
    public string EpicRepairMenuText => T("Epic.Repair.ContextMenu", "Repair installation");
    public string EpicUninstallMenuText => T("Epic.Uninstall.ContextMenu", "Uninstall");
    public string EpicManageDlcsMenuText => T("Epic.Dlc.ContextMenu", "Manage DLCs...");
    public string GogManageDlcsMenuText => T("EditMedia.GogManageDlcs", "Manage DLCs...");
    public string StoreContextMenuText => T("Store.ContextMenu", "Store");
    public string GogLinkStoreMenuText => T("Gog.Link.Button", "Link to GOG game...");
    public string GogSwitchStoreMenuText => T("Gog.Link.SwitchButton", "Switch to GOG...");
    public string EpicLinkStoreMenuText => T("Epic.Link.Button", "Link to Epic Games title...");
    public string EpicSwitchStoreMenuText => T("Epic.Link.SwitchButton", "Switch to Epic Games...");

    private void InitializeCommands()
    {
        // Replaced RelayCommand with AsyncRelayCommand to handle Tasks properly
        // and avoid "async void" pitfalls
        
        AddCategoryCommand = new AsyncRelayCommand<MediaNode?>(AddCategoryAsync, _ => !_libraryLoadFailed);
        AddMediaCommand = new AsyncRelayCommand<MediaNode?>(AddMediaAsync, CanOperateOnNode);
        AddEmptyMediaCommand = new AsyncRelayCommand<MediaNode?>(AddEmptyMediaAsync, CanOperateOnNode);
        DeleteCommand = new AsyncRelayCommand<MediaNode?>(DeleteNodeAsync);
        
        EditMediaCommand = new AsyncRelayCommand<MediaItem?>(EditMediaAsync);
        BulkEditSelectedMediaCommand = new AsyncRelayCommand(BulkEditSelectedMediaAsync);
        TestPlayMediaCommand = new AsyncRelayCommand<MediaItem?>(TestPlayMediaAsync, CanTestPlayMedia);
        ViewLastLaunchLogCommand = new AsyncRelayCommand<MediaItem?>(
            ViewLastLaunchLogAsync,
            item => item != null && _launchLogService.HasLog(item.Id));
        MoveMediaCommand = new AsyncRelayCommand<MediaItem?>(MoveMediaAsync);
        DeleteMediaCommand = new AsyncRelayCommand<MediaItem?>(DeleteMediaAsync);
        ToggleItemProtectionCommand = new AsyncRelayCommand<MediaItem?>(ToggleItemProtectionAsync);
        ToggleParentalLockCommand = new AsyncRelayCommand(ToggleParentalLockAsync);
        ChangeParentalPasswordCommand = new AsyncRelayCommand(ChangeParentalPasswordAsync);
        PlayCommand = new AsyncRelayCommand<MediaItem?>(item => PlayMediaAsync(item), CanPlayMedia);
        InstallStoreItemCommand = new AsyncRelayCommand<MediaItem?>(InstallStoreMediaAsync, CanInstallStoreMedia);
        CheckGogUpdatesCommand = new AsyncRelayCommand<MediaItem?>(_gogWorkflow.CheckGogUpdatesNowAsync, _gogWorkflow.CanCheckGogUpdatesForItem);
        ReinstallGogCommand = new AsyncRelayCommand<MediaItem?>(_gogWorkflow.ReinstallGogMediaAsync, _gogWorkflow.CanReinstallGogMedia);
        UpdateGogCommand = new AsyncRelayCommand<MediaItem?>(_gogWorkflow.UpdateGogMediaAsync, _gogWorkflow.CanUpdateGogMedia);
        UninstallGogCommand = new AsyncRelayCommand<MediaItem?>(_gogWorkflow.UninstallGogMediaAsync, _gogWorkflow.CanUninstallGogMedia);
        CheckEpicUpdatesCommand = new AsyncRelayCommand<MediaItem?>(_epicWorkflow.CheckEpicUpdatesNowAsync, _epicWorkflow.CanCheckEpicUpdates);
        UpdateEpicCommand = new AsyncRelayCommand<MediaItem?>(_epicWorkflow.UpdateEpicMediaAsync, _epicWorkflow.CanUpdateEpicMedia);
        RepairEpicCommand = new AsyncRelayCommand<MediaItem?>(_epicWorkflow.RepairEpicMediaAsync, _epicWorkflow.CanRepairEpicMedia);
        UninstallEpicCommand = new AsyncRelayCommand<MediaItem?>(_epicWorkflow.UninstallEpicMediaAsync, _epicWorkflow.CanUninstallEpicMedia);
        ManageEpicDlcsCommand = new AsyncRelayCommand<MediaItem?>(_epicWorkflow.ShowEpicDlcManagerAsync, _epicWorkflow.CanManageEpicDlcs);
        ManageGogDlcsCommand = new AsyncRelayCommand<MediaItem?>(_gogWorkflow.ShowGogDlcManagerAsync, _gogWorkflow.CanManageGogDlcs);
        LinkMediaItemToGogCommand = new AsyncRelayCommand<MediaItem?>(
            _gogWorkflow.LinkMediaItemToGogFromContextAsync,
            _gogWorkflow.CanLinkMediaItemToGog);
        LinkMediaItemToEpicCommand = new AsyncRelayCommand<MediaItem?>(
            _epicWorkflow.LinkMediaItemToEpicFromContextAsync,
            _epicWorkflow.CanLinkMediaItemToEpic);
        
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        OpenSetupSettingsCommand = new AsyncRelayCommand(OpenSetupSettingsAsync);
        OpenStatisticsCommand = new AsyncRelayCommand(OpenStatisticsAsync);
        RetryMediaViewCommand = new RelayCommand(UpdateContent, () => ShowMediaViewLoadError);
        OpenHelpGuidesCommand = new RelayCommand(OpenHelpGuides);
        CloseGettingStartedCommand = new RelayCommand(CloseGettingStarted);
        CreateFirstCategoryCommand = new AsyncRelayCommand(CreateFirstCategoryAsync);
        OpenMetadataBackupsCommand = new AsyncRelayCommand(OpenMetadataBackupsFromMainWindowAsync);
        OpenManualCommand = new RelayCommand<MediaAsset?>(OpenManual);
        EditNodeCommand = new AsyncRelayCommand<MediaNode?>(EditNodeAsync);
        
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        
        ImportRomsCommand = new AsyncRelayCommand<MediaNode?>(ImportRomsAsync, CanOperateOnNode);
        ImportSteamCommand = new AsyncRelayCommand<MediaNode?>(ImportSteamAsync, CanOperateOnNode);
        ImportEpicCommand = new AsyncRelayCommand<MediaNode?>(node => _epicWorkflow.ImportEpicAsync(node ?? SelectedNode), CanOperateOnNode);
        ImportGogCommand = new AsyncRelayCommand<MediaNode?>(node => _gogWorkflow.ImportGogAsync(node ?? SelectedNode), CanOperateOnNode);
        IdentifyNodeWithRetroAchievementsCommand = new AsyncRelayCommand<MediaNode?>(
            IdentifyNodeWithRetroAchievementsAsync,
            CanIdentifyNodeWithRetroAchievements);
        IdentifyMediaWithRetroAchievementsCommand = new AsyncRelayCommand<MediaItem?>(
            IdentifyMediaWithRetroAchievementsAsync,
            CanIdentifyMediaWithRetroAchievements);
        
        ScrapeMediaCommand = new AsyncRelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new AsyncRelayCommand<MediaNode?>(ScrapeNodeAsync, CanOperateOnNode);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);
        SaveSearchTermCommand = new RelayCommand<string?>(SaveSearchTerm);
        RemoveSavedSearchTermCommand = new RelayCommand<string?>(RemoveSavedSearchTerm);
        
        EnterBigModeCommand = new RelayCommand(EnterBigMode, () => !_libraryLoadFailed);
        
        AddManualToMediaCommand = new AsyncRelayCommand<MediaItem?>(AddManualToMediaAsync);
    }

    private void CloseGettingStarted()
    {
        IsGettingStartedDismissed = true;
    }

    private async Task CreateFirstCategoryAsync()
    {
        var previousRootCount = RootItems.Count;
        await AddCategoryAsync(null);

        if (RootItems.Count > previousRootCount)
        {
            SelectedNode = RootItems[^1];
            OnPropertyChanged(nameof(ShowGettingStarted));
        }
    }

    private async Task OpenMetadataBackupsFromMainWindowAsync()
    {
        if (CurrentWindow is not { } owner)
            return;

        var restored = await OpenMetadataBackupsAsync(owner);
        if (restored)
            UiThreadHelper.Post(owner.Close, DispatcherPriority.Background);
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
                if (_desktopContent.SearchArea == null)
                    OpenIntegratedSearch();

                if (_desktopContent.SearchArea is not { } search)
                    return;

                _desktopContent.ClearPendingSearchSelection();
                search.SelectedMediaItem = null;
                search.ApplyScopeSelection(RootItems.Select(root => root.Id).ToArray());
                search.SearchYear = string.Empty;
                search.SelectedStatus = null;
                search.SearchText = query;
                search.OnlyFavorites = favoritesOnly;
            });
            return;
        }

        await UiThreadHelper.InvokeAsync(() =>
        {
            _desktopContent.SetMediaFilters(query, favoritesOnly);

            _desktopContent.RememberNodeDeselection(request.ScopeNode.Id);
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
            _desktopContent.SetMediaFilters(string.Empty, onlyFavorites: false);

            _desktopContent.RememberNodeSelection(targetNode.Id, item.Id);
            _currentSettings.LastSelectedMediaId = item.Id;
            ExpandPathToNode(RootItems, targetNode);

            if (ReferenceEquals(SelectedNode, targetNode))
                UpdateContent();
            else
                SelectedNode = targetNode;
        });

        // The branch above always starts exactly one refresh: either explicitly
        // for the current node or through the SelectedNode setter.
        if (!await AwaitCurrentContentUpdateAsync())
            return;

        await UiThreadHelper.InvokeAsync(() =>
        {
            if (!ReferenceEquals(SelectedNode, targetNode) || _desktopContent.MediaArea is not { } mediaViewModel)
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
            if (IsDelegatedLaunchPending)
                return T("Button.Launching", "Starting...");

            var item = GetCurrentSelectedItem();
            if (ShouldOfferInstallForItem(item))
                return T("Button_Install", "Install");
            return Strings.Button_Play;
        }
    }
    public string StoreUpdateAvailableBadgeTooltip => T("Store.Update.BadgeTooltip", "Game update available");

    private void NotifyPlayAvailabilityChanged()
    {
        PlayCommand.NotifyCanExecuteChanged();
        InstallStoreItemCommand.NotifyCanExecuteChanged();
        TestPlayMediaCommand.NotifyCanExecuteChanged();
        CheckGogUpdatesCommand.NotifyCanExecuteChanged();
        ReinstallGogCommand.NotifyCanExecuteChanged();
        UpdateGogCommand.NotifyCanExecuteChanged();
        UninstallGogCommand.NotifyCanExecuteChanged();
        CheckEpicUpdatesCommand.NotifyCanExecuteChanged();
        UpdateEpicCommand.NotifyCanExecuteChanged();
        RepairEpicCommand.NotifyCanExecuteChanged();
        UninstallEpicCommand.NotifyCanExecuteChanged();
        ManageEpicDlcsCommand.NotifyCanExecuteChanged();
        ManageGogDlcsCommand.NotifyCanExecuteChanged();
        LinkMediaItemToGogCommand.NotifyCanExecuteChanged();
        LinkMediaItemToEpicCommand.NotifyCanExecuteChanged();
        IdentifyMediaWithRetroAchievementsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPlaySelectedMedia));
        OnPropertyChanged(nameof(PlaySelectedMediaButtonText));
    }

    private bool CanPlayMedia(MediaItem? item)
    {
        if (IsLaunchInProgress || IsMultiSelectModeActive || item == null ||
            ParentalControlHelper.IsPlaybackBlocked(_currentSettings, item))
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

    private static bool ShouldOfferInstallForItem(MediaItem? item) =>
        DesktopStoreWorkflow.ShouldOfferInstallForItem(item);

    private bool CanInstallStoreMedia(MediaItem? item) =>
        !IsLaunchInProgress &&
        !IsMultiSelectModeActive &&
        (GogMediaItemStateHelper.CanInstallWithRetromind(item) ||
         EpicMediaItemStateHelper.CanInstallWithRetromind(item));

    private bool IsMultiSelectModeActive =>
        _desktopContent.MediaArea?.IsMultiSelectMode == true ||
        _desktopContent.SearchArea?.IsMultiSelectMode == true;

    private bool CanTestPlayMedia(MediaItem? item)
        => CanPlayMedia(item) && !ShouldOfferInstallForItem(item);

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
        if (_libraryLoadFailed || _bigModeSession.IsOpen)
            return;

        Debug.WriteLine("[CoreApp] EnterBigMode starting.");

        // Stop music immediately to avoid overlap and to keep the UI responsive.
        _audioService.StopMusic();

        // Direct BigMode startup and desktop views without an active media item
        // begin on Home. An explicit CoreApp item selection keeps the established
        // behavior and opens at that exact library position.
        var startOnHome = _currentSettings.EnableBigModeHomeScreen &&
                          (App.Current?.IsBigModeOnly == true || GetCurrentSelectedItem() == null);

        // Ensure we have a valid node selection once the library is loaded.
        if (SelectedNode == null)
            SelectedNode = FindFirstVisibleNode();

        _bigModeSession.Open(initialTheme => new BigModeViewModel(
            RootItems,
            _currentSettings,
            initialTheme,
            _soundEffectService,
            _gamepadService,
            _retroAchievementsProgressService,
            _retroAchievementsBadgeService,
            _steamAchievementsProgressService,
            _errorService,
            IsParentalFilterActive,
            startOnHome,
            _gogAchievementsProgressService,
            _storeAchievementImageCacheService));
    }
    
    private bool _synchronizingSelectionFromBigMode;

    private void UpdateBigModeStateFromCoreSelection(MediaNode node, MediaItem? selectedItem)
    {
        if (IsLibraryLoading || _synchronizingSelectionFromBigMode || _bigModeSession.ActiveViewModel != null) return;
        BigModeNavigationPersistence.RecordDesktopSelection(_currentSettings, node, selectedItem);

        SaveSettingsOnly();
    }
    
    public async Task SyncSelectionFromBigModeAsync()
    {
        _synchronizingSelectionFromBigMode = true;
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
                    _desktopContent.RememberNodeSelection(node.Id, desiredItemId);

                if (ReferenceEquals(SelectedNode, node))
                    UpdateContent();
                else
                    SelectedNode = node;
            });

            // 3) Wait until the grid (SelectedNodeContent) is actually there.
            if (!await AwaitCurrentContentUpdateAsync())
                return;

            // 4) Select item in the grid if we have a concrete item id.
            // Do not rely on LastBigModeWasItemView alone; some themes can end up desyncing it.
            if (string.IsNullOrWhiteSpace(desiredItemId))
                return;

            await UiThreadHelper.InvokeAsync(() =>
            {
                if (!ReferenceEquals(SelectedNode, node) || _desktopContent.MediaArea is not { } mediaVm)
                    return;

                var item = mediaVm.Node?.Items?.FirstOrDefault(i => i.Id == desiredItemId);
                if (item != null)
                    mediaVm.SelectedMediaItem = item;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CoreApp] SyncSelectionFromBigModeAsync failed: {ex}");
        }
        finally
        {
            _synchronizingSelectionFromBigMode = false;
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
        ImportRomsCommand.NotifyCanExecuteChanged();
        ImportSteamCommand.NotifyCanExecuteChanged();
        ImportEpicCommand.NotifyCanExecuteChanged();
        ImportGogCommand.NotifyCanExecuteChanged();
        ScrapeNodeCommand.NotifyCanExecuteChanged();
        IdentifyNodeWithRetroAchievementsCommand.NotifyCanExecuteChanged();
        IdentifyMediaWithRetroAchievementsCommand.NotifyCanExecuteChanged();
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
                    RootItems.Add(new MediaNode(name));
                }
                else
                {
                    parentNode.Children.Add(new MediaNode(name)
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

            var removed = RootItems.Remove(nodeToDelete) ||
                          RemoveNodeRecursive(RootItems, nodeToDelete);
            if (!removed)
                return;

            var saved = await SaveData();
            if (saved && !_libraryLoadFailed)
            {
                _launchLogService.TryDeleteForSubtree(nodeToDelete);
                ViewLastLaunchLogCommand.NotifyCanExecuteChanged();
            }
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
            _errorService,
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

    private Task TestPlayMediaAsync(MediaItem? item)
        => PlayMediaAsync(item, recordStatistics: false);

    private async Task InstallStoreMediaAsync(MediaItem? item)
    {
        if (!CanInstallStoreMedia(item) || item == null || CurrentWindow is not { } owner)
            return;

        if (EpicMediaItemStateHelper.TryGetGameId(item) != null)
        {
            _ = await _epicWorkflow.RunEpicInstallFromEditorAsync(item, owner);
            return;
        }

        _ = await _gogWorkflow.RunGogInstallFromEditorAsync(
            item,
            owner,
            requireAvailableUpdate: false);
    }

    private Task<bool> InstallStoreItemAsync(MediaItem item)
    {
        return EpicMediaItemStateHelper.TryGetGameId(item) != null
            ? _epicWorkflow.InstallEpicItemAsync(item)
            : _gogWorkflow.InstallGogItemAsync(item);
    }

    private async Task<LaunchResult?> PlayMediaAsync(MediaItem? item, bool recordStatistics = true)
    {
        if (item == null)
            return null;

        if (!CanPlayMedia(item))
            return null;

        // Global launch guard: ignore additional requests while one is in progress.
        if (IsLaunchInProgress)
            return null;

        var isDelegatedLaunch = string.IsNullOrWhiteSpace(item.OverrideWatchProcess) &&
                                LauncherService.IsDelegatedCommand(item);
        var launchGuardStartedAt = Stopwatch.GetTimestamp();
        IsDelegatedLaunchPending = isDelegatedLaunch;
        IsLaunchInProgress = true;
        
        // Stop music immediately for responsiveness
        _audioService.StopMusic();

        try
        {
            if (ShouldOfferInstallForItem(item))
            {
                await InstallStoreItemAsync(item);
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
            EpicLaunchPreparation? epicLaunch = null;
            if (EpicMediaItemStateHelper.IsRetromindManagedInstallation(item) &&
                CurrentWindow is { } epicOwner)
            {
                epicLaunch = await _epicWorkflow.PrepareEpicLaunchAsync(item, epicOwner);
                if (epicLaunch == null)
                    return null;

                effectiveEnvironment = DesktopEpicWorkflow.MergeEpicLaunchEnvironment(
                    effectiveEnvironment,
                    epicLaunch.Environment);
            }

            var launchResult = await _launcherService.LaunchAsync(
                item,
                emulator,
                nodePath,
                nativeWrappers: effectiveWrappers,
                environmentOverrides: effectiveEnvironment,
                additionalLaunchArguments: epicLaunch?.Arguments,
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

            var delegatedLaunchSucceeded = isDelegatedLaunch &&
                                           launchResult.Outcome == LaunchOutcome.Started &&
                                           !launchResult.WasSessionTracked;

            // A delegated store handoff has no reliable game-exit boundary. Do
            // not restart preview music while the external game is still opening.
            if (!delegatedLaunchSucceeded && ReferenceEquals(GetCurrentSelectedItem(), item))
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
                launchResult.WasSessionTracked)
            {
                var activeBigMode = _bigModeSession.ActiveViewModel;
                if (activeBigMode != null && ReferenceEquals(activeBigMode.SelectedItem, item))
                {
                    activeBigMode.RefreshAchievementsAfterTrackedSession(item);
                }
                else if (ReferenceEquals(GetCurrentSelectedItem(), item))
                {
                    _ = RetroAchievementsProgress.SelectItemAsync(item, forceRefresh: true);
                }
            }

            if (delegatedLaunchSucceeded)
            {
                var elapsed = Stopwatch.GetElapsedTime(launchGuardStartedAt);
                var remaining = DelegatedLaunchGuardDuration - elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining);
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
            IsDelegatedLaunchPending = false;
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
        string? preservedEpicInstallDirectory = null;
        
        try
        {
            if (EpicMediaItemStateHelper.HasRetromindManagedInstallMetadata(item))
            {
                var (canDelete, preservedDirectory) = await _epicWorkflow.PrepareLibraryItemDeletionAsync(item, owner);
                if (!canDelete)
                    return;
                preservedEpicInstallDirectory = preservedDirectory;
            }
            else if (!await ShowConfirmDialog(owner, Strings.Dialog_MsgConfirmDelete))
            {
                return;
            }

            if (ReferenceEquals(item, GetCurrentSelectedItem()))
            {
                if (_desktopContent.SearchArea is { } searchVm)
                    searchVm.SelectedMediaItem = null;
                else
                    _audioService.StopMusic();
            }
            
            var parentNode = FindParentNode(RootItems, item);
            if (parentNode != null)
            {
                parentNode.Items.Remove(item);
                var saved = await SaveData();
                if (saved && !_libraryLoadFailed)
                {
                    _launchLogService.TryDelete(item.Id);
                    ViewLastLaunchLogCommand.NotifyCanExecuteChanged();
                }
                
                RefreshContentAfterMediaCollectionChange();
            }

            await _epicWorkflow.ShowPreservedInstallDirectoryAsync(owner, preservedEpicInstallDirectory);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Error] DeleteMedia failed: {ex.Message}");
        }
    }

    // --- Dialog Helpers ---

    private static Task<bool> ShowConfirmDialog(Window owner, string message) =>
        DesktopDialogs.ShowConfirmAsync(owner, message);

    private static Task ShowInfoDialog(Window owner, string message, bool showCopyButton = true) =>
        DesktopDialogs.ShowInfoAsync(owner, message, showCopyButton);

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

    private void OpenHelpGuides()
    {
        if (_helpGuidesView != null)
        {
            _helpGuidesView.WindowState = WindowState.Normal;
            _helpGuidesView.Activate();
            return;
        }

        var helpView = new HelpGuidesView
        {
            DataContext = new HelpGuidesViewModel()
        };
        _helpGuidesView = helpView;
        helpView.Closed += (_, _) => _helpGuidesView = null;

        if (CurrentWindow is { } owner)
            helpView.Show(owner);
        else
            helpView.Show();
    }

    private Task OpenSettingsAsync()
        => OpenSettingsAsync(initialTabIndex: null);

    private Task OpenSetupSettingsAsync()
        => OpenSettingsAsync(initialTabIndex: 2);

    private async Task OpenSettingsAsync(int? initialTabIndex)
    {
        if (CurrentWindow is not { } owner) return;

        var portableHomeWasEnabled = _currentSettings.UsePortableHomeInAppImage;
        var (settingsVm, dialog) = await _settingsWorkflow.CreateDialogAsync(
            () => RootItems,
            _gamepadService,
            initialTabIndex,
            message => ShowConfirmDialog(owner, message),
            _libraryTracker.MarkDirty,
            SaveData);

        settingsVm.RequestOpenHelpGuides += OpenHelpGuides;
        settingsVm.RequestSortPreviewRefresh += UpdateContent;
        settingsVm.RequestOpenMetadataBackups += async () =>
        {
            var restored = await OpenMetadataBackupsAsync(dialog);
            if (!restored)
                return;

            dialog.Close();
            UiThreadHelper.Post(owner.Close, DispatcherPriority.Background);
        };

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
        await _settingsWorkflow.CompleteAsync(
            settingsVm,
            portableHomeWasEnabled,
            _metadataBackups.RequiresRestart,
            _libraryTracker.MarkDirty,
            SaveDataWithStatusAsync,
            settingsAccepted: () =>
            {
                _downloads.SettingsChanged();
                OnPropertyChanged(nameof(ShowStoreBadges));
                _metadataService.ClearProviderCache();
                NotifyNodeCommandsCanExecuteChanged();
            },
            refreshAchievements: forceRefresh =>
                RetroAchievementsProgress.SelectItemAsync(GetCurrentSelectedItem(), forceRefresh: forceRefresh));
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

    private void SortAllNodesRecursive(
        IEnumerable<MediaNode> nodes,
        CancellationToken cancellationToken)
    {
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Only sort if needed to avoid overhead on every start
            // (Assuming items are added roughly in order or user doesn't care about strict alphabetical all the time)
            SortMediaItems(node.Items);
            SortAllNodesRecursive(node.Children, cancellationToken);
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

    internal NodeAssetOperationResult? LastNodeAssetOperation { get; private set; }
    internal NodeAssetFileOperations? NodeAssetFileOperations { get; set; }

    public Task<bool> TryMoveNodeAsync(MediaNode sourceNode, MediaNode targetNode, NodeDropPosition dropPosition) =>
        TryMoveNodeAsync(sourceNode, targetNode, dropPosition, null, null, null);

    internal async Task<bool> TryMoveNodeAsync(MediaNode sourceNode, MediaNode targetNode, NodeDropPosition dropPosition,
        Func<string, Task<bool>>? confirm, Action<MediaNode?>? selectNode, Func<Task<bool>>? save)
    {
        LastNodeAssetOperation = null;
        var traversal = NodeAssetFolderHelper.ValidateTraversal(RootItems);
        if (!traversal.Success)
        {
            LastNodeAssetOperation = traversal;
            NodeAssetDiagnostics.Report(_errorService, traversal, "Move", sourceNode?.Id ?? "");
            return false;
        }
        async Task<bool> Confirm(string message)
        {
            try { return confirm != null ? await confirm(message)
                : CurrentWindow is { } window && await ShowConfirmDialog(window, message); }
            catch (OperationCanceledException) { return false; }
            catch (Exception ex)
            {
                LastNodeAssetOperation = new NodeAssetOperationResult { FailurePhase = "Confirmation" };
                LastNodeAssetOperation.Fail("NodeAssets.MoveFailed", ex);
                NodeAssetDiagnostics.Report(_errorService, LastNodeAssetOperation, "Move", sourceNode?.Id ?? "");
                return false;
            }
        }
        void Select(MediaNode? node)
        {
            if (selectNode != null) selectNode(node);
            else SelectedNode = node;
        }
        if (sourceNode == null || targetNode == null)
            return false;

        if (ReferenceEquals(sourceNode, targetNode))
            return false;

        var sourceChain = PathHelper.GetNodeChain(sourceNode, RootItems, matchById: true);
        if (sourceChain.Count == 0 || !ReferenceEquals(sourceChain[^1], sourceNode))
            return false;

        var targetChain = PathHelper.GetNodeChain(targetNode, RootItems, matchById: true);
        if (targetChain.Count == 0 || !ReferenceEquals(targetChain[^1], targetNode))
            return false;

        if (IsChildOf(sourceNode, targetNode))
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
                var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeFormat, targetNode.Name);
                if (!await Confirm(mergeMessage))
                    return false;

                mergeTarget = targetNode;
            }
            else
            {
                var destinationCollection = newParent == null ? RootItems : newParent.Children;
                mergeTarget = FindNameCollision(destinationCollection, sourceNode);
                if (mergeTarget != null)
                {
                    var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeFormat, mergeTarget.Name);
                    if (!await Confirm(mergeMessage))
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
                var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeFormat, collision.Name);
                if (!await Confirm(mergeMessage))
                    return false;

                mergeTarget = collision;
            }
        }

        if (parentChanged)
        {
            var confirmMessage = string.Format(Strings.Dialog_ConfirmMoveNodeAssetsFormat, sourceNode.Name);
            if (!await Confirm(confirmMessage))
                return false;
        }

        var sourceCollection = sourceParent == null ? RootItems : sourceParent.Children;
        var targetCollection = targetParent == null ? RootItems : targetParent.Children;

        var sourceIndex = sourceCollection.IndexOf(sourceNode);
        var targetIndex = targetCollection.IndexOf(targetNode);

        if (sourceIndex < 0 || targetIndex < 0)
            return false;

        if (!parentChanged)
        {
            if (dropPosition == NodeDropPosition.Inside &&
                (ReferenceEquals(sourceParent, targetNode) || ReferenceEquals(sourceCollection, targetNode.Children))) return false;
            if (dropPosition != NodeDropPosition.Inside && ReferenceEquals(sourceCollection, targetCollection) &&
                ((sourceIndex == targetIndex && dropPosition == NodeDropPosition.Before) ||
                 (sourceIndex == targetIndex + 1 && dropPosition == NodeDropPosition.After))) return false;
        }
        void ApplyTreeChange()
        {
            if (mergeTarget != null && parentChanged)
            {
                sourceCollection.RemoveAt(sourceIndex);
                MergeNodes(sourceNode, mergeTarget);
                Select(mergeTarget);
            }
            else if (dropPosition == NodeDropPosition.Inside)
            {
                sourceCollection.RemoveAt(sourceIndex);
                targetNode.Children.Add(sourceNode);
                targetNode.IsExpanded = true;
            }
            else
            {
                if (ReferenceEquals(sourceCollection, targetCollection))
                {
                    if (sourceIndex < targetIndex)
                        targetIndex -= 1;
                }

                sourceCollection.RemoveAt(sourceIndex);

                var insertIndex = dropPosition == NodeDropPosition.Before ? targetIndex : targetIndex + 1;
                if (insertIndex < 0) insertIndex = 0;
                if (insertIndex > targetCollection.Count) insertIndex = targetCollection.Count;

                targetCollection.Insert(insertIndex, sourceNode);
            }

            if (parentChanged && !NodeAssetFolderHelper.ValidateTraversal(RootItems).Success)
                throw new InvalidOperationException("The category assignment produced an invalid tree.");
            if (mergeTarget == null)
                Select(sourceNode);
        }
        if (!parentChanged)
        {
            ApplyTreeChange();
            return true;
        }

        traversal = NodeAssetFolderHelper.ValidateTraversal(RootItems);
        if (!traversal.Success)
        {
            LastNodeAssetOperation = traversal;
            NodeAssetDiagnostics.Report(_errorService, traversal, "Move", sourceNode.Id);
            return false;
        }
        var oldPathSegments = PathHelper.GetNodePath(sourceNode, RootItems);
        var newPathSegments = mergeTarget != null ? PathHelper.GetNodePath(mergeTarget, RootItems)
            : BuildNodePathSegments(newParent, sourceNode.Name);
        var oldFolder = PathHelper.ResolveNodeFolder(oldPathSegments, AppPaths.LibraryRoot);
        var newFolder = PathHelper.ResolveNodeFolder(newPathSegments, AppPaths.LibraryRoot);
        if (!string.Equals(oldFolder, newFolder, StringComparison.Ordinal) &&
            NodeAssetFolderHelper.HasAnyAssetFolders(oldFolder) && Directory.Exists(newFolder))
        {
            var mergeMessage = string.Format(Strings.Dialog_ConfirmMergeNodeAssetsFormat, sourceNode.Name);
            if (!await Confirm(mergeMessage)) return false;
        }

        traversal = NodeAssetFolderHelper.ValidateTraversal(RootItems);
        if (!traversal.Success)
        {
            LastNodeAssetOperation = traversal;
            NodeAssetDiagnostics.Report(_errorService, traversal, "Move", sourceNode.Id);
            return false;
        }
        sourceIndex = sourceCollection.IndexOf(sourceNode);
        targetIndex = targetCollection.IndexOf(targetNode);
        if (sourceIndex < 0 || targetIndex < 0) return false;
        oldPathSegments = PathHelper.GetNodePath(sourceNode, RootItems);
        newPathSegments = mergeTarget != null ? PathHelper.GetNodePath(mergeTarget, RootItems)
            : BuildNodePathSegments(newParent, sourceNode.Name);

        var placement = NodeAssetFolderHelper.ValidateTraversal(new[] { sourceNode }, newPathSegments.Count - 1);
        if (!placement.Success)
        {
            LastNodeAssetOperation = placement;
            NodeAssetDiagnostics.Report(_errorService, placement, "Move", sourceNode.Id);
            return false;
        }
        NodeAssignmentSnapshot snapshot;
        try
        {
            snapshot = new NodeAssignmentSnapshot(sourceNode, mergeTarget ?? targetNode, sourceCollection,
                dropPosition == NodeDropPosition.Inside ? targetNode.Children : targetCollection);
        }
        catch (Exception ex)
        {
            LastNodeAssetOperation = new NodeAssetOperationResult { FailurePhase = "PrepareMetadata" };
            LastNodeAssetOperation.Fail("NodeAssets.PathUpdateFailed", ex);
            NodeAssetDiagnostics.Report(_errorService, LastNodeAssetOperation, "Move", sourceNode.Id);
            return false;
        }
        var previousSelection = SelectedNode;
        var previousExpanded = targetNode.IsExpanded;
        LastNodeAssetOperation = NodeAssetFolderHelper.MoveAndUpdateAssetPaths(sourceNode, oldPathSegments, newPathSegments,
            ApplyTreeChange, () =>
            {
                snapshot.Restore();
                targetNode.IsExpanded = previousExpanded;
                Select(previousSelection);
            }, NodeAssetFileOperations ?? new NodeAssetFileOperations());
        NodeAssetDiagnostics.Report(_errorService, LastNodeAssetOperation, "Move", sourceNode.Id);
        if (!LastNodeAssetOperation.Success) return false;
        return save != null ? await save() : await SaveData().ConfigureAwait(false);
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
