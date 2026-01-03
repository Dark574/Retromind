using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel : ViewModelBase
{
    private readonly EmulatorConfig? _inheritedEmulator;
    private readonly MediaItem _originalItem;
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;
    
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private readonly MediaNode? _parentNode;
    
    // Keep a reference to global settings so preview can resolve emulator profiles
    // and default native wrappers in the same way as the runtime launcher.
    private readonly AppSettings _settings;

    // --- Prefix (Wine/Proton/UMU) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrefix))]
    [NotifyCanExecuteChangedFor(nameof(OpenPrefixFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPrefixCommand))]
    private string _prefixPath = string.Empty;

    public bool HasPrefix => !string.IsNullOrWhiteSpace(PrefixPath);

    public IRelayCommand GeneratePrefixCommand { get; }
    public IRelayCommand OpenPrefixFolderCommand { get; }
    public IRelayCommand ClearPrefixCommand { get; }

    /// <summary>
    /// Human-readable path of the primary launch file used by this item
    /// This resolves exactly what the launcher will use via GetPrimaryLaunchPath()
    /// </summary>
    public string PrimaryFileDisplayPath
    {
        get
        {
            var path = _originalItem.GetPrimaryLaunchPath();
            return string.IsNullOrWhiteSpace(path) ? "(no launch file set)" : path;
        }
    }

    /// <summary>
    /// Command to change the primary launch file (Disc 1 / main executable)
    /// This updates MediaItem.Files so the launcher and preview both use the new path
    /// </summary>
    public IAsyncRelayCommand ChangePrimaryFileCommand { get; }
    
    // --- Per-item environment overrides (e.g. PROTONPATH, PROTON_LOG) ---

    public sealed partial class EnvVarRow : ObservableObject
    {
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private string _value = string.Empty;
    }

    /// <summary>
    /// Editable list of per-item environment overrides.
    /// </summary>
    public ObservableCollection<EnvVarRow> EnvironmentOverrides { get; } = new();

    public IRelayCommand AddEnvironmentVariableCommand { get; }
    public IRelayCommand<EnvVarRow?> RemoveEnvironmentVariableCommand { get; }

    private void AddEnvironmentVariable()
    {
        EnvironmentOverrides.Add(new EnvVarRow());
    }

    private void RemoveEnvironmentVariable(EnvVarRow? row)
    {
        if (row == null) return;
        EnvironmentOverrides.Remove(row);
    }
    
    private void GeneratePrefix()
    {
        // Only generate if not already set (user might have a custom path)
        if (HasPrefix) return;

        var safeTitle = SanitizeForPathSegment(Title);
        var folderName = $"{_originalItem.Id}_{safeTitle}";
        PrefixPath = Path.Combine("Prefixes", folderName);
    }

    private void OpenPrefixFolder()
    {
        try
        {
            if (!HasPrefix) return;

            var folder = Path.GetFullPath(Path.Combine(AppPaths.LibraryRoot, PrefixPath));
            Directory.CreateDirectory(folder);

            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false,
                ArgumentList = { folder }
            });
        }
        catch
        {
            // best-effort: opening a folder must not break the dialog
        }
    }

    private void ClearPrefix()
    {
        PrefixPath = string.Empty;
    }
    
    // --- Native wrapper chain (Tri-state; item-level) ---

    private static string SanitizeForPathSegment(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "Unknown";

        var safe = input.Replace(' ', '_');

        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c.ToString(), string.Empty);

        while (safe.Contains("__", StringComparison.Ordinal))
            safe = safe.Replace("__", "_", StringComparison.Ordinal);

        // Keep it readable, but avoid pathological lengths in folder names.
        const int maxLen = 80;
        if (safe.Length > maxLen)
            safe = safe[..maxLen];

        return safe;
    }
    
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
            => new LaunchWrapper
            {
                Path = Path?.Trim() ?? string.Empty,
                Args = string.IsNullOrWhiteSpace(Args) ? null : Args
            };
    }

    [ObservableProperty]
    private WrapperMode _nativeWrapperMode = WrapperMode.Inherit;

    public ObservableCollection<LaunchWrapperRow> NativeWrappers { get; } = new();

    public IRelayCommand AddNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveNativeWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveNativeWrapperDownCommand { get; }

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

    private void InitializeNativeWrapperUiFromItem()
    {
        // Important: detach old rows (in case the view model instance is reused).
        foreach (var row in NativeWrappers)
            UnwireWrapperRow(row);
        
        NativeWrappers.Clear();

        if (_originalItem.NativeWrappersOverride == null)
        {
            NativeWrapperMode = WrapperMode.Inherit;
            return;
        }

        if (_originalItem.NativeWrappersOverride.Count == 0)
        {
            NativeWrapperMode = WrapperMode.None;
            return;
        }

        NativeWrapperMode = WrapperMode.Override;
        foreach (var w in _originalItem.NativeWrappersOverride)
        {
            var row = new LaunchWrapperRow(w);
            WireWrapperRow(row);
            NativeWrappers.Add(row);
        }
    }

    private void WireWrapperRow(LaunchWrapperRow row)
    {
        row.PropertyChanged += OnWrapperRowPropertyChanged;
    }

    private void UnwireWrapperRow(LaunchWrapperRow row)
    {
        row.PropertyChanged -= OnWrapperRowPropertyChanged;
    }

    private void OnWrapperRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Whenever Path/Args change, update the preview (users expect it to be live).
        if (e.PropertyName == nameof(LaunchWrapperRow.Path) ||
            e.PropertyName == nameof(LaunchWrapperRow.Args))
        {
            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        }
    }
    
    partial void OnNativeWrapperModeChanged(WrapperMode value)
    {
        OnPropertyChanged(nameof(IsNativeWrapperInherit));
        OnPropertyChanged(nameof(IsNativeWrapperNone));
        OnPropertyChanged(nameof(IsNativeWrapperOverride));
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    private void AddNativeWrapper()
    {
        var row = new LaunchWrapperRow();
        WireWrapperRow(row);
        NativeWrappers.Add(row);

        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void RemoveNativeWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;

        UnwireWrapperRow(row);
        NativeWrappers.Remove(row);

        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void MoveNativeWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx <= 0) return;
        NativeWrappers.Move(idx, idx - 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }

    private void MoveNativeWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;
        var idx = NativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= NativeWrappers.Count - 1) return;
        NativeWrappers.Move(idx, idx + 1);
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewText));
    }
    
    // --- Metadata Properties (Temporary Buffer) ---
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private DateTimeOffset? _releaseDate; 
    [ObservableProperty] private PlayStatus _status;

    // --- Launch Config Properties ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsEmulatorMode))]
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(IsNativeMode))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private MediaType _mediaType;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private EmulatorConfig? _selectedEmulatorProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherArgs;
    
    // hard guarantee that PreviewText updates when LauncherArgs changes
    partial void OnLauncherArgsChanged(string? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }
    
    // if typing LauncherPath should also update preview immediately
    partial void OnLauncherPathChanged(string? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }
    
    [ObservableProperty] private string _overrideWatchProcess = string.Empty;

    public bool IsCustomLauncherVisible => IsManualEmulator;
    
    public bool IsNativeMode => MediaType == MediaType.Native;
    
    // --- Asset Management ---
    
    // We bind directly to the item's Assets collection.
    // Since FileService updates the list live, the UI immediately reflects all changes.
    public ObservableCollection<MediaAsset> Assets => _originalItem.Assets;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(DeleteAssetCommand))]
    private MediaAsset? _selectedAsset;

    // --- Commands ---
    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }
    public IRelayCommand DeleteAssetCommand { get; }
    
    public IAsyncRelayCommand BrowseLauncherCommand { get; }
    public IRelayCommand<Window?> SaveAndCloseCommand { get; }
    public IRelayCommand<Window?> CancelAndCloseCommand { get; }


    public IStorageProvider? StorageProvider { get; set; }
    
    public bool HasAssetChanges { get; private set; }

    // --- UI Lists ---
    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();
    public List<MediaType> MediaTypeOptions { get; } = new() { MediaType.Native, MediaType.Emulator };
    public List<PlayStatus> StatusOptions { get; } = Enum.GetValues<PlayStatus>().ToList();

    public IAsyncRelayCommand<Window?> CopyPreviewCommand { get; }
    
    public EditMediaViewModel(
        MediaItem item,
        AppSettings settings,
        FileManagementService fileService,
        List<string> nodePath,
        EmulatorConfig? inheritedEmulator = null,
        ObservableCollection<MediaNode>? rootNodes = null,
        MediaNode? parentNode = null)
    {
        _originalItem = item;
        _fileService = fileService;
        _nodePath = nodePath; 
        _inheritedEmulator = inheritedEmulator;
        
        _rootNodes = rootNodes ?? new ObservableCollection<MediaNode>();
        _parentNode = parentNode;
        _settings = settings;

        // Prefix commands
        GeneratePrefixCommand = new RelayCommand(GeneratePrefix);
        OpenPrefixFolderCommand = new RelayCommand(OpenPrefixFolder, () => HasPrefix);
        ClearPrefixCommand = new RelayCommand(ClearPrefix, () => HasPrefix);
        
        // Primary launch file command
        ChangePrimaryFileCommand = new AsyncRelayCommand(ChangePrimaryFileAsync);
        
        // Environment overrides commands
        AddEnvironmentVariableCommand = new RelayCommand(AddEnvironmentVariable);
        RemoveEnvironmentVariableCommand = new RelayCommand<EnvVarRow?>(RemoveEnvironmentVariable);

        // General commands.
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);
        
        // Generic asset commands
        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
        DeleteAssetCommand = new RelayCommand(DeleteSelectedAsset, () => SelectedAsset != null);

        // Native wrapper editor commands
        AddNativeWrapperCommand = new RelayCommand(AddNativeWrapper);
        RemoveNativeWrapperCommand = new RelayCommand<LaunchWrapperRow?>(RemoveNativeWrapper);
        MoveNativeWrapperUpCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperUp,
            row => row != null && NativeWrappers.IndexOf(row) > 0);

        MoveNativeWrapperDownCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveNativeWrapperDown,
            row => row != null && NativeWrappers.IndexOf(row) >= 0 && NativeWrappers.IndexOf(row) < NativeWrappers.Count - 1);

        CopyPreviewCommand = new AsyncRelayCommand<Window?>(CopyPreviewAsync, CanCopyPreview);
        
        NativeWrappers.CollectionChanged += (_, e) =>
        {
            // Wire/unwire rows on Add/Remove – otherwise Preview would only update when clicking "+ Wrapper"
            if (e.OldItems != null)
                foreach (var oldItem in e.OldItems.OfType<LaunchWrapperRow>())
                    UnwireWrapperRow(oldItem);

            if (e.NewItems != null)
                foreach (var newItem in e.NewItems.OfType<LaunchWrapperRow>())
                    WireWrapperRow(newItem);

            MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
            MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(PreviewText));
        };
        
        // Dialog closes itself (less window manager / modal noise)
        SaveAndCloseCommand = new RelayCommand<Window?>(win =>
        {
            Save();
            win?.Close(true);
        });

        CancelAndCloseCommand = new RelayCommand<Window?>(win =>
        {
            win?.Close(false);
        });
        
        LoadItemData();
        InitializeEmulators(settings);
        InitializeNativeWrapperUiFromItem();

        // Initialize environment overrides from the original item
        EnvironmentOverrides.Clear();
        if (_originalItem.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in _originalItem.EnvironmentOverrides)
            {
                EnvironmentOverrides.Add(new EnvVarRow
                {
                    Key = kv.Key,
                    Value = kv.Value
                });
            }
        }
        
        // After initialization, ensure commands reflect the current list state
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        
        // Ensure preview reflects the current primary file at startup
        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(PreviewText));
    }

    private bool CanCopyPreview(Window? _)
        => !string.IsNullOrWhiteSpace(PreviewText);
    
    private async Task CopyPreviewAsync(Window? win)
    {
        try
        {
            // Window is a TopLevel, so Clipboard is available here.
            var text = PreviewText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return;

            // Optional: trim to keep copy clean (no trailing whitespace)
            text = text.Trim();

            // Strip leading prompt marker ("> ") so the copied command can be
            // pasted directly into a terminal.
            if (text.StartsWith("> ", StringComparison.Ordinal))
            {
                text = text.Substring(2).TrimStart();
            }
            
            if (win?.Clipboard != null)
                await win.Clipboard.SetTextAsync(text);
        }
        catch
        {
            // best-effort: clipboard should never break the dialog
        }
    }
    
    private void LoadItemData()
    {
        // Load metadata into the temporary buffer
        Title = _originalItem.Title;
        Developer = _originalItem.Developer;
        Genre = _originalItem.Genre;
        ReleaseDate = _originalItem.ReleaseDate.HasValue ? new DateTimeOffset(_originalItem.ReleaseDate.Value) : null;
        Status = _originalItem.Status;
        Description = _originalItem.Description;
        MediaType = _originalItem.MediaType;
        
        // Load launch configuration
        LauncherPath = _originalItem.LauncherPath;
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        
        // Prefix
        PrefixPath = _originalItem.PrefixPath ?? string.Empty;
        
        // Arguments: load exactly what is stored on the item
        LauncherArgs = _originalItem.LauncherArgs ?? string.Empty;
        
        // Assets do not need to be loaded separately because we bind directly to _originalItem.Assets
        // The FileService should ensure the assets list is up to date before opening this dialog
        // (via something like RefreshItemAssets)
    }

    partial void OnMediaTypeChanged(MediaType value)
    {
        // When switching to Native, strip any leftover {file} placeholder (friendlier defaults)
        if (value == MediaType.Native)
        {
            if (string.Equals(LauncherArgs?.Trim(), "{file}", StringComparison.Ordinal) ||
                string.Equals(LauncherArgs?.Trim(), "\"{file}\"", StringComparison.Ordinal))
            {
                LauncherArgs = string.Empty;
            }

            // Reset inherited/profile-based emulator selection when switching to native mode
            // This ensures that:
            //  - PreviewText no longer shows emulator-based commands
            //  - Save() does not persist an EmulatorId for native items
            SelectedEmulatorProfile = null;
            
            return;
        }

        // When switching to Emulator and we are in manual-emulator mode (no profile selected),
        // insert a default {file} placeholder if the field is empty
        if (value == MediaType.Emulator && IsManualEmulator)
        {
            if (string.IsNullOrWhiteSpace(LauncherArgs))
                LauncherArgs = "{file}";
        }
    }
    
    private void InitializeEmulators(AppSettings settings)
    {
        AvailableEmulators.Clear();
        AvailableEmulators.Add(new EmulatorConfig { Name = "Custom / Manual", Id = null! });
        
        foreach (var emu in settings.Emulators) 
            AvailableEmulators.Add(emu);

        if (!string.IsNullOrEmpty(_originalItem.EmulatorId))
        {
            SelectedEmulatorProfile = settings.Emulators.FirstOrDefault(e => e.Id == _originalItem.EmulatorId);
        }
        else if (_originalItem.MediaType == MediaType.Native && _inheritedEmulator != null)
        {
            MediaType = MediaType.Emulator;
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == _inheritedEmulator.Id);
        }

        if (SelectedEmulatorProfile == null && MediaType == MediaType.Emulator)
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == null!);
    }

    // --- Asset Actions ---

    private async Task ImportAssetAsync(AssetType type)
    {
        if (StorageProvider == null) return;

        // Create file type filters based on the selected asset type.
        var fileTypes = type switch
        {
            AssetType.Music => new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.sid" } } },
            AssetType.Video => new[] { new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.webm" } } },
            _ => new[] { FilePickerFileTypes.ImageAll } // Default für Cover, Logo, Wallpaper, Marquee
        };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {type}",
            AllowMultiple = true,
            FileTypeFilter = fileTypes
        });

        if (result == null || result.Count == 0) return;

        foreach (var file in result)
        {
            // The FileManagementService handles copying, renaming, and adding the asset to the list.
            var imported = await _fileService.ImportAssetAsync(file.Path.LocalPath, _originalItem, _nodePath, type);
            if (imported != null)
            {
                await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Add(imported));
                HasAssetChanges = true;
            }
        }
    }

    private async void DeleteSelectedAsset()
    {
        if (SelectedAsset == null) return;

        var asset = SelectedAsset;

        // 1) Remove from collection on UI thread (immediate UI feedback)
        await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Remove(asset));

        try
        {
            // 2) Delete file (IO)
            _fileService.DeleteAssetFile(asset);
            HasAssetChanges = true;
            SelectedAsset = null;
        }
        catch
        {
            // 3) Rollback in collection if delete failed
            await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Add(asset));
        }
    }

    // --- Computed Properties & Helpers ---

    public bool IsManualEmulator => IsEmulatorMode &&
                                    (SelectedEmulatorProfile == null || SelectedEmulatorProfile.Id == null);

    public bool IsEmulatorMode => MediaType == MediaType.Emulator;

    public string PreviewText
    {
        get
        {
            // Use the primary launch file (Disc 1 / first entry). If missing, fall back to a sample path
            var primaryPath = _originalItem.GetPrimaryLaunchPath();
            var launchPath = !string.IsNullOrWhiteSpace(primaryPath)
                ? primaryPath
                : "/Games/SuperMario.smc";

            var realFileQuoted = $"\"{launchPath}\"";

            // Resolve effective wrapper chain once (global/emulator/node/item logic)
            var wrappers = ResolveEffectiveNativeWrappersForPreview();

            // --- Emulator via profile ---
            if (MediaType == MediaType.Emulator &&
                SelectedEmulatorProfile != null &&
                SelectedEmulatorProfile.Id != null)
            {
                var baseArgs = SelectedEmulatorProfile.Arguments ?? string.Empty;
                var itemArgs = LauncherArgs ?? string.Empty;

                // Keep the combination logic in sync with LauncherService.CombineTemplateArguments(...)
                var combinedTemplate = CombineTemplateArguments(baseArgs, itemArgs);

                var expandedArgs = ExpandPreviewArguments(combinedTemplate, launchPath);

                // Inner command: emulator binary + expanded args + file
                string inner;
                if (string.IsNullOrWhiteSpace(expandedArgs))
                    inner = $"{SelectedEmulatorProfile.Path} {realFileQuoted}".Trim();
                else
                    inner = $"{SelectedEmulatorProfile.Path} {expandedArgs}".Trim();

                // If there is a wrapper chain, wrap it; otherwise return inner directly
                var final = wrappers.Count > 0
                    ? BuildWrappedCommandLine(inner, wrappers)
                    : inner;

                return $"> {final}".Trim();
            }

            // --- Manual emulator (no profile selected) ---
            if (MediaType == MediaType.Emulator && IsManualEmulator)
            {
                var expandedArgs = ExpandPreviewArguments(LauncherArgs, launchPath);

                string inner;
                if (string.IsNullOrWhiteSpace(LauncherPath))
                {
                    if (string.IsNullOrWhiteSpace(expandedArgs))
                        return string.Empty;

                    inner = expandedArgs;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(expandedArgs))
                        inner = $"{LauncherPath} {realFileQuoted}".Trim();
                    else
                        inner = $"{LauncherPath} {expandedArgs}".Trim();
                }

                var final = wrappers.Count > 0
                    ? BuildWrappedCommandLine(inner, wrappers)
                    : inner;

                return $"> {final}".Trim();
            }

            // --- Native execution (direct or via wrappers) ---
            if (MediaType == MediaType.Native)
            {
                var nativeArgs = BuildNativeArgumentsForPreview(LauncherArgs);

                // Inner command = the real executable + native args
                var inner = string.IsNullOrWhiteSpace(nativeArgs)
                    ? realFileQuoted
                    : $"{realFileQuoted} {nativeArgs}";

                var final = wrappers.Count > 0
                    ? BuildWrappedCommandLine(inner, wrappers)
                    : inner;

                return $"> {final}".Trim();
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Expands preview argument templates using the same placeholder semantics as the runtime launcher:
    /// {file}     -> full path to the launch file (quoted if necessary)
    /// {fileDir}  -> directory of the launch file
    /// {fileName} -> file name with extension
    /// {fileBase} -> file name without extension (e.g. ROM short name for MAME)
    /// </summary>
    private static string ExpandPreviewArguments(string? templateArgs, string launchFilePath)
    {
        var fullPath = string.IsNullOrWhiteSpace(launchFilePath)
            ? string.Empty
            : Path.GetFullPath(launchFilePath);

        var fileDir = string.Empty;
        var fileName = string.Empty;
        var fileBase = string.Empty;

        if (!string.IsNullOrWhiteSpace(fullPath))
        {
            fileDir = Path.GetDirectoryName(fullPath) ?? string.Empty;
            fileName = Path.GetFileName(fullPath);
            fileBase = string.IsNullOrEmpty(fileName)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(fileName);
        }

        if (string.IsNullOrWhiteSpace(templateArgs))
            return string.Empty;

        var result = templateArgs
            .Replace("{fileDir}", fileDir, StringComparison.Ordinal)
            .Replace("{fileName}", fileName, StringComparison.Ordinal)
            .Replace("{fileBase}", fileBase, StringComparison.Ordinal);

        // If the user explicitly wrote "\"{file}\"", preserve that quoting style.
        if (result.Contains("\"{file}\"", StringComparison.Ordinal))
        {
            return result.Replace("{file}", fullPath, StringComparison.Ordinal).Trim();
        }

        var quotedPath = (!string.IsNullOrEmpty(fullPath) && fullPath.Contains(' ', StringComparison.Ordinal))
            ? $"\"{fullPath}\""
            : fullPath;

        return result.Replace("{file}", quotedPath, StringComparison.Ordinal).Trim();
    }
    
    private List<LaunchWrapper> ResolveEffectiveNativeWrappersForPreview()
    {
        // 1) Item-level tri-state (based on current UI state in the dialog)
        //    This reflects unsaved overrides directly from the edit UI.
        switch (NativeWrapperMode)
        {
            case WrapperMode.None:
                // Explicit "no wrappers" for this item.
                return new List<LaunchWrapper>();

            case WrapperMode.Override:
                // Use the item-level override list from the UI (ignoring node/emulator).
                return NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

            case WrapperMode.Inherit:
            default:
                // Fall through to emulator/node chain resolution.
                break;
        }

        // 2) Emulator-level base (matches PlayMediaAsync semantics for MediaType.Native)
        List<LaunchWrapper>? wrappers = null;

        EmulatorConfig? effectiveEmulator = null;

        // 2a) If the item has an explicit emulator assigned, use it.
        if (!string.IsNullOrWhiteSpace(_originalItem.EmulatorId))
        {
            effectiveEmulator = _settings.Emulators
                .FirstOrDefault(e => e.Id == _originalItem.EmulatorId);
        }
        else if (_parentNode != null)
        {
            // 2b) Otherwise traverse node chain upwards to find a DefaultEmulatorId.
            var chain = GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf first (nearest wins)

            foreach (var node in chain)
            {
                if (string.IsNullOrWhiteSpace(node.DefaultEmulatorId))
                    continue;

                effectiveEmulator = _settings.Emulators
                    .FirstOrDefault(e => e.Id == node.DefaultEmulatorId);

                if (effectiveEmulator != null)
                    break;
            }
        }

        if (effectiveEmulator != null)
        {
            switch (effectiveEmulator.NativeWrapperMode)
            {
                case EmulatorConfig.WrapperMode.Inherit:
                    // Inherit from global defaults (may be null).
                    wrappers = _settings.DefaultNativeWrappers;
                    break;

                case EmulatorConfig.WrapperMode.None:
                    // Explicitly no wrappers for this emulator (unless item overrides, which it doesn't in Inherit mode).
                    wrappers = new List<LaunchWrapper>();
                    break;

                case EmulatorConfig.WrapperMode.Override:
                    // Use emulator-level override list (may be empty to mean "none").
                    wrappers = effectiveEmulator.NativeWrappersOverride != null
                        ? new List<LaunchWrapper>(effectiveEmulator.NativeWrappersOverride)
                        : new List<LaunchWrapper>();
                    break;
            }
        }
        else
        {
            // No emulator: start with global defaults only.
            wrappers = _settings.DefaultNativeWrappers;
        }

        // 3) Node-level inheritance (nearest override wins, tri-state via null/empty/non-empty).
        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse(); // Leaf (parent) first

            foreach (var node in chain)
            {
                if (node.NativeWrappersOverride == null)
                {
                    // Inherit -> nothing to do here, continue upwards.
                    continue;
                }

                // Empty list => explicit "none" at node level.
                // Non-empty   => node-level override.
                wrappers = node.NativeWrappersOverride;
                break;
            }
        }

        // 4) Final normalization: return a concrete list (never null).
        return wrappers != null
            ? wrappers.ToList()
            : new List<LaunchWrapper>();
    }

    private static string BuildWrappedCommandLine(string innerCommand, IReadOnlyList<LaunchWrapper> wrappers)
    {
        var current = innerCommand;

        foreach (var wrapper in wrappers)
        {
            if (string.IsNullOrWhiteSpace(wrapper.Path))
                continue;

            var templateArgs = string.IsNullOrWhiteSpace(wrapper.Args) ? "{file}" : wrapper.Args;

            var expandedArgs = templateArgs.Contains("{file}", StringComparison.Ordinal)
                ? templateArgs.Replace("{file}", current, StringComparison.Ordinal)
                : $"{templateArgs} {current}";

            current = $"{wrapper.Path} {expandedArgs}".Trim();
        }

        return NormalizeWhitespace(current);
    }

    private static List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
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
    
    private static string CombineTemplateArguments(string? baseArgs, string? itemArgs)
    {
        baseArgs ??= string.Empty;
        itemArgs ??= string.Empty;

        if (string.IsNullOrWhiteSpace(itemArgs))
            return baseArgs;

        // Matches LauncherService.CombineTemplateArguments(...)
        if (baseArgs.Contains("{file}", StringComparison.Ordinal) &&
            itemArgs.Contains("{file}", StringComparison.Ordinal))
        {
            return baseArgs.Replace("{file}", itemArgs, StringComparison.Ordinal);
        }

        return $"{baseArgs} {itemArgs}".Trim();
    }

    private static string BuildNativeArgumentsForPreview(string? templateArgs)
    {
        if (string.IsNullOrWhiteSpace(templateArgs))
            return string.Empty;

        var args = templateArgs;

        // DAU-Rule:
        // If user types "{file}" in native args, treat it as a leftover from emulator templates.
        // Keep only what comes AFTER {file} (so "prefix {file} --arg" does NOT show "prefix").
        var idxQuoted = args.IndexOf("\"{file}\"", StringComparison.Ordinal);
        if (idxQuoted >= 0)
        {
            args = args[(idxQuoted + "\"{file}\"".Length)..];
        }
        else
        {
            var idx = args.IndexOf("{file}", StringComparison.Ordinal);
            if (idx >= 0)
                args = args[(idx + "{file}".Length)..];
        }

        return NormalizeWhitespace(args);
    }
    
    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        Span<char> buffer = stackalloc char[value.Length];
        int w = 0;
        bool lastWasSpace = false;

        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (lastWasSpace) continue;
                buffer[w++] = ' ';
                lastWasSpace = true;
            }
            else
            {
                buffer[w++] = c;
                lastWasSpace = false;
            }
        }

        int start = 0;
        int length = w;

        if (length > 0 && buffer[0] == ' ')
        {
            start++;
            length--;
        }
        if (length > 0 && buffer[start + length - 1] == ' ')
        {
            length--;
        }

        return length <= 0 ? string.Empty : new string(buffer.Slice(start, length));
    }
    
    private async Task ChangePrimaryFileAsync()
    {
        if (StorageProvider == null)
            return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select launch file (main executable / Disc 1)",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                // Generic filter: let the user pick anything; launch semantics are defined elsewhere
                FilePickerFileTypes.All
            }
        });

        var file = result?.FirstOrDefault();
        if (file == null)
            return;

        var path = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Decide how to store the launch file path based on global settings:
        // - PreferPortableLaunchPaths == false (default): store absolute paths (classic behavior)
        // - PreferPortableLaunchPaths == true: store a DataRoot-relative path so Retromind + Games
        //   can be moved together as a portable bundle
        string storedPath;
        MediaFileKind storedKind;

        if (_settings.PreferPortableLaunchPaths)
        {
            storedPath = AppPaths.MakeDataRelative(path);
            storedKind = MediaFileKind.LibraryRelative;
        }
        else
        {
            storedPath = path;
            storedKind = MediaFileKind.Absolute;
        }
        
        // Update the primary file in the item's Files list
        // If there is no file yet, add a new entry
        var primary = _originalItem.GetPrimaryFile();
        if (primary == null)
        {
            primary = new MediaFileRef
            {
                Kind = storedKind,
                Path = storedPath,
                Index = 1
            };
            var list = _originalItem.Files ?? new List<MediaFileRef>();
            list.Add(primary);
            _originalItem.Files = list;
        }
        else
        {
            primary.Path = storedPath;
            primary.Kind = storedKind;
        }

        // Notify UI about the change:
        // - display path
        // - preview command line (uses GetPrimaryLaunchPath())
        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }
    
    private async Task BrowseLauncherAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Launcher Executable",
            AllowMultiple = false
        });

        if (result != null && result.Count > 0) LauncherPath = result[0].Path.LocalPath;
    }

    private void Save()
    {
        // 1. Write metadata back to the original item
        _originalItem.Title = Title;
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.MediaType = MediaType;

        // Prefix: store null when not used
        _originalItem.PrefixPath = string.IsNullOrWhiteSpace(PrefixPath) ? null : PrefixPath.Trim();
        
        if (MediaType == MediaType.Emulator &&
            SelectedEmulatorProfile != null &&
            SelectedEmulatorProfile.Id != null)
        {
            // Only emulator items are allowed to persist an EmulatorId
            _originalItem.EmulatorId = SelectedEmulatorProfile.Id;
        }
        else
        {
            _originalItem.EmulatorId = null;

            // Manual emulator only: LauncherPath is the tool/emulator path
            if (MediaType == MediaType.Emulator)
            {
                _originalItem.LauncherPath = LauncherPath;
                _originalItem.LauncherArgs = LauncherArgs;
            }
            else
            {
                // Native: LauncherPath is deprecated (wrapper chain handles launching)
                _originalItem.LauncherPath = null;
                _originalItem.LauncherArgs = LauncherArgs;
            }

            _originalItem.OverrideWatchProcess = OverrideWatchProcess;
        }
        
        // 3. Native wrapper override (tri-state, item-level)
        switch (NativeWrapperMode)
        {
            case WrapperMode.Inherit:
                _originalItem.NativeWrappersOverride = null;
                break;

            case WrapperMode.None:
                _originalItem.NativeWrappersOverride = new List<LaunchWrapper>();
                break;

            case WrapperMode.Override:
                _originalItem.NativeWrappersOverride = NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();
                break;
        }
        
        // 4. Environment overrides: sync back into the model dictionary
        _originalItem.EnvironmentOverrides.Clear();
        foreach (var row in EnvironmentOverrides)
        {
            if (string.IsNullOrWhiteSpace(row.Key))
                continue;

            _originalItem.EnvironmentOverrides[row.Key.Trim()] = row.Value ?? string.Empty;
        }
    }
}