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
/// ViewModel used as DataContext for BigMode themes.
/// Theme authors bind to the properties exposed by this type.
/// </summary>
public partial class BigModeViewModel : ViewModelBase
{
    private readonly LibVLC _libVlc;
    private readonly AppSettings _settings;

    // Navigation history used for "Back" behavior.
    private readonly Stack<ObservableCollection<MediaNode>> _navigationStack = new();
    private readonly Stack<string> _titleStack = new();
    private readonly Stack<MediaNode> _navigationPath = new();

    // Root categories (top-level nodes).
    private readonly ObservableCollection<MediaNode> _rootNodes;

    private bool _isLaunching;
    private string _currentThemePath = string.Empty;
    private CancellationTokenSource? _previewCts;

    // Prevents LibVLC from creating its own output window during startup:
    // previews must start only after the theme view is fully loaded.
    private bool _isViewReady;

    // Makes state saving idempotent (RequestClose + Dispose can both call SaveState).
    private bool _stateSaved;

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

        // Start at root categories
        CurrentCategories = _rootNodes;

        // Linux-friendly VLC options:
        // --no-osd: no text overlays
        // --avcodec-hw=none: software decode to avoid driver/X11 quirks
        // --quiet: reduce console noise
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