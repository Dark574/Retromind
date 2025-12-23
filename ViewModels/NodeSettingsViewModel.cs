using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for configuring a <see cref="MediaNode"/>.
/// Supports editing metadata, default emulator selection, theme binding, and node-level assets (cover/logo/wallpaper/video).
/// </summary>
public partial class NodeSettingsViewModel : ViewModelBase
{
    private const int PreviewDecodeWidth = 200;

    // Sentinel: "inherit / no default emulator".
    // We avoid null-suppression on Id by using an empty string.
    private const string InheritEmulatorId = "";

    private readonly MediaNode _node;
    private readonly AppSettings _settings;
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;

    public ObservableCollection<MediaAsset> Assets => _node.Assets;

    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();
    public ObservableCollection<string> AvailableThemes { get; } = new();

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    [ObservableProperty] private bool? _randomizeCovers;
    [ObservableProperty] private bool? _randomizeMusic;

    [ObservableProperty] private EmulatorConfig? _selectedEmulator;

    [ObservableProperty] private Bitmap? _coverPreview;
    [ObservableProperty] private Bitmap? _logoPreview;
    [ObservableProperty] private Bitmap? _wallpaperPreview;

    [ObservableProperty] private string _videoName = "";

    [ObservableProperty] private string? _selectedTheme;

    public bool IsThemeExplicitlySelected => !string.IsNullOrWhiteSpace(SelectedTheme);
    
    public bool HasVideoAsset => _node.Assets.Any(a => a.Type == AssetType.Video);

    partial void OnSelectedThemeChanged(string? value)
        => OnPropertyChanged(nameof(IsThemeExplicitlySelected));

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearThemeCommand { get; }

    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }
    public IAsyncRelayCommand<AssetType> DeleteAssetCommand { get; }

    /// <summary>
    /// Set by the view (Window injects it on open).
    /// </summary>
    public IStorageProvider? StorageProvider { get; set; }

    /// <summary>
    /// Signals the view to close (true = saved, false = cancelled).
    /// </summary>
    public event Action<bool>? RequestClose;

    public enum WrapperMode
    {
        Inherit,
        None,
        Override
    }

    public sealed partial class LaunchWrapperRow : ObservableObject
    {
        [ObservableProperty] private string _path = string.Empty;
        [ObservableProperty] private string _args = string.Empty;

        public LaunchWrapperRow()
        {
        }

        public LaunchWrapperRow(LaunchWrapper wrapper)
        {
            Path = wrapper.Path ?? string.Empty;
            Args = wrapper.Args ?? string.Empty;
        }

        public LaunchWrapper ToModel()
            => new LaunchWrapper { Path = Path?.Trim() ?? string.Empty, Args = string.IsNullOrWhiteSpace(Args) ? null : Args };
    }

    [ObservableProperty]
    private WrapperMode _nativeWrapperMode = WrapperMode.Inherit;

    public ObservableCollection<LaunchWrapperRow> NativeWrappers { get; } = new();

    public IRelayCommand AddNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperDownCommand { get; }

    public NodeSettingsViewModel(
        MediaNode node,
        AppSettings settings,
        FileManagementService fileService,
        List<string> nodePath)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _nodePath = nodePath ?? [];

        InitializeFromNode();
        InitializeEmulators();
        LoadAvailableThemes();

        SelectedTheme = string.IsNullOrWhiteSpace(_node.ThemePath) ? null : _node.ThemePath;

        RefreshPreviews();

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        ClearThemeCommand = new RelayCommand(() => SelectedTheme = null);

        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
        DeleteAssetCommand = new AsyncRelayCommand<AssetType>(DeleteAssetsByTypeAsync);
        
        AddNativeWrapperCommand = new RelayCommand(AddNativeWrapper);
        RemoveNativeWrapperCommand = new RelayCommand<LaunchWrapperRow?>(RemoveNativeWrapper);
        MoveNativeWrapperUpCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperUp,
            row => row != null && NativeWrappers.IndexOf(row) > 0);

        MoveNativeWrapperDownCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperDown,
            row => row != null && NativeWrappers.IndexOf(row) >= 0 && NativeWrappers.IndexOf(row) < NativeWrappers.Count - 1);

        NativeWrappers.CollectionChanged += (_, __) =>
        {
            MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
            MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        };

        InitializeNativeWrapperUiFromNode();
        
        // Update once after initialization
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    public bool IsNativeWrapperInherit
    {
        get => NativeWrapperMode == WrapperMode.Inherit;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.Inherit;
        }
    }

    public bool IsNativeWrapperNone
    {
        get => NativeWrapperMode == WrapperMode.None;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.None;
        }
    }

    public bool IsNativeWrapperOverride
    {
        get => NativeWrapperMode == WrapperMode.Override;
        set
        {
            if (!value) return;
            NativeWrapperMode = WrapperMode.Override;
        }
    }
    
    private void InitializeNativeWrapperUiFromNode()
    {
        NativeWrappers.Clear();

        if (_node.NativeWrappersOverride == null)
        {
            NativeWrapperMode = WrapperMode.Inherit;
            return;
        }

        if (_node.NativeWrappersOverride.Count == 0)
        {
            NativeWrapperMode = WrapperMode.None;
            return;
        }

        NativeWrapperMode = WrapperMode.Override;
        foreach (var w in _node.NativeWrappersOverride)
            NativeWrappers.Add(new LaunchWrapperRow(w));
    }

    // Keep RadioButtons and IsVisible in sync if NativeWrapperMode changes in code (e.g. InitializeNativeWrapperUiFromNode)
    partial void OnNativeWrapperModeChanged(WrapperMode value)
    {
        OnPropertyChanged(nameof(IsNativeWrapperInherit));
        OnPropertyChanged(nameof(IsNativeWrapperNone));
        OnPropertyChanged(nameof(IsNativeWrapperOverride));
    }
    
    private void AddNativeWrapper()
    {
        NativeWrappers.Add(new LaunchWrapperRow());
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void RemoveNativeWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;
        NativeWrappers.Remove(row);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveNativeWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx <= 0) return;
        NativeWrappers.Move(idx, idx - 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveNativeWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= NativeWrappers.Count - 1) return;
        NativeWrappers.Move(idx, idx + 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
    }
    
    private void InitializeFromNode()
    {
        Name = _node.Name;
        Description = _node.Description;

        RandomizeCovers = _node.RandomizeCovers;
        RandomizeMusic = _node.RandomizeMusic;
    }

    private void InitializeEmulators()
    {
        AvailableEmulators.Clear();

        AvailableEmulators.Add(new EmulatorConfig
        {
            Id = InheritEmulatorId,
            Name = Strings.Profile_NoDefaultInherit
        });

        foreach (var emu in _settings.Emulators)
            AvailableEmulators.Add(emu);

        if (!string.IsNullOrWhiteSpace(_node.DefaultEmulatorId))
        {
            SelectedEmulator = AvailableEmulators.FirstOrDefault(e => e.Id == _node.DefaultEmulatorId);
        }

        SelectedEmulator ??= AvailableEmulators.FirstOrDefault();
    }

    private void LoadAvailableThemes()
    {
        AvailableThemes.Clear();

        try
        {
            var themesRoot = AppPaths.ThemesRoot;
            if (!Directory.Exists(themesRoot))
                return;

            // Example layout: Themes/Wheel/theme.axaml, Themes/Default/theme.axaml
            foreach (var dir in Directory.EnumerateDirectories(themesRoot))
            {
                var name = Path.GetFileName(dir);
                var themeFile = Path.Combine(dir, "theme.axaml");
                if (File.Exists(themeFile))
                {
                    // Store as relative path under ThemesRoot (portable, stable when moved)
                    AvailableThemes.Add($"{name}/theme.axaml");
                }
            }
        }
        catch
        {
            // Best-effort: if scanning fails (permissions, IO errors), keep the list empty.
        }
    }

    // --- Asset logic ---

    private void RefreshPreviews()
    {
        CoverPreview = LoadBitmapPreview(AssetType.Cover);
        LogoPreview = LoadBitmapPreview(AssetType.Logo);
        WallpaperPreview = LoadBitmapPreview(AssetType.Wallpaper);

        var vidAsset = _node.Assets.FirstOrDefault(a => a.Type == AssetType.Video);
        VideoName = vidAsset != null
            ? Path.GetFileName(vidAsset.RelativePath)
            : Strings.Common_NoVideo;
        
        OnPropertyChanged(nameof(HasVideoAsset));
    }

    private Bitmap? LoadBitmapPreview(AssetType type)
    {
        var asset = _node.Assets.FirstOrDefault(a => a.Type == type);
        if (asset?.RelativePath is not { Length: > 0 } relPath)
            return null;

        var fullPath = AppPaths.ResolveDataPath(relPath);
        if (!File.Exists(fullPath))
            return null;

        try
        {
            // Decode down to a small width to keep memory and CPU usage low.
            using var stream = File.OpenRead(fullPath);
            return Bitmap.DecodeToWidth(stream, PreviewDecodeWidth);
        }
        catch
        {
            return null;
        }
    }

    private async Task ImportAssetAsync(AssetType type)
    {
        if (StorageProvider == null)
            return;

        var fileTypes = type == AssetType.Video
            ? new[] { new FilePickerFileType(Strings.FileType_Videos) { Patterns = new[] { "*.mp4", "*.mkv", "*.avi" } } }
            : new[] { FilePickerFileTypes.ImageAll };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = string.Format(Strings.Dialog_ImportAssetTitle, type),
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        if (result == null || result.Count == 0)
            return;

        var sourcePath = result[0].Path.LocalPath;

        // Single-slot behavior for node assets:
        // A node can have multiple assets in the filesystem, but for UI simplicity
        // we keep only one active asset per type here (delete old -> import new).
        await DeleteAssetsByTypeAsync(type);

        var imported = await _fileService.ImportAssetAsync(sourcePath, _node, _nodePath, type);
        if (imported != null)
        {
            // Ensure collection mutation is on UI thread
            await UiThreadHelper.InvokeAsync(() => _node.Assets.Add(imported));
        }

        RefreshPreviews();
    }

    private async Task DeleteAssetsByTypeAsync(AssetType type)
    {
        var toRemove = _node.Assets.Where(a => a.Type == type).ToList();
        if (toRemove.Count == 0)
        {
            RefreshPreviews();
            return;
        }

        // Remove from the model first (UI updates immediately).
        foreach (var asset in toRemove)
            _node.Assets.Remove(asset);

        // Delete files on a background thread to avoid UI stalls on slow disks.
        await Task.Run(() =>
        {
            foreach (var asset in toRemove)
            {
                if (string.IsNullOrWhiteSpace(asset.RelativePath))
                    continue;

                var fullPath = AppPaths.ResolveDataPath(asset.RelativePath);

                try
                {
                    Helpers.AsyncImageHelper.InvalidateCache(fullPath);

                    if (File.Exists(fullPath))
                        File.Delete(fullPath);
                }
                catch
                {
                    // Best-effort: if a file is locked or deletion fails, we ignore it.
                    // The next cleanup/rescan can reconcile the state.
                }
            }
        });

        RefreshPreviews();
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return;

        _node.Name = Name;
        _node.Description = Description;
        _node.RandomizeCovers = RandomizeCovers;
        _node.RandomizeMusic = RandomizeMusic;

        _node.ThemePath = string.IsNullOrWhiteSpace(SelectedTheme) ? null : SelectedTheme;

        if (SelectedEmulator != null && !string.IsNullOrWhiteSpace(SelectedEmulator.Id) && SelectedEmulator.Id != InheritEmulatorId)
        {
            _node.DefaultEmulatorId = SelectedEmulator.Id;
        }
        else
        {
            _node.DefaultEmulatorId = null;
        }
        
        // persist wrapper override with tri-state semantics
        switch (NativeWrapperMode)
        {
            case WrapperMode.Inherit:
                _node.NativeWrappersOverride = null;
                break;

            case WrapperMode.None:
                _node.NativeWrappersOverride = new List<LaunchWrapper>();
                break;

            case WrapperMode.Override:
                _node.NativeWrappersOverride = NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();
                break;
        }

        RequestClose?.Invoke(true);
    }
    
    
}