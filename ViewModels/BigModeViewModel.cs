using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
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
    private readonly LibVLC _libVlc;
    private readonly AppSettings _settings;

    // Injected dependencies
    private Theme _theme;
    private readonly SoundEffectService _soundEffectService;
    private readonly GamepadService _gamepadService;

    // Oberfläche für das gerenderte Video (LibVLC-Callbacks)
    private readonly LibVlcVideoSurface _videoSurface;

    // Zweite Oberfläche für einen optionalen zweiten Video-Kanal (Secondary).
    // Wird in einem späteren Schritt vollständig verdrahtet.
    private readonly LibVlcVideoSurface? _secondaryVideoSurface;

    // Player für den Haupt-Preview-Kanal (MediaPlayer-Property).
    // Player für den zweiten Hintergrundkanal.
    private MediaPlayer? _secondaryPlayer;

    // Aktuelle Media-Instanz für den Hintergrundkanal (z.B. bkg_anim.mp4).
    private Media? _secondaryBackgroundMedia;
    private string? _secondaryBackgroundPath;
    
    /// <summary>
    /// Alte API: Oberfläche für das Vorschaubild (wird von BigModeHostView gerendert).
    /// Entspricht dem Haupt-Videokanal (MainVideoSurface).
    /// </summary>
    public IVideoSurface VideoSurface => _videoSurface;
    
    /// <summary>
    /// Hauptkanal für Video-Vorschau. Themes sollten bevorzugt diese Property nutzen.
    /// </summary>
    public IVideoSurface? MainVideoSurface => _videoSurface;

    /// <summary>
    /// Optionaler zweiter Videokanal (z.B. System-Intro, B-Roll).
    /// Wird in einem späteren Schritt vollständig mit LibVLC verdrahtet.
    /// </summary>
    public IVideoSurface? SecondaryVideoSurface => _secondaryVideoSurface;
    
    // Navigation history used to implement "Back" behavior.
    private readonly Stack<ObservableCollection<MediaNode>> _navigationStack = new();
    private readonly Stack<string> _titleStack = new();
    private readonly Stack<MediaNode> _navigationPath = new();

    // Top-level categories (e.g. "Games", "Movies", ...).
    private readonly ObservableCollection<MediaNode> _rootNodes;

    // Prevents input while a game is being launched.
    private bool _isLaunching;

    // Avoids unnecessary VLC stop/play cycles (reduces flicker and CPU usage).
    private string? _currentPreviewVideoPath;

    // LibVLC may open its own output window if playback starts before the VideoView is attached.
    // The host must call NotifyViewReady() after the theme view is loaded.
    private bool _isViewReady;

    // Makes state saving idempotent (RequestClose + Dispose can both call SaveState).
    private bool _stateSaved;

    [ObservableProperty]
    private int _selectedItemIndex = -1;
    private int _selectedCategoryIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMarqueePath))]
    [NotifyPropertyChangedFor(nameof(ActiveBezelPath))]
    [NotifyPropertyChangedFor(nameof(ActiveControlPanelPath))]
    private MediaNode? _themeContextNode;
    
    // Avoid repeated filesystem I/O while scrolling large lists.
    private readonly Dictionary<string, string?> _itemVideoPathCache = new();
    private readonly Dictionary<string, string?> _nodeVideoPathCache = new();

    // --- View state ---

    /// <summary>
    /// True: game list is shown. False: category list is shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategorySelectionActive))]
    [NotifyPropertyChangedFor(nameof(ActiveMarqueePath))]
    [NotifyPropertyChangedFor(nameof(ActiveBezelPath))]
    [NotifyPropertyChangedFor(nameof(ActiveControlPanelPath))]
    private bool _isGameListActive;

    public bool IsCategorySelectionActive => !IsGameListActive;

    [ObservableProperty]
    private ObservableCollection<MediaNode> _currentCategories;

    [ObservableProperty]
    private MediaNode? _selectedCategory;

    /// <summary>
    /// Sichtbarkeits-Flag für den Haupt-Videokanal (Legacy: Overlay-Fades).
    /// Themes können dies weiterhin für Opacity-Bindings verwenden, bis die
    /// Inline-Video-Implementierung abgeschlossen ist.
    /// </summary>
    [ObservableProperty]
    private bool _isVideoVisible;

    // The overlay exists in the visual tree only if this flag is true.
    // IsVideoVisible is used for opacity/fade.
    [ObservableProperty]
    private bool _isVideoOverlayVisible;

    // Theme capability: the host should only enable video if the theme allows it AND provides a slot.
    [ObservableProperty]
    private bool _canShowVideo = true;

    /// <summary>
    /// Gibt an, ob für die aktuelle Auswahl ein Video im Hauptkanal verfügbar ist.
    /// (CanShowVideo &amp;&amp; aufgelöster Pfad != null)
    /// </summary>
    [ObservableProperty]
    private bool _mainVideoHasContent;

    /// <summary>
    /// Gibt an, ob der Haupt-Videokanal aktuell abgespielt wird.
    /// </summary>
    [ObservableProperty]
    private bool _mainVideoIsPlaying;

    /// <summary>
    /// Gibt an, ob für die aktuelle Auswahl ein Video im zweiten Kanal verfügbar ist.
    /// Wird in einem späteren Schritt befüllt.
    /// </summary>
    [ObservableProperty]
    private bool _secondaryVideoHasContent;

    /// <summary>
    /// Gibt an, ob der zweite Videokanal aktuell abgespielt wird.
    /// Wird in einem späteren Schritt befüllt.
    /// </summary>
    [ObservableProperty]
    private bool _secondaryVideoIsPlaying;
    
    [ObservableProperty]
    private MediaNode? _currentNode;

    [ObservableProperty]
    private string _currentThemeDirectory = string.Empty;

    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    [ObservableProperty]
    private ObservableCollection<MediaItem> _items = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveMarqueePath))]
    [NotifyPropertyChangedFor(nameof(ActiveBezelPath))]
    [NotifyPropertyChangedFor(nameof(ActiveControlPanelPath))]
    private MediaItem? _selectedItem;

    [ObservableProperty]
    private string _categoryTitle = "";

    /// <summary>
    /// Total number of games in the current game list.
    /// Equals zero while the category list is active.
    /// </summary>
    [ObservableProperty]
    private int _totalGames;

    /// <summary>
    /// 1-based index of the currently selected game in the current game list.
    /// Equals zero if no item is selected or the category list is active.
    /// </summary>
    [ObservableProperty]
    private int _currentGameNumber;

    /// <summary>
    /// Release year of the currently selected game, or null if unknown or
    /// no game is selected. Intended for use in BigMode themes (bottom info bar).
    /// </summary>
    public int? SelectedYear => SelectedItem?.ReleaseDate?.Year;

    /// <summary>
    /// Developer of the currently selected game, or null/empty if unknown or
    /// no game is selected. Intended for use in BigMode themes (bottom info bar).
    /// </summary>
    public string? SelectedDeveloper => SelectedItem?.Developer;

    /// <summary>
    /// Resolved marquee artwork path for the currently selected item in context of the
    /// active node. Resolution order: item → node. Does not apply theme defaults.
    /// </summary>
    public string? ActiveMarqueePath =>
        ResolveArtworkForSelection(AssetType.Marquee);

    /// <summary>
    /// Resolved bezel artwork path for the currently selected item in context of the
    /// active node. Resolution order: item → node. Does not apply theme defaults.
    /// </summary>
    public string? ActiveBezelPath =>
        ResolveArtworkForSelection(AssetType.Bezel);

    /// <summary>
    /// Resolved control panel artwork path for the currently selected item in context of the
    /// active node. Resolution order: item → node. Does not apply theme defaults.
    /// </summary>
    public string? ActiveControlPanelPath =>
        ResolveArtworkForSelection(AssetType.ControlPanel);

    /// <summary>
    /// Helper that resolves artwork for the currently selected item using the standard
    /// item → node fallback order. Returns null if no game list or selection is active.
    /// Theme-level defaults are intentionally not applied here to keep this ViewModel
    /// theme-agnostic. Themes can use ThemeProperties for additional fallbacks.
    /// </summary>
    private string? ResolveArtworkForSelection(AssetType type)
    {
        if (!IsGameListActive || SelectedItem is null)
            return null;

        // ThemeContextNode represents the logical node whose items are currently shown.
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

        // Root = no node context -> default/root theme selection logic.
        ThemeContextNode = null;

        // Keep some theme metadata available for bindings/debug overlays.
        CurrentThemeDirectory = _theme.BasePath;

        // Default capability based on the loaded theme (the host may further restrict this based on VideoSlot existence).
        CanShowVideo = _theme.VideoEnabled;

        CategoryTitle = Strings.BigMode_MainMenu;

        // Linux-friendly VLC options:
        // --no-osd: disable VLC overlays (file name, volume, etc.)
        // --avcodec-hw=none: force software decoding for better compatibility
        // --quiet: reduce console noise
        string[] vlcOptions = { "--no-osd", "--avcodec-hw=none", "--quiet" };

        _libVlc = new LibVLC(enableDebugLogs: false, vlcOptions);
        
        // Video-Surface für Callback-Rendering – Hauptkanal.
        _videoSurface = new LibVlcVideoSurface();

        // Zweiter Videokanal: Oberfläche vorbereiten (Player-Anbindung folgt später).
        _secondaryVideoSurface = new LibVlcVideoSurface();
        
        MediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = 100,
            Scale = 0f // 0 = scale to fill the control
        };

        // Videoformat + -Callbacks einrichten (wie in LibVlcVideoPlayer)
        MediaPlayer.SetVideoFormatCallbacks(
            _videoSurface.VideoFormat,
            _videoSurface.VideoCleanup);

        MediaPlayer.SetVideoCallbacks(
            _videoSurface.VideoLock,
            _videoSurface.VideoUnlock,
            _videoSurface.VideoDisplay);
        
        // Loop preview videos by restarting them when they reach the end.
        MediaPlayer.EndReached += OnPreviewEndReached;
        
        // Zweiter Player für Hintergrundvideos (z.B. Theme-Wallpaper).
        _secondaryPlayer = new MediaPlayer(_libVlc)
        {
            Volume = 0,  // Wallpaper ohne Ton
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

        // Subscribe to gamepad events (raised on SDL thread; handler methods must marshal if they touch UI state).
        _gamepadService.OnUp += OnGamepadUp;
        _gamepadService.OnDown += OnGamepadDown;
        _gamepadService.OnLeft += OnGamepadLeft;
        _gamepadService.OnRight += OnGamepadRight;
        _gamepadService.OnSelect += OnGamepadSelect;
        _gamepadService.OnBack += OnGamepadBack;

        if (CurrentCategories.Count > 0)
        {
            RestoreLastState();
        }
        
        // Ensure counters are in a known state even if the restored state
        // did not activate a game list yet.
        UpdateGameCounters();
        
        // Nach Konstruktion: möglichen Hintergrundkanal anhand des aktuellen Themes vorbereiten.
        UpdateSecondaryBackgroundVideo();
    }

    /// <summary>
    /// Lädt/aktualisiert das optionale Theme-Hintergrundvideo (Secondary-Kanal).
    /// Sucht im aktuellen Theme-Verzeichnis nach "Video/bkg_anim.mp4".
    /// </summary>
    private void UpdateSecondaryBackgroundVideo()
    {
        // Vorherigen Inhalt bereinigen
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
        _secondaryBackgroundPath = null;

        SecondaryVideoHasContent = false;
        SecondaryVideoIsPlaying = false;

        if (!CanShowVideo || _secondaryPlayer == null || _secondaryVideoSurface == null)
            return;

        // Theme-Ordner + vom Theme definierter Pfad für Hintergrundvideo
        try
        {
            var basePath = _theme.BasePath;
            var relative = _theme.SecondaryBackgroundVideoPath;

            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(relative))
                return;

            var candidate = System.IO.Path.Combine(basePath, relative);
            if (!System.IO.File.Exists(candidate))
                return;

            _secondaryBackgroundPath = candidate;
            var media = new Media(_libVlc, new Uri(candidate));
            _secondaryBackgroundMedia = media;
            _secondaryPlayer.Media = media;

            SecondaryVideoHasContent = true;

            // Noch nicht starten – das machen wir erst, wenn die View "ready" ist.
        }
        catch
        {
            // Bei Fehlern den Hintergrundkanal einfach deaktiviert lassen.
            SecondaryVideoHasContent = false;
            SecondaryVideoIsPlaying = false;
        }
    }

    /// <summary>
    /// Startet das vorbereitete Hintergrundvideo, falls vorhanden und die View bereit ist.
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
        // Loop des Hintergrundvideos – läuft "ewig" im Kreis.
        if (!SecondaryVideoHasContent || _secondaryPlayer == null)
            return;

        try
        {
            _secondaryPlayer.Stop();
            _secondaryPlayer.Play();
        }
        catch
        {
            SecondaryVideoIsPlaying = false;
        }
    }
    
    /// <summary>
    /// Updates game counters (TotalGames, CurrentGameNumber) based on the current
    /// game list and selection. Safe to call from any place that changes Items
    /// or SelectedItem, or toggles between category and game view.
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
    
    /// <summary>
    /// Updates the active theme at runtime.
    /// The host is responsible for swapping the view and calling NotifyViewReady() afterwards.
    /// </summary>
    public void UpdateTheme(Theme newTheme)
    {
        _theme = newTheme ?? throw new ArgumentNullException(nameof(newTheme));

        CurrentThemeDirectory = _theme.BasePath;

        // When a new theme view is loaded, the VideoView attachment state becomes invalid.
        // Wait for the next NotifyViewReady() call before starting preview playback.
        _isViewReady = false;

        // Ensure VLC is stopped before the old view detaches to avoid LibVLC spawning its own output window.
        _previewDebounceCts?.Cancel();
        _previewDebounceCts = null;

        StopVideo();

        // Update default capability (host may still disable based on missing slot).
        CanShowVideo = _theme.VideoEnabled;
        
        // Theme swap may change how many items are visible or how they are
        // interpreted by the theme; keep counters consistent.
        UpdateGameCounters();
        
        // Hintergrund-Video für das neue Theme aktualisieren.
        UpdateSecondaryBackgroundVideo();
    }
}