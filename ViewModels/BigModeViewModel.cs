using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Retromind.Helpers;
using Retromind.Helpers.Video;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// Root ViewModel for BigMode.
/// Theme authors bind to properties exposed by this type.
/// </summary>
public partial class BigModeViewModel : ViewModelBase, IDisposable
{
    // --- Dependencies (constructor-injected / core) ---
    private readonly LibVLC _libVlc;
    private readonly LibVLC _secondaryLibVlc;
    private readonly AppSettings _settings;
    private Theme _theme;
    private readonly SoundEffectService _soundEffectService;
    private readonly GamepadService _gamepadService;

    // --- Lifecycle / safety ---
    private int _disposed;
    private bool _stateSaved;

    // --- Navigation state ---
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private readonly Stack<ObservableCollection<MediaNode>> _navigationStack = new();
    private readonly Stack<string> _titleStack = new();
    private readonly Stack<MediaNode> _navigationPath = new();

    private bool _isLaunching;

    // --- Video (VLC + surfaces + players) ---
    private readonly LibVlcVideoSurface _videoSurface;
    private readonly LibVlcVideoSurface? _secondaryVideoSurface;
    private MediaPlayer? _secondaryPlayer;
    private Media? _secondaryBackgroundMedia;

    private bool _isViewReady;
    private string? _currentPreviewVideoPath;

    // --- Caches ---
    private readonly Dictionary<string, string?> _itemVideoPathCache = new();
    private readonly Dictionary<string, string?> _nodeVideoPathCache = new();

    // --- Index helpers ---
    [ObservableProperty]
    private int _selectedItemIndex = -1;

    [ObservableProperty]
    private int _selectedCategoryIndex = -1;

    // --- Theme context / selection ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMarqueePath))]
    [NotifyPropertyChangedFor(nameof(ActiveBezelPath))]
    [NotifyPropertyChangedFor(nameof(ActiveControlPanelPath))]
    private MediaNode? _themeContextNode;

    [ObservableProperty]
    private ObservableCollection<MediaNode> _currentCategories;

    [ObservableProperty]
    private MediaNode? _selectedCategory;

    [ObservableProperty]
    private MediaNode? _currentNode;

    [ObservableProperty]
    private ObservableCollection<MediaItem> _items = new();

    private const int CircularLogoWindowSize = 9;
    private readonly ObservableCollection<MediaItem> _circularItems = new();

    public ObservableCollection<MediaItem> CircularItems => _circularItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMarqueePath))]
    [NotifyPropertyChangedFor(nameof(ActiveBezelPath))]
    [NotifyPropertyChangedFor(nameof(ActiveControlPanelPath))]
    private MediaItem? _selectedItem;

    // --- Video UI flags (theme bindings) ---
    [ObservableProperty]
    private bool _isVideoVisible;

    [ObservableProperty]
    private bool _isVideoOverlayVisible;

    [ObservableProperty]
    private bool _canShowVideo = true;

    [ObservableProperty]
    private bool _mainVideoHasContent;

    [ObservableProperty]
    private bool _mainVideoHasFrame;

    [ObservableProperty]
    private bool _mainVideoIsPlaying;

    [ObservableProperty]
    private bool _secondaryVideoHasContent;

    [ObservableProperty]
    private bool _secondaryVideoIsPlaying;

    // --- Misc UI state ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategorySelectionActive))]
    [NotifyPropertyChangedFor(nameof(ActiveMarqueePath))]
    [NotifyPropertyChangedFor(nameof(ActiveBezelPath))]
    [NotifyPropertyChangedFor(nameof(ActiveControlPanelPath))]
    private bool _isGameListActive;

    public bool IsCategorySelectionActive => !IsGameListActive;
    
    [ObservableProperty]
    private string _currentThemeDirectory = string.Empty;

    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    [ObservableProperty]
    private string _categoryTitle = "";

    [ObservableProperty]
    private int _totalGames;

    [ObservableProperty]
    private int _currentGameNumber;

    /// <summary>
    /// Main channel for video previews. Themes should preferably use this property
    /// </summary>
    public IVideoSurface? MainVideoSurface => _videoSurface;

    /// <summary>
    /// Optional second video channel (e.g., system intro, B-roll)
    /// Will be fully integrated with LibVLC in a later step
    /// </summary>
    public IVideoSurface? SecondaryVideoSurface => _secondaryVideoSurface;
    
    /// <summary>
    /// Release year of the currently selected game, or null if unknown or
    /// no game is selected. Intended for use in BigMode themes (bottom info bar)
    /// </summary>
    public int? SelectedYear => SelectedItem?.ReleaseDate?.Year;

    /// <summary>
    /// Developer of the currently selected game, or null/empty if unknown or
    /// no game is selected. Intended for use in BigMode themes (bottom info bar)
    /// </summary>
    public string? SelectedDeveloper => SelectedItem?.Developer;

    /// <summary>
    /// Resolved marquee artwork path for the currently selected item in context of the
    /// active node. Resolution order: item → node. Does not apply theme defaults
    /// </summary>
    public string? ActiveMarqueePath =>
        ResolveArtworkForSelection(AssetType.Marquee);

    /// <summary>
    /// Resolved bezel artwork path for the currently selected item in context of the
    /// active node. Resolution order: item → node. Does not apply theme defaults
    /// </summary>
    public string? ActiveBezelPath =>
        ResolveArtworkForSelection(AssetType.Bezel);

    /// <summary>
    /// Resolved control panel artwork path for the currently selected item in context of the
    /// active node. Resolution order: item → node. Does not apply theme defaults
    /// </summary>
    public string? ActiveControlPanelPath =>
        ResolveArtworkForSelection(AssetType.ControlPanel);

    /// <summary>
    /// Helper that resolves artwork for the currently selected item using the standard
    /// item → node fallback order. Returns null if no game list or selection is active
    /// Theme-level defaults are intentionally not applied here to keep this ViewModel
    /// theme-agnostic. Themes can use ThemeProperties for additional fallbacks
    /// </summary>
    private string? ResolveArtworkForSelection(AssetType type)
    {
        if (!IsGameListActive || SelectedItem is null)
            return null;

        // ThemeContextNode represents the logical node whose items are currently shown
        var node = ThemeContextNode ?? CurrentNode;

        return AssetResolver.ResolveAssetPath(
            SelectedItem,
            node,
            type);
    }
    
    public ICommand ForceExitCommand { get; }

    public event Action? RequestClose;
    public event Func<MediaItem, Task>? RequestPlay;

    public BigModeViewModel(
        ObservableCollection<MediaNode> rootNodes,
        AppSettings settings,
        Theme theme,
        SoundEffectService soundEffectService,
        GamepadService gamepadService)
    {
        _rootNodes = rootNodes ?? throw new ArgumentNullException(nameof(rootNodes));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _soundEffectService = soundEffectService ?? throw new ArgumentNullException(nameof(soundEffectService));
        _gamepadService = gamepadService ?? throw new ArgumentNullException(nameof(gamepadService));

        // Start at root categories.
        CurrentCategories = _rootNodes;

        // Root = no node context -> default/root theme selection logic
        ThemeContextNode = null;

        // Keep some theme metadata available for bindings/debug overlays
        CurrentThemeDirectory = _theme.BasePath;

        // Default capability based on the loaded theme (the host may further restrict this based on VideoSlot existence)
        CanShowVideo = _theme.PrimaryVideoEnabled || _theme.SecondaryVideoEnabled;

        CategoryTitle = Strings.BigMode_MainMenu;

        // Determine the effective hardware decode mode from settings
        // Accepted values are implementation-defined; typical values are:
        //  - "none"  : always use software decoding (safest default)
        //  - "auto"  : let VLC/FFmpeg pick a suitable hardware backend
        //  - "vaapi" : force VAAPI on compatible Linux systems
        var hwMode = (_settings.VlcHardwareDecodeMode ?? "none").Trim().ToLowerInvariant();
        if (hwMode is not ("none" or "auto" or "vaapi"))
        {
            // Fallback for unknown/typo values to keep startup stable
            hwMode = "none";
        }

        // Linux-friendly VLC options:
        // --no-osd         : disable VLC overlays (file name, volume, etc.)
        // --avcodec-hw=...: hardware decode mode from settings (see above)
        // --quiet          : reduce console noise for normal runtime
        //
        // For detailed troubleshooting you can temporarily replace "--quiet" with
        // e.g. "--verbose=2" and/or set enableDebugLogs: true.
        string[] vlcOptions =
        {
            "--no-osd",
            $"--avcodec-hw={hwMode}",
            "--quiet"
        };

        _libVlc = new LibVLC(enableDebugLogs: false, vlcOptions);

        // Secondary channel uses an isolated LibVLC instance to avoid cross-player interference.
        string[] secondaryVlcOptions =
        {
            "--no-osd",
            "--no-audio",
            $"--avcodec-hw={hwMode}",
            "--quiet"
        };

        _secondaryLibVlc = new LibVLC(enableDebugLogs: false, secondaryVlcOptions);
        
        // Video surface for callback rendering – main channel
        _videoSurface = new LibVlcVideoSurface();
        _videoSurface.FrameReady += OnMainVideoFrameReady;

        // Second video channel
        _secondaryVideoSurface = new LibVlcVideoSurface();
        
        MediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = 100,
            Scale = 0f // 0 = scale to fill the control
        };

        // Setting up video format and callbacks
        MediaPlayer.SetVideoFormatCallbacks(
            _videoSurface.VideoFormat,
            _videoSurface.VideoCleanup);

        MediaPlayer.SetVideoCallbacks(
            _videoSurface.VideoLock,
            _videoSurface.VideoUnlock,
            _videoSurface.VideoDisplay);
        
        // Loop preview videos by restarting them when they reach the end
        MediaPlayer.EndReached += OnPreviewEndReached;
        
        // Second player for background videos
        _secondaryPlayer = new MediaPlayer(_secondaryLibVlc)
        {
            Volume = 0,  // Wallpaper without sound
            Scale = 0f
        };
        
        _secondaryPlayer.SetVideoFormatCallbacks(
            _secondaryVideoSurface.VideoFormat,
            _secondaryVideoSurface.VideoCleanup);

        _secondaryPlayer.SetVideoCallbacks(
            _secondaryVideoSurface.VideoLock,
            _secondaryVideoSurface.VideoUnlock,
            _secondaryVideoSurface.VideoDisplay);

        _secondaryPlayer.EndReached += OnSecondaryBackgroundEndReached;

        ForceExitCommand = new RelayCommand(() => RequestClose?.Invoke());

        // Subscribe to gamepad events (raised on SDL thread; handler methods must marshal if they touch UI state)
        _gamepadService.OnDirectionStateChanged += OnGamepadDirectionStateChanged;
        _gamepadService.OnSelect += OnGamepadSelect;
        _gamepadService.OnBack += OnGamepadBack;

        if (CurrentCategories.Count > 0)
        {
            RestoreLastState();
        }
        
        // Ensure counters are in a known state even if the restored state
        // did not activate a game list yet
        UpdateGameCounters();
        
        // After construction: prepare a possible background channel based on the current theme
        UpdateSecondaryBackgroundVideo();
        
        // Start attract-mode timer (if configured by the theme)
        InitializeAttractModeTimer();
    }

    /// <summary>
    /// Loads/updates the optional theme background video (secondary channel),
    /// using <see cref="Theme.SecondaryBackgroundVideoPath"/> resolved against <see cref="Theme.BasePath"/>
    /// </summary>
    private void UpdateSecondaryBackgroundVideo()
    {
        // Stop previous content (best-effort)
        try
        {
            if (_secondaryPlayer != null && _secondaryPlayer.IsPlaying)
                _secondaryPlayer.Stop();
        }
        catch
        {
            // best effort
        }

        try
        {
            _secondaryPlayer?.Media?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _secondaryBackgroundMedia?.Dispose();
        }
        catch
        {
            // ignore
        }

        _secondaryBackgroundMedia = null;

        SecondaryVideoHasContent = false;
        SecondaryVideoIsPlaying = false;

        if (!CanShowVideo || _secondaryPlayer == null || _secondaryVideoSurface == null)
            return;

        // Resolve theme folder + theme-defined relative path for background video
        try
        {
            var basePath = _theme.BasePath;
            var relative = _theme.SecondaryBackgroundVideoPath;

            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(relative))
                return;

            var candidate = System.IO.Path.Combine(basePath, relative);
            if (!System.IO.File.Exists(candidate))
                return;

            var media = new Media(_secondaryLibVlc, new Uri(candidate));
            _secondaryBackgroundMedia = media;
            _secondaryPlayer.Media = media;

            SecondaryVideoHasContent = true;

            // Do not start yet; only start once the view is marked as ready
        }
        catch
        {
            // If this fails, keep the secondary channel disabled
            SecondaryVideoHasContent = false;
            SecondaryVideoIsPlaying = false;
        }
    }

    /// <summary>
    /// Starts the prepared background video, if available and the view is ready
    /// </summary>
    private void EnsureSecondaryBackgroundPlayingIfReady()
    {
        if (!CanShowVideo || !_isViewReady || _isLaunching)
            return;

        if (!SecondaryVideoHasContent || _secondaryPlayer == null || _secondaryBackgroundMedia == null)
            return;

        try
        {
            if (!_secondaryPlayer.IsPlaying)
            {
                _secondaryPlayer.Play();
            }

            SecondaryVideoIsPlaying = true;
        }
        catch
        {
            SecondaryVideoIsPlaying = false;
        }
    }

    private void OnSecondaryBackgroundEndReached(object? sender, EventArgs e)
    {
        // Loop the background video indefinitely
        if (!SecondaryVideoHasContent || _secondaryPlayer == null)
            return;

        UiThreadHelper.Post(() =>
        {
            if (!SecondaryVideoHasContent || _secondaryPlayer == null)
                return;

            try
            {
                _secondaryPlayer.Stop();
                _secondaryPlayer.Play();
                SecondaryVideoIsPlaying = true;
            }
            catch
            {
                SecondaryVideoIsPlaying = false;
            }
        }, DispatcherPriority.Background);
    }
    
    /// <summary>
    /// Updates game counters (TotalGames, CurrentGameNumber) based on the current
    /// game list and selection. Safe to call from any place that changes Items
    /// or SelectedItem, or toggles between category and game view
    /// </summary>
    private void UpdateGameCounters()
    {
        if (!IsGameListActive || Items is not { Count: > 0 })
        {
            TotalGames = 0;
            CurrentGameNumber = 0;
            return;
        }

        TotalGames = Items.Count;

        if (SelectedItem == null)
        {
            CurrentGameNumber = 0;
            return;
        }

        var index = Items.IndexOf(SelectedItem);
        CurrentGameNumber = index >= 0 ? index + 1 : 0;
    }

    private void UpdateCircularItems()
    {
        _circularItems.Clear();

        if (!IsGameListActive || Items.Count == 0)
            return;

        var count = Items.Count;
        var windowSize = Math.Min(CircularLogoWindowSize, count);
        if (windowSize <= 0)
            return;

        if (windowSize % 2 == 0)
            windowSize--;

        if (windowSize <= 0)
        {
            foreach (var item in Items)
                _circularItems.Add(item);
            return;
        }

        var selectedIndex = SelectedItem != null ? Items.IndexOf(SelectedItem) : 0;
        if (selectedIndex < 0)
            selectedIndex = 0;

        var half = windowSize / 2;

        for (int i = -half; i <= half; i++)
        {
            var idx = (selectedIndex + i) % count;
            if (idx < 0)
                idx += count;

            _circularItems.Add(Items[idx]);
        }
    }
    
    /// <summary>
    /// Updates the active theme at runtime
    /// The host is responsible for swapping the view and calling NotifyViewReady() afterwards
    /// </summary>
    public void UpdateTheme(Theme newTheme)
    {
        _theme = newTheme ?? throw new ArgumentNullException(nameof(newTheme));

        CurrentThemeDirectory = _theme.BasePath;

        // When a new theme view is loaded, the VideoView attachment state becomes invalid
        // Wait for the next NotifyViewReady() call before starting preview playback
        _isViewReady = false;

        StopVideo();

        // Update default capability (host may still disable based on missing slot)
        CanShowVideo = _theme.PrimaryVideoEnabled || _theme.SecondaryVideoEnabled;
        
        // Theme swap may change how many items are visible or how they are
        // interpreted by the theme; keep counters consistent
        UpdateGameCounters();
        
        // Background video for the new theme
        UpdateSecondaryBackgroundVideo();
        
        // Re-evaluate attract-mode configuration for the new theme
        InitializeAttractModeTimer();
    }
}
