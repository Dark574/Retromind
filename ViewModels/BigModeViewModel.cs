using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// Root ViewModel for BigMode.
/// Theme authors bind to properties exposed by this type.
/// </summary>
public partial class BigModeViewModel : ViewModelBase
{
    private readonly LibVLC _libVlc;
    private readonly AppSettings _settings;

    // Navigation history used to implement "Back" behavior.
    private readonly Stack<ObservableCollection<MediaNode>> _navigationStack = new();
    private readonly Stack<string> _titleStack = new();
    private readonly Stack<MediaNode> _navigationPath = new();

    // Top-level categories (e.g. "Games", "Movies", ...).
    private readonly ObservableCollection<MediaNode> _rootNodes;

    // Prevents input while a game is being launched.
    private bool _isLaunching;

    // Currently active theme file to avoid redundant reloads.
    private string _currentThemePath = string.Empty;

    // Helps avoiding unnecessary VLC Stop/Play cycles (prevents flicker and saves CPU).
    private string? _currentPreviewVideoPath;
    
    // Debounce/cancellation for category/game preview playback.
    private CancellationTokenSource? _previewCts;

    // LibVLC may open its own output window if playback starts before the VideoView is attached.
    // The host must call NotifyViewReady() after the theme view is loaded.
    private bool _isViewReady;

    // Makes state saving idempotent (RequestClose + Dispose can both call SaveState).
    private bool _stateSaved;
    
    // Performance: Avoid O(n) IndexOf() on every navigation step for very large lists.
    private int _selectedItemIndex = -1;
    private int _selectedCategoryIndex = -1;
    
    // Performance: avoid repeated filesystem I/O (File.Exists) while scrolling large lists.
    private readonly Dictionary<string, string?> _itemVideoPathCache = new();
    private readonly Dictionary<string, string?> _nodeVideoPathCache = new();

    // --- View state ---

    /// <summary>
    /// True: game list is shown. False: category list is shown.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategorySelectionActive))]
    private bool _isGameListActive;

    public bool IsCategorySelectionActive => !IsGameListActive;

    [ObservableProperty]
    private ObservableCollection<MediaNode> _currentCategories;

    [ObservableProperty]
    private MediaNode? _selectedCategory;

    [ObservableProperty]
    private bool _isVideoVisible;

    [ObservableProperty]
    private MediaNode? _currentNode;

    [ObservableProperty]
    private string _currentThemeDirectory = string.Empty;

    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;

    [ObservableProperty]
    private ObservableCollection<MediaItem> _items = new();

    [ObservableProperty]
    private MediaItem? _selectedItem;

    [ObservableProperty]
    private string _categoryTitle = "Main Menu";

    public ICommand ForceExitCommand { get; }

    public event Action? RequestClose;
    public event Func<MediaItem, Task>? RequestPlay;
    public event Action<string>? RequestThemeChange;

    public BigModeViewModel(ObservableCollection<MediaNode> rootNodes, AppSettings settings)
    {
        _rootNodes = rootNodes;
        _settings = settings;

        // Start at root categories.
        CurrentCategories = _rootNodes;

        // Linux-friendly VLC options:
        // --no-osd: disables overlays (file name, volume, etc.)
        // --avcodec-hw=none: forces software decoding for better compatibility
        // --quiet: reduces console noise
        string[] vlcOptions = { "--no-osd", "--avcodec-hw=none", "--quiet" };

        _libVlc = new LibVLC(enableDebugLogs: false, vlcOptions);
        MediaPlayer = new MediaPlayer(_libVlc) { Volume = 100 };

        ForceExitCommand = new RelayCommand(() => RequestClose?.Invoke());

        if (CurrentCategories.Count > 0)
        {
            RestoreLastState();
        }
    }
}