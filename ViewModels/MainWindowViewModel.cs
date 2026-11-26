using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Services.BigMode;
using Retromind.Services.Achievements;
using Retromind.Services.RetroAchievements;
using Retromind.Services.Scrapers;
using Retromind.Services.SteamAchievements;
using Retromind.Services.Stores.Gog.Achievements;

namespace Retromind.ViewModels;

/// <summary>
/// Main ViewModel acting as the orchestrator for the application.
/// This part contains State, Constructor and Lifecycle logic.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly TimeSpan GogShutdownGracePeriod = TimeSpan.FromSeconds(5);

    // --- Services ---
    private readonly AudioService _audioService;
    private readonly MediaDataService _dataService;
    private readonly MetadataBackupService _metadataBackupService;
    private readonly DesktopSettingsWorkflow _settingsWorkflow;
    private readonly IErrorService _errorService;
    private readonly FileManagementService _fileService;
    private readonly WinetricksService _winetricksService;
    private readonly ImportService _importService;
    private readonly LauncherService _launcherService;
    private readonly LaunchLogService _launchLogService;
    private readonly StoreImportService _storeService;
    private readonly DesktopGogWorkflow _gogWorkflow;
    private readonly DesktopEpicWorkflow _epicWorkflow;
    private readonly DesktopDownloadWorkflow _downloads;
    private readonly SettingsService _settingsService;
    private readonly MetadataService _metadataService;
    private readonly ScrapedAssetImporter _scrapedAssetImporter;
    private readonly GamepadService _gamepadService;
    private readonly SoundEffectService _soundEffectService;
    private readonly IDocumentService _documentService;
    private readonly LibraryChangeTracker _libraryTracker;
    private readonly RetroAchievementsAccountService _retroAchievementsAccountService;
    private readonly IStoreAchievementImageCacheService _storeAchievementImageCacheService;
    private readonly IRetroAchievementsGameIdentificationService _retroAchievementsGameIdentificationService;
    private readonly RetroAchievementsBulkIdentificationService _retroAchievementsBulkIdentificationService;
    private readonly IRetroAchievementsProgressService _retroAchievementsProgressService;
    private readonly IRetroAchievementsBadgeService _retroAchievementsBadgeService;
    private readonly ISteamAchievementsProgressService _steamAchievementsProgressService;
    private readonly IGogAchievementsProgressService _gogAchievementsProgressService;

    private readonly DesktopContentSession _desktopContent;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLibraryLoadingHint))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLibraryHint))]
    [NotifyPropertyChangedFor(nameof(ShowGettingStarted))]
    [NotifyPropertyChangedFor(nameof(ShowLibraryLoadError))]
    private bool _isLibraryLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLibraryHint))]
    [NotifyPropertyChangedFor(nameof(ShowGettingStarted))]
    [NotifyPropertyChangedFor(nameof(ShowLibraryLoadError))]
    private string? _libraryLoadErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyLibraryHint))]
    [NotifyPropertyChangedFor(nameof(ShowGettingStarted))]
    private bool _isGettingStartedDismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSettingsLoadError))]
    private string? _settingsLoadErrorMessage;

    public object? FullScreenContent => _bigModeSession.Content;
    
    // Prevents overlapping launches (double-click + Start button + Play Random).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunchIdle))]
    private bool _isLaunchInProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaySelectedMediaButtonText))]
    private bool _isDelegatedLaunchPending;
    
    /// <summary>
    /// True if no launch is currently running. Can be used to enable/disable UI elements.
    /// </summary>
    public bool IsLaunchIdle => !IsLaunchInProgress;
    
    private int _currentShownGameCount;
    private int _totalLibraryGameCount;
    
    public IStorageProvider? StorageProvider { get; set; }
    public RetroAchievementsProgressViewModel RetroAchievementsProgress { get; }
    public RetroAchievementsProgressViewModel AchievementProgress => RetroAchievementsProgress;
    
    // Command to open per-item manuals/documents with the system viewer.
    public IRelayCommand<MediaAsset?> OpenManualCommand { get; private set; } = null!;

    private DateTime _lastControllerExitHandledUtc = DateTime.MinValue;

    private readonly DesktopPersistenceCoordinator _persistence;
    private readonly DesktopMetadataBackupCoordinator _metadataBackups;
    private bool _libraryLoadFailed;
    private CancellationTokenSource? _startupWarmupCts;
    private Task? _startupWarmupTask;

    private readonly TaskCompletionSource<bool> _loadDataTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _pendingBigModeEntry;
    private readonly DesktopBigModeSession _bigModeSession;

    // ensure Cleanup() is executed at most once (Exit + Closing can both fire)
    private int _cleanupOnce;

    public bool ShouldIgnoreBackKeyTemporarily()
        => (DateTime.UtcNow - _lastControllerExitHandledUtc) < TimeSpan.FromMilliseconds(600);
    
    // Helper to access the current window for dialogs
    private Window? CurrentWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public ObservableCollection<MediaNode> RootItems
    {
        get => _rootItems;
        set
        {
            if (!SetProperty(ref _rootItems, value))
                return;

            RefreshTreeVisibility();
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
            OnPropertyChanged(nameof(ShowGettingStarted));
            UpdateLibraryGameCounters();
        }
    }

    public MediaNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetProperty(ref _selectedNode, value))
            {
                UpdateContent();
                NotifyNodeCommandsCanExecuteChanged();
                NotifyPlayAvailabilityChanged();

                OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
                OnPropertyChanged(nameof(ResolvedDisplayNode));
            
                // Persist selection
                if (value != null)
                {
                    _currentSettings.LastSelectedNodeId = value.Id;
                    SaveSettingsOnly();
                    
                    // Mirror state into BigMode (CoreApp -> BigMode).
                    UpdateBigModeStateFromCoreSelection(value, null);
                }
            }
        }
    }

    public object? SelectedNodeContent
    {
        get => _selectedNodeContent;
        set
        {
            if (!SetProperty(ref _selectedNodeContent, value))
                return;

            var selectedItem = value switch
            {
                MediaAreaViewModel mediaArea => mediaArea.SelectedMediaItem,
                SearchAreaViewModel searchArea => searchArea.SelectedMediaItem,
                _ => null
            };
            _ = RetroAchievementsProgress.SelectItemAsync(selectedItem);
            OnPropertyChanged(nameof(ShowLibraryLoadingHint));
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
            OnPropertyChanged(nameof(ShowGettingStarted));
            UpdateLibraryGameCounters();
            NotifyPlayAvailabilityChanged();
            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            OnPropertyChanged(nameof(ResolvedDisplayNode));
        }
    }

    public bool ShowLibraryLoadingHint => IsLibraryLoading && SelectedNodeContent is null && !ShowMediaViewLoadError;
    public bool ShowLibraryLoadError =>
        !IsLibraryLoading && !string.IsNullOrWhiteSpace(LibraryLoadErrorMessage);
    public bool ShowMediaViewLoadError => _desktopContent.HasMediaLoadError;
    public string MediaViewLoadErrorMessage => T(
        "Desktop.MediaViewLoadError",
        "The media view for this category could not be loaded.");
    public string RetryMediaViewText => T("Desktop.MediaViewRetry", "Retry");
    public bool ShowSettingsLoadError => !string.IsNullOrWhiteSpace(SettingsLoadErrorMessage);
    public bool ShowStoreBadges => _currentSettings.ShowStoreBadges;

    // Empty-library hint should only be shown for truly empty libraries,
    // not while startup loading is still building the first content.
    private bool IsEmptyLibraryState =>
        !IsLibraryLoading &&
        !ShowLibraryLoadError &&
        SelectedNodeContent is null &&
        RootItems.Count == 0;

    public bool ShowGettingStarted =>
        !IsLibraryLoading &&
        !ShowLibraryLoadError &&
        IsEmptyLibraryState &&
        !IsGettingStartedDismissed;

    public bool ShowEmptyLibraryHint => IsEmptyLibraryState && !ShowGettingStarted;

    public string GettingStartedTitle => T("GettingStarted.Title", "Welcome to Retromind");
    public string GettingStartedIntro => T(
        "GettingStarted.Intro",
        "Retromind organizes games and other media in categories. Start with the structure that fits your library.");
    public string GettingStartedCategoryTitle => T("GettingStarted.Category.Title", "Create your first category");
    public string GettingStartedCategoryDescription => T(
        "GettingStarted.Category.Description",
        "Create a category such as Games, Movies or Books. You can add more levels at any time.");
    public string GettingStartedCategoryAction => T("GettingStarted.Category.Action", "Create category...");
    public string GettingStartedSettingsTitle => T("GettingStarted.Settings.Title", "Configure emulators and services");
    public string GettingStartedSettingsDescription => T(
        "GettingStarted.Settings.Description",
        "Set up emulators, metadata providers, library paths and optional RetroAchievements access.");
    public string GettingStartedSettingsAction => T("GettingStarted.Settings.Action", "Open settings...");
    public string GettingStartedRestoreTitle => T("GettingStarted.Restore.Title", "Restore an existing library");
    public string GettingStartedRestoreDescription => T(
        "GettingStarted.Restore.Description",
        "Restore library metadata and settings from a Retromind backup stored in the current data location.");
    public string GettingStartedRestoreAction => T("GettingStarted.Restore.Action", "Manage backups...");
    public string GettingStartedNextStep => T(
        "GettingStarted.NextStep",
        "After creating a category, right-click it to import ROMs, Steam or Epic games, add GOG games, or create media manually.");
    public string GettingStartedHelpLocation => T(
        "GettingStarted.Help.Location",
        "You can find more guides under Settings → Help.");
    public string GettingStartedHelpText => T("Help.Workflows.Open", "Open workflow guides...");
    public string GettingStartedCloseText => T("GettingStarted.Close", "Close overview");
    public bool ShowLibraryGameCountSummary => TotalLibraryGameCount > 0;
    public int CurrentShownGameCount => _currentShownGameCount;
    public int TotalLibraryGameCount => _totalLibraryGameCount;
    public string LibraryGameCountSummary => 
        string.Format(Strings.Common_LibraryGameCountSummary, 
            CurrentShownGameCount.ToString("N0", CultureInfo.CurrentCulture), 
            TotalLibraryGameCount.ToString("N0", CultureInfo.CurrentCulture));
    
    public string? ResolvedSelectedItemLogoPath => ResolveSelectedItemAsset(AssetType.Logo);
    public string? ResolvedSelectedItemWallpaperPath => ResolveSelectedItemAsset(AssetType.Wallpaper);
    public string? ResolvedSelectedItemVideoPath => ResolveSelectedItemAsset(AssetType.Video);
    public string? ResolvedSelectedItemMarqueePath => ResolveSelectedItemAsset(AssetType.Marquee);

    /// <summary>
    /// The node whose name/logo should be displayed in the header.
    /// When a media item is selected, this resolves to the item's true parent node
    /// (the leaf node that directly contains it). Falls back to SelectedNode otherwise.
    /// </summary>
    public MediaNode? ResolvedDisplayNode
    {
        get
        {
            var item = GetCurrentSelectedItem();
            if (item != null)
            {
                return FindParentNode(RootItems, item) ?? SelectedNode;
            }
            return SelectedNode;
        }
    }
    
    // --- Layout Properties ---
    public GridLength TreePaneWidth
    {
        get => _treePaneWidth;
        set
        {
            if (SetProperty(ref _treePaneWidth, value))
            {
                _currentSettings.TreeColumnWidth = value.Value;
                SaveSettingsOnly();
            }
        }
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

    public IBrush PanelBackground => IsDarkTheme
        ? new SolidColorBrush(Color.Parse("#CC252526"))
        : new SolidColorBrush(Color.Parse("#CCC8C8C8"));
    public IBrush TextColor => IsDarkTheme ? Brushes.White : new SolidColorBrush(Color.Parse("#1A1A1A"));
    public IBrush WindowBackground => IsDarkTheme
        ? new SolidColorBrush(Color.Parse("#252526"))
        : new SolidColorBrush(Color.Parse("#D6D6D6"));

    private readonly Action _onControllerExitRequested;
    private readonly Action _onSessionExitRequested;
    
    // --- Constructor (Dependency Injection) ---
    public MainWindowViewModel(
        AudioService audioService,
        MediaDataService dataService,
        MetadataBackupService metadataBackupService,
        FileManagementService fileService,
        WinetricksService winetricksService,
        ImportService importService,
        LauncherService launcherService,
        LaunchLogService launchLogService,
        StoreImportService storeService,
        DesktopStoreWorkflowFactory storeWorkflowFactory,
        SettingsService settingsService,
        DesktopSettingsWorkflow settingsWorkflow,
        MetadataService metadataService,
        ScrapedAssetImporter scrapedAssetImporter,
        SoundEffectService soundEffectService,
        AppSettings preloadedSettings,
        IDocumentService documentService,
        RetroAchievementsAccountService retroAchievementsAccountService,
        IStoreAchievementImageCacheService storeAchievementImageCacheService,
        IRetroAchievementsGameIdentificationService retroAchievementsGameIdentificationService,
        RetroAchievementsBulkIdentificationService retroAchievementsBulkIdentificationService,
        IRetroAchievementsProgressService retroAchievementsProgressService,
        IRetroAchievementsBadgeService retroAchievementsBadgeService,
        ISteamAchievementsProgressService steamAchievementsProgressService,
        IGogAchievementsProgressService gogAchievementsProgressService,
        IErrorService errorService)
    {
        _audioService = audioService;
        _dataService = dataService;
        _metadataBackupService = metadataBackupService;
        _settingsWorkflow = settingsWorkflow;
        _errorService = errorService;
        _desktopContent = new DesktopContentSession(_errorService);
        _fileService = fileService;
        _winetricksService = winetricksService;
        _importService = importService;
        _launcherService = launcherService;
        _launchLogService = launchLogService;
        _storeService = storeService;
        _settingsService = settingsService;
        _metadataService = metadataService;
        _scrapedAssetImporter = scrapedAssetImporter;
        _soundEffectService = soundEffectService;
        _currentSettings = preloadedSettings;
        _documentService = documentService;
        _retroAchievementsAccountService = retroAchievementsAccountService;
        _storeAchievementImageCacheService = storeAchievementImageCacheService;
        _retroAchievementsGameIdentificationService = retroAchievementsGameIdentificationService;
        _retroAchievementsBulkIdentificationService = retroAchievementsBulkIdentificationService;
        _retroAchievementsProgressService = retroAchievementsProgressService;
        _retroAchievementsBadgeService = retroAchievementsBadgeService;
        _steamAchievementsProgressService = steamAchievementsProgressService;
        _gogAchievementsProgressService = gogAchievementsProgressService;
        RetroAchievementsProgress = new RetroAchievementsProgressViewModel(
            _currentSettings,
            _retroAchievementsProgressService,
            _retroAchievementsBadgeService,
            _steamAchievementsProgressService,
            _gogAchievementsProgressService,
            _storeAchievementImageCacheService,
            selectionDelay: TimeSpan.FromMilliseconds(300));
        if (_settingsService.HasLoadFailure)
        {
            SettingsLoadErrorMessage = T(
                "Persistence.SettingsLoadFailed",
                "Retromind could not load the application settings. The primary file and its backup are unreadable or invalid. Existing settings files will not be overwritten, and settings changes made during this session will not be saved. Close Retromind and check app_settings.json, its backup, any .corrupt_* file, and file permissions.");
        }

        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(_currentSettings.IgnoreLeadingArticlesInSort);
        _audioService.MusicPlaybackEnded += OnMusicPlaybackEnded;
        _libraryTracker = new LibraryChangeTracker(
            _dataService,
            OnTrackedItemPropertyChanged,
            onStructureChanged: () =>
            {
                RefreshTreeVisibility();
                OnPropertyChanged(nameof(ShowEmptyLibraryHint));
                OnPropertyChanged(nameof(ShowGettingStarted));
            },
            onItemProtectionChanged: item =>
            {
                if (_isApplyingProtectionChanges) return;
                ScheduleParentalProtectionRefresh();
            },
            onBeforeSaveMigration: roots =>
            {
                if (!_currentSettings.PreferPortableLaunchPaths) return 0;
                return LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(roots);
            }
        );
        _libraryTracker.LibraryDirtyStateChanged += () =>
        {
            UpdateLibraryGameCounters();
        };
        _persistence = new DesktopPersistenceCoordinator(
            _libraryTracker,
            _settingsService,
            () => _libraryLoadFailed,
            () => UiThreadHelper.InvokeAsync(() => _settingsService.Serialize(_currentSettings)),
            _errorService,
            TryShowPersistenceSaveFailureAsync);
        _metadataBackups = new DesktopMetadataBackupCoordinator(
            _metadataBackupService,
            _dataService,
            _settingsService,
            _libraryTracker,
            _currentSettings,
            () => RootItems,
            () => _libraryLoadFailed,
            _persistence.CancelPendingSettingsSave);
        _fileService.LibraryChanged += _libraryTracker.MarkDirty;
        (_gogWorkflow, _epicWorkflow, _downloads) = storeWorkflowFactory.Create(new DesktopStoreWorkspace(
            _currentSettings,
            GetWindow: () => CurrentWindow,
            GetStorageProvider: () => StorageProvider,
            GetRootItems: () => RootItems,
            GetMultiSelectionActive: () => IsMultiSelectModeActive,
            GetOperationInProgress: () => IsLaunchInProgress,
            SetOperationInProgress: inProgress => IsLaunchInProgress = inProgress,
            FindParentNode: item => FindParentNode(RootItems, item),
            FindInheritedEmulator,
            SaveLibraryAsync: SaveData,
            MarkLibraryDirty: _libraryTracker.MarkDirty,
            MarkLibraryDirtyAndSaveSoon: _libraryTracker.MarkDirtyAndSaveSoon,
            RefreshActions: NotifyPlayAvailabilityChanged,
            RefreshItemAsync: RefreshItemPresentationAsync,
            PublishImportAsync: PublishStoreImportAsync));
        _downloads.QueueChanged += OnDownloadQueueChanged;
        
        // Seed layout values early so bindings are stable before LoadData completes.
        _treePaneWidth = new GridLength(_currentSettings.TreeColumnWidth);
        _detailPaneWidth = new GridLength(_currentSettings.DetailColumnWidth);
        _itemWidth = _currentSettings.ItemWidth;
        
        SyncSavedSearchTermsCollectionFromSettings();
        
        // Ensure SDL also reports background events and applies deadzone handling.
        Environment.SetEnvironmentVariable("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        Environment.SetEnvironmentVariable("SDL_LINUX_JOYSTICK_DEADZONES", "1");

        // Gamepad service (hot-plug + configurable action routing)
        _gamepadService = new GamepadService(_currentSettings, errorService: _errorService);
        _bigModeSession = new DesktopBigModeSession(
            _currentSettings, () => CurrentWindow, item => PlayMediaAsync(item), SyncSelectionFromBigModeAsync);
        _bigModeSession.ContentChanged += () => OnPropertyChanged(nameof(FullScreenContent));
        _gamepadService.StartMonitoring();
    
        // Route the configured exit action globally: only act if BigMode is actually active.
        _onControllerExitRequested = () =>
        {
            // Capture this session and its input generation before entering the UI queue.
            _bigModeSession.ActiveViewModel?.DispatchControllerExit(
                () => _lastControllerExitHandledUtc = DateTime.UtcNow);
        };
        _gamepadService.OnExitBigMode += _onControllerExitRequested;

        _onSessionExitRequested = () => _ = HandleSessionExitRequestAsync();
        _gamepadService.OnSessionExitRequested += _onSessionExitRequested;
        
        InitializeCommands();
        _desktopContent.ContentChanged += content => SelectedNodeContent = content;
        _desktopContent.MediaLoadErrorChanged += () =>
        {
            OnPropertyChanged(nameof(ShowMediaViewLoadError));
            OnPropertyChanged(nameof(ShowLibraryLoadingHint));
            RetryMediaViewCommand.NotifyCanExecuteChanged();
        };
        _desktopContent.PlayRequested += OnDesktopPlayRequested;
        _desktopContent.MediaPropertyChanged += OnMediaAreaPropertyChanged;
        _desktopContent.SearchPropertyChanged += OnSearchAreaPropertyChanged;
        _desktopContent.ItemsChanged += UpdateLibraryGameCounters;
        Debug.WriteLine("[DEBUG] Konstruktor finished. BigModeOnly = " + (App.Current?.IsBigModeOnly == true));
    }

    public void SetGamepadUiInputEnabled(bool enabled)
    {
        _gamepadService.SetUiInputEnabled(enabled);
        if (!enabled)
            _bigModeSession.ActiveViewModel?.NotifyNavigationInputLost();
    }

    private async Task HandleSessionExitRequestAsync()
    {
        try
        {
            var result = await _launcherService.RequestActiveSessionStopAsync().ConfigureAwait(false);
            Debug.WriteLine($"[Launcher] Controller session-stop request: {result}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Launcher] Controller session-stop request failed: {ex.Message}");
        }
    }

    // --- Persistence & Lifecycle ---

    public async Task LoadData()
    {
        IsLibraryLoading = true;

        try 
        {
            RootItems = await _dataService.LoadAsync();
            Debug.WriteLine("[DEBUG] LoadData: RootItems loaded. Count = " + RootItems.Count);
            OnPropertyChanged(nameof(ShowEmptyLibraryHint));
            OnPropertyChanged(nameof(ShowGettingStarted));
        
            _libraryTracker.Initialize(RootItems);
            InitializeParentalStateAfterLoad();
            
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(PanelBackground));
            OnPropertyChanged(nameof(TextColor));
            OnPropertyChanged(nameof(WindowBackground));
    
            if (Application.Current != null)
                Application.Current.RequestedThemeVariant = _currentSettings.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

            TreePaneWidth = new GridLength(_currentSettings.TreeColumnWidth);
            DetailPaneWidth = new GridLength(_currentSettings.DetailColumnWidth);
            ItemWidth = _currentSettings.ItemWidth;

            // Clean up persisted selection state if it points to deleted/moved nodes or items.
            // This prevents invalid IDs from crashing the UI on startup.
            var settingsChanged = SanitizeSelectionState();
            if (settingsChanged)
                SaveSettingsOnly();

            if (!string.IsNullOrEmpty(_currentSettings.LastSelectedNodeId))
            {
                var node = FindNodeById(RootItems, _currentSettings.LastSelectedNodeId);
                if (node != null && node.IsVisibleInTree)
                {
                    SelectedNode = node;
                    ExpandPathToNode(RootItems, node);
                }
                else
                {
                    var firstVisible = FindFirstVisibleNode();
                    if (firstVisible != null)
                        ExpandPathToNode(RootItems, firstVisible);
                    SelectedNode = firstVisible;
                }
            }
            else
            {
                var firstVisible = FindFirstVisibleNode();
                if (firstVisible != null)
                    ExpandPathToNode(RootItems, firstVisible);
                SelectedNode = firstVisible;
            }

            // Give the first media view time to load before starting background maintenance.
            await AwaitCurrentContentUpdateAsync();

            _downloadsReady = !_settingsService.HasLoadFailure && Volatile.Read(ref _cleanupOnce) == 0;
            if (_downloadsReady && !_closingDownloads)
                _downloads.Start();

            // Heavy startup maintenance runs in the background after first content is visible.
            _startupWarmupCts = new CancellationTokenSource();
            _startupWarmupTask = RunDeferredStartupWarmupAsync(_startupWarmupCts.Token);
        }
        catch (LibraryLoadException ex)
        {
            _libraryLoadFailed = true;
            LibraryLoadErrorMessage = T(
                "Persistence.LoadFailed",
                "Retromind could not load the media library. The primary file and its backup are unreadable or invalid. Existing library files will not be overwritten. Close Retromind and check retromind_tree.json, its backup, and file permissions.");
            NotifyNodeCommandsCanExecuteChanged();
            Debug.WriteLine($"[ViewModel] Library load failed: {ex}");
        }
        catch (Exception ex)
        {
            _libraryLoadFailed = true;
            LibraryLoadErrorMessage = T(
                "Persistence.InitializationFailed",
                "Retromind could not safely initialize the media library. Existing library files will not be overwritten during this session. Close Retromind and check the application log and library files.");
            NotifyNodeCommandsCanExecuteChanged();
            Debug.WriteLine($"[ViewModel] LoadData Error: {ex}");
        }
        finally
        {
            IsLibraryLoading = false;
            _loadDataTcs.TrySetResult(true);
        }
    }

    private async Task RunDeferredStartupWarmupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RescanAllAssetsAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await UiThreadHelper.InvokeAsync(
                    () => SortAllNodesRecursive(RootItems, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the application closes before startup maintenance finishes.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupWarmup] Failed: {ex.Message}");
        }
    }

    private async Task StopStartupWarmupAsync()
    {
        var cancellation = _startupWarmupCts;
        var warmupTask = _startupWarmupTask;
        _startupWarmupCts = null;
        _startupWarmupTask = null;

        if (cancellation == null && warmupTask == null)
            return;

        try
        {
            cancellation?.Cancel();
            if (warmupTask != null)
                await warmupTask.ConfigureAwait(false);
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private bool SanitizeSelectionState()
    {
        bool changed = false;

        MediaNode? selectedNode = null;
        if (_currentSettings.LastSelectedNodeId is { Length: > 0 } nodeId)
        {
            selectedNode = FindNodeById(RootItems, nodeId);
            if (selectedNode is null)
            {
                _currentSettings.LastSelectedNodeId = null;
                changed = true;
            }
        }

        if (selectedNode is null)
        {
            if (_currentSettings.LastSelectedMediaId is { Length: > 0 })
            {
                _currentSettings.LastSelectedMediaId = null;
                changed = true;
            }
        }
        else if (_currentSettings.LastSelectedMediaId is { Length: > 0 } mediaId)
        {
            if (!IsMediaIdInNodeSubtree(selectedNode, mediaId))
            {
                _currentSettings.LastSelectedMediaId = null;
                changed = true;
            }
        }

        // The canonical bookmark supports both entry kinds at every level.
        // Normalize with the same rules used when opening BigMode, including old settings.
        var oldBookmark = System.Text.Json.JsonSerializer.Serialize(_currentSettings.BigModeNavigation);
        var desktopNode = _currentSettings.LastSelectedNodeId;
        var desktopItem = _currentSettings.LastSelectedMediaId;
        var session = new BigModeNavigationSession(RootItems, IsParentalFilterActive);
        BigModeNavigationPersistence.Restore(session, _currentSettings);
        BigModeNavigationPersistence.Save(session, _currentSettings);
        _currentSettings.LastSelectedNodeId = desktopNode;
        _currentSettings.LastSelectedMediaId = desktopItem;
        changed |= oldBookmark != System.Text.Json.JsonSerializer.Serialize(_currentSettings.BigModeNavigation);

        return changed;
    }

    /// <summary>
    /// Called by LibraryChangeTracker when item properties change.
    /// Keeps active presentation and filtering state in sync.
    /// </summary>
    private void OnTrackedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var item = sender as MediaItem;
        if (item == null) return;
    
        if (string.IsNullOrWhiteSpace(e.PropertyName))
            return;

        if (SearchQueryMatcher.IsLiveItemFilterInput(e.PropertyName))
        {
            if (UiThreadHelper.CheckAccess())
                _desktopContent.SearchArea?.RefreshAfterItemChange(e.PropertyName);
            else
                UiThreadHelper.Post(() => _desktopContent.SearchArea?.RefreshAfterItemChange(e.PropertyName));
        }

        if (e.PropertyName == nameof(MediaItem.RetroAchievementsGame) &&
            ReferenceEquals(item, GetCurrentSelectedItem()))
        {
            if (UiThreadHelper.CheckAccess())
                _ = RetroAchievementsProgress.SelectItemAsync(item);
            else
                UiThreadHelper.Post(() => _ = RetroAchievementsProgress.SelectItemAsync(item));
        }
    
        var isAssetProperty = e.PropertyName switch
        {
            nameof(MediaItem.PrimaryWallpaperPath) => true,
            nameof(MediaItem.PrimaryScreenshotPath) => true,
            nameof(MediaItem.PrimaryLogoPath) => true,
            nameof(MediaItem.PrimaryVideoPath) => true,
            nameof(MediaItem.PrimaryMarqueePath) => true,
            _ => false
        };
    
        if (!isAssetProperty)
            return;
    
        var selected = GetCurrentSelectedItem();
        if (!ReferenceEquals(item, selected))
            return;
    
        if (UiThreadHelper.CheckAccess())
        {
            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
        }
        else
        {
            UiThreadHelper.Post(() =>
            {
                OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
                OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            });
        }
    }
    
    private bool IsMediaIdInNodeSubtree(MediaNode node, string mediaId)
    {
        if (node.Items.Any(i => i.Id == mediaId))
            return true;

        foreach (var child in node.Children)
        {
            if (IsMediaIdInNodeSubtree(child, mediaId))
                return true;
        }

        return false;
    }

    // Helper to make UpdateContent awaitable (wraps the background task in an awaitable Task).
    private async Task UpdateContentAsync()
    {
        UpdateContent();  // Start the existing background task.

        await AwaitCurrentContentUpdateAsync().ConfigureAwait(false);
    }

    private Task<bool> AwaitCurrentContentUpdateAsync() => _desktopContent.AwaitMediaLoadAsync();

    /// <summary>
    /// Confirms a clean tracked library and a current settings write. Protected/skipped stores,
    /// write failures, and newer pending edits return false without reverting applied changes.
    /// </summary>
    public async Task<bool> SaveData()
        => (await SaveDataWithStatusAsync().ConfigureAwait(false)).AllSaved;

    private static string OperationSavePendingMessage() => DesktopPersistenceCoordinator.OperationSavePendingMessage();

    private Task<(bool AllSaved, bool SettingsSaved)> SaveDataWithStatusAsync()
        => _persistence.SaveAsync();

    private async Task<bool> SaveDataAndReleaseAssetsAsync(IReadOnlyList<AssetRemoval>? originals, Action? cleanupFolders = null)
    {
        if (originals is not { Count: > 0 } && cleanupFolders == null)
            return await SaveData().ConfigureAwait(false);

        var saved = await _persistence.SaveAsync(() =>
        {
            if (originals is { Count: > 0 })
                _fileService.RemoveUnreferencedAssets(originals, RootItems);
            cleanupFolders?.Invoke();
        }).ConfigureAwait(false);
        return saved.AllSaved;
    }

    private void CleanRelocatedCategoryFolders(NodeAssetOperationResult move)
    {
        foreach (var path in move.OriginalFolders)
            NodeAssetFolderHelper.DeleteDirectoryIfEmpty(path);
    }

    private void SaveSettingsOnly() => _ = _persistence.ScheduleSettingsSave();

    private async Task<bool> TryShowPersistenceSaveFailureAsync(string message)
    {
        if (CurrentWindow is not { } owner)
            return false;

        await ShowInfoDialog(owner, message);
        return true;
    }

    private string? ResolveSelectedItemAsset(AssetType type)
    {
        var item = GetCurrentSelectedItem();
        if (item == null)
            return null;

        var node = FindParentNode(RootItems, item) ?? SelectedNode;
        return AssetResolver.ResolveAssetPath(item, node, type);
    }

    private MediaItem? GetCurrentSelectedItem()
    {
        var item = _desktopContent.MediaArea?.SelectedMediaItem;
        if (item != null)
            return item;

        if (SelectedNodeContent is SearchAreaViewModel searchVm)
            return searchVm.SelectedMediaItem;

        return null;
    }

    /// <summary>
    /// Clears the currently selected media item in the active center view (node or global search).
    /// Returns true if a selection was cleared.
    /// </summary>
    public bool TryClearActiveSelection() => _desktopContent.TryClearSelection();

    /// <summary>
    /// Flushes pending saves and performs cleanup only after a successful write.
    /// Must be awaited from the UI during window closing to avoid deadlocks.
    /// </summary>
    public async Task<bool> FlushAndCleanupAsync()
    {
        _closingDownloads = true;
        if (_downloadsReady)
            await _downloads.StopAsync();

        // Stop playback immediately; saving can take a moment.
        _audioService.StopMusic();

        // Startup maintenance mutates asset collections and item order. It must
        // finish or observe cancellation before the final library snapshot is taken.
        await StopStartupWarmupAsync().ConfigureAwait(false);
        await _launcherService
            .PrepareForShutdownAsync(GogShutdownGracePeriod)
            .ConfigureAwait(false);

        if (_metadataBackups.RequiresRestart)
        {
            await UiThreadHelper.InvokeAsync(Cleanup).ConfigureAwait(false);
            return true;
        }

        // Cancel pending debounces so we don't race with our final flush.
        _persistence.CancelPendingSettingsSave();
        _parentalRefreshCts?.Cancel();

        try
        {
            await _persistence.FlushAsync(() => UiThreadHelper.InvokeAsync(Cleanup)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Shutdown] Final save failed: {ex.Message}");
            _launcherService.ResumeAfterCancelledShutdown();
            return false;
        }
    }
    
    /// <summary>
    /// Fast cleanup. No IO here (no saving).
    /// Safe to call from Exit handlers too.
    /// </summary>
    public void Cleanup()
    {
        // Guard against double-cleanup (can happen when Closing triggers Close(), then desktop.Exit runs)
        if (Interlocked.Exchange(ref _cleanupOnce, 1) == 1)
            return;
        
        _audioService.StopMusic();
        _audioService.MusicPlaybackEnded -= OnMusicPlaybackEnded;
        
        _fileService.LibraryChanged -= _libraryTracker.MarkDirty;
        _persistence.Dispose();
        _libraryTracker.StopTracking();
        
        _desktopContent.Dispose();
        
        _parentalRefreshCts?.Cancel();
        _parentalRefreshCts?.Dispose();
        _parentalRefreshCts = null;

        // Exit handlers can call Cleanup without going through the awaited flush.
        // Signal cancellation here as a best-effort fallback; the normal closing
        // path has already awaited this task in StopStartupWarmupAsync().
        _startupWarmupCts?.Cancel();

        _launcherService.CancelPendingGogCometCompletion();

        RetroAchievementsProgress.Dispose();

        _downloads.QueueChanged -= OnDownloadQueueChanged;
        _downloads.Release();
        
        _gamepadService.OnExitBigMode -= _onControllerExitRequested;
        _gamepadService.OnSessionExitRequested -= _onSessionExitRequested;
        _gamepadService.StopMonitoring();
    }

    private void UpdateLibraryGameCounters()
    {
        var shownCount = _desktopContent.MediaArea?.FilteredItems.Count
                         ?? _desktopContent.SearchArea?.SearchResults.Count
                         ?? 0;
        var totalCount = CountAllGames(RootItems);

        if (_currentShownGameCount != shownCount)
        {
            _currentShownGameCount = shownCount;
            OnPropertyChanged(nameof(CurrentShownGameCount));
            OnPropertyChanged(nameof(LibraryGameCountSummary));
        }

        if (_totalLibraryGameCount != totalCount)
        {
            _totalLibraryGameCount = totalCount;
            OnPropertyChanged(nameof(TotalLibraryGameCount));
            OnPropertyChanged(nameof(LibraryGameCountSummary));
            OnPropertyChanged(nameof(ShowLibraryGameCountSummary));
        }
    }

    private static int CountAllGames(IEnumerable<MediaNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            total += node.Items.Count;
            if (node.Children.Count > 0)
                total += CountAllGames(node.Children);
        }

        return total;
    }

    private void OnDesktopPlayRequested(MediaItem item)
    {
        _ = PlayMediaAsync(item);
    }

    private void OnMusicPlaybackEnded(string filePath)
    {
        // Process exit happens on a background thread. Marshal to UI thread before touching VM state.
        UiThreadHelper.Post(() => _ = RestartSelectionMusicAsync(filePath));
    }

    private async Task RestartSelectionMusicAsync(string endedFilePath)
    {
        if (!_currentSettings.EnableSelectionMusicPreview)
            return;

        var item = GetCurrentSelectedItem();
        var contextNode = GetSelectionMusicContextNode(item);
        if (item == null || contextNode == null)
            return;

        // Ignore a delayed exit notification if the user selected another item
        // before it reached the UI thread.
        var activeMusicPath = item.GetPrimaryAssetPath(AssetType.Music);
        var activeFullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(activeMusicPath);
        if (string.IsNullOrWhiteSpace(activeFullPath) ||
            !FileSystemPathIdentity.Equals(activeFullPath, endedFilePath))
        {
            return;
        }

        await PlaySelectionMusicAsync(item, contextNode);
    }

    private MediaNode? GetSelectionMusicContextNode(MediaItem? item)
    {
        if (item == null)
            return null;

        if (_desktopContent.MediaArea?.SelectedMediaItem == item)
            return _desktopContent.MediaArea.Node;

        if (_desktopContent.SearchArea?.SelectedMediaItem == item)
            return FindParentNode(RootItems, item);

        return null;
    }

    private async Task PlaySelectionMusicAsync(MediaItem? item, MediaNode? contextNode)
    {
        if (FullScreenContent != null ||
            !_currentSettings.EnableSelectionMusicPreview ||
            item == null ||
            contextNode == null)
        {
            _audioService.StopMusic();
            return;
        }

        var musicPath = ResolveSelectionMusicPath(contextNode, item);
        var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(musicPath);
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            _audioService.StopMusic();
            return;
        }

        await _audioService.PlayMusicAsync(fullPath);
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

        if (args.PropertyName == nameof(MediaAreaViewModel.SearchText))
        {
            ApplySavedSearchFavoriteState(mediaVm.SearchText, value => mediaVm.OnlyFavorites = value);
            return;
        }

        if (args.PropertyName == nameof(MediaAreaViewModel.IsMultiSelectMode))
        {
            OnPropertyChanged(nameof(AreSingleItemActionsEnabled));
            NotifyPlayAvailabilityChanged();
            return;
        }

        if (args.PropertyName == nameof(MediaAreaViewModel.SelectedMediaItem))
        {
            var item = mediaVm.SelectedMediaItem;
            _currentSettings.LastSelectedMediaId = item?.Id;
            SaveSettingsOnly();
            NotifyPlayAvailabilityChanged();

            if (item != null && _gogWorkflow.CanCheckGogUpdatesForItem(item))
                _ = _gogWorkflow.CheckGogUpdatesForItemCoreAsync(item, force: false, CancellationToken.None);

            if (item != null)
                _desktopContent.RememberNodeSelection(mediaVm.Node.Id, item.Id);
            else
                _desktopContent.RememberNodeDeselection(mediaVm.Node.Id);

            if (SelectedNode != null)
                UpdateBigModeStateFromCoreSelection(SelectedNode, item);

            OnPropertyChanged(nameof(ResolvedSelectedItemLogoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemWallpaperPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemVideoPath));
            OnPropertyChanged(nameof(ResolvedSelectedItemMarqueePath));
            OnPropertyChanged(nameof(ResolvedDisplayNode));

            _ = RetroAchievementsProgress.SelectItemAsync(item);
            _ = PlaySelectionMusicAsync(item, mediaVm.Node);
        }
    }

    private string? ResolveSelectionMusicPath(MediaNode contextNode, MediaItem item)
    {
        if (!DesktopMediaProjectionBuilder.IsMusicRandomized(contextNode, RootItems))
        {
            item.ClearActiveAsset(AssetType.Music);
            return item.GetPrimaryAssetPath(AssetType.Music);
        }

        var musicAssets = item.Assets
            .Where(a => a.Type == AssetType.Music && !string.IsNullOrWhiteSpace(a.RelativePath))
            .Select(a => a.RelativePath)
            .ToList();

        if (musicAssets.Count == 0)
            return null;

        if (musicAssets.Count == 1)
            return musicAssets[0];

        var current = item.GetPrimaryAssetPath(AssetType.Music);
        var candidates = musicAssets;

        if (!string.IsNullOrWhiteSpace(current))
        {
            candidates = musicAssets
                .Where(path => !FileSystemPathIdentity.Equals(path, current))
                .ToList();
        }

        var picked = RandomHelper.PickRandom(candidates) ?? RandomHelper.PickRandom(musicAssets);
        if (!string.IsNullOrWhiteSpace(picked))
            item.SetActiveAsset(AssetType.Music, picked);

        return picked;
    }

    /// <summary>
    /// Opens the given manual/document asset with the system's default viewer.
    /// Best-effort only: invalid assets or missing files are ignored silently
    /// </summary>
    /// <param name="asset">Manual asset to open (Type must be Manual).</param>
    private void OpenManual(MediaAsset? asset)
    {
        if (asset is not { Type: AssetType.Manual } ||
            string.IsNullOrWhiteSpace(asset.RelativePath))
        {
            return;
        }

        try
        {
            var fullPath = AppPaths.ResolveDataPathInsideRootOrEmpty(asset.RelativePath);
            if (!string.IsNullOrWhiteSpace(fullPath))
                _documentService.OpenDocument(fullPath);
        }
        catch
        {
            // Best-effort: opening manuals must never crash the UI.
        }
    }
    
    /// <summary>
    /// Converts all absolute launch file paths that are located under AppPaths.DataRoot
    /// into LibraryRelative paths, then persists the updated library
    /// This is intended as a one-time operation when switching to a portable setup
    /// </summary>
    private async Task ConvertLaunchPathsToPortableAsync()
    {
        if (RootItems.Count == 0)
            return;

        try
        {
            int migrated = 0;

            await UiThreadHelper.InvokeAsync(() =>
            {
                migrated = LibraryMigrationHelper.MigrateLaunchFilePathsToLibraryRelative(RootItems);
                if (migrated > 0)
                    _libraryTracker.MarkDirty();
            });

            if (migrated <= 0)
            {
                Console.WriteLine("[Migration] No launch file paths needed conversion.");
                return;
            }

            Console.WriteLine($"[Migration] Converted {migrated} launch file paths to LibraryRelative.");
            await _libraryTracker.SaveIfDirtyAsync(force: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Migration] ConvertLaunchPathsToPortableAsync failed: {ex}");
        }
    }
    
    // --- Content Logic (The heart of the UI) ---
    private void RefreshContentAfterMediaCollectionChange()
    {
        if (_desktopContent.SearchArea is { } searchVm)
        {
            searchVm.RefreshResults();
            return;
        }

        UpdateContent();
    }

    private void UpdateContent()
    {
        _audioService.StopMusic();
        _ = _desktopContent.LoadMediaAsync(
            SelectedNode, RootItems, IsParentalFilterActive,
            node => ReferenceEquals(SelectedNode, node) ? (ItemWidth, _currentSettings.LastSelectedMediaId) : null);
    }
}
