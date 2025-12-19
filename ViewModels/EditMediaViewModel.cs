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

    private void GeneratePrefix()
    {
        // Only generate if not already set (user might have a custom path).
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
        // Wichtig: alte Rows abhängen (falls die VM wiederverwendet wird)
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
        // Sobald Path/Args geändert werden: Preview aktualisieren (DAU erwartet "live")
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
    
    // Wir binden direkt an die Collection des Items. 
    // Da FileService die Liste live aktualisiert, sieht die UI sofort Änderungen.
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

        // Prefix commands
        GeneratePrefixCommand = new RelayCommand(GeneratePrefix);
        OpenPrefixFolderCommand = new RelayCommand(OpenPrefixFolder, () => HasPrefix);
        ClearPrefixCommand = new RelayCommand(ClearPrefix, () => HasPrefix);
        
        // Commands initialisieren
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);
        
        // Generische Asset-Commands
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
            // Rows wiring/unwiring (Add/Remove) – sonst updated Preview nur beim "+ Wrapper"
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
        
        // Dialog schließt sich selbst (reduziert WM/Modal "pop")
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

        // nach Initialisierung auch einmal aktualisieren
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
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
        // Metadaten laden (Puffer)
        Title = _originalItem.Title;
        Developer = _originalItem.Developer;
        Genre = _originalItem.Genre;
        ReleaseDate = _originalItem.ReleaseDate.HasValue ? new DateTimeOffset(_originalItem.ReleaseDate.Value) : null;
        Status = _originalItem.Status;
        Description = _originalItem.Description;
        MediaType = _originalItem.MediaType;
        
        // Launch Config laden
        LauncherPath = _originalItem.LauncherPath;
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        
        // Prefix
        PrefixPath = _originalItem.PrefixPath ?? string.Empty;
        
        // Native: no {file} placeholder needed anymore
        if (MediaType == MediaType.Native)
        {
            LauncherArgs = _originalItem.LauncherArgs ?? string.Empty;
        }
        else
        {
            LauncherArgs = string.IsNullOrWhiteSpace(_originalItem.LauncherArgs) ? "{file}" : _originalItem.LauncherArgs;
        }
        
        // Assets müssen nicht geladen werden, da wir direkt auf _originalItem.Assets zugreifen
        // Der FileService sollte idealerweise vor dem Öffnen dieses Dialogs sicherstellen,
        // dass die Assets-Liste aktuell ist (via RefreshItemAssets).
    }

    partial void OnMediaTypeChanged(MediaType value)
    {
        // Wenn der User zu Native wechselt: {file} entfernen (DAU-freundlich)
        if (value == MediaType.Native)
        {
            if (string.Equals(LauncherArgs?.Trim(), "{file}", StringComparison.Ordinal) ||
                string.Equals(LauncherArgs?.Trim(), "\"{file}\"", StringComparison.Ordinal))
            {
                LauncherArgs = string.Empty;
            }

            return;
        }

        // Wenn der User zu Emulator wechselt und Args leer sind: {file} setzen
        if (value == MediaType.Emulator)
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

        // Filter basierend auf AssetType erstellen
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
            // Der Service übernimmt das Kopieren, Umbenennen und Hinzufügen zur Liste
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
            // Use the primary launch file (Disc 1 / first entry). If missing, fall back to a sample path.
            var primaryPath = _originalItem.GetPrimaryLaunchPath();
            var realFile = !string.IsNullOrWhiteSpace(primaryPath)
                ? $"\"{primaryPath}\""
                : "\"/Games/SuperMario.smc\"";

            if (SelectedEmulatorProfile != null && SelectedEmulatorProfile.Id != null)
            {
                // ... existing code (Emulator Profile preview bleibt) ...
                var baseArgs = SelectedEmulatorProfile.Arguments ?? "";
                var itemArgs = LauncherArgs ?? "";

                string combinedArgs;

                if (!string.IsNullOrWhiteSpace(itemArgs))
                {
                    if (baseArgs.Contains("{file}") && itemArgs.Contains("{file}"))
                    {
                        combinedArgs = baseArgs.Replace("{file}", itemArgs);
                    }
                    else
                    {
                        combinedArgs = $"{baseArgs} {itemArgs}".Trim();
                    }
                }
                else
                {
                    combinedArgs = baseArgs;
                }

                if (string.IsNullOrWhiteSpace(combinedArgs))
                    return $"> {SelectedEmulatorProfile.Path} {realFile}";

                if (combinedArgs.Contains("{file}"))
                    return $"> {SelectedEmulatorProfile.Path} {combinedArgs.Replace("{file}", realFile)}";

                return $"> {SelectedEmulatorProfile.Path} {combinedArgs} {realFile}";
            }

            if (MediaType == MediaType.Emulator && IsManualEmulator)
            {
                var args = string.IsNullOrWhiteSpace(LauncherArgs)
                    ? realFile
                    : LauncherArgs?.Replace("{file}", realFile) ?? realFile;

                return $"> {LauncherPath} {args}".Trim();
            }

            if (MediaType == MediaType.Native)
            {
                var wrappers = ResolveEffectiveNativeWrappersForPreview();
                var nativeArgs = BuildNativeArgumentsForPreview(LauncherArgs);

                // Inner command = the real executable + native args
                var inner = string.IsNullOrWhiteSpace(nativeArgs)
                    ? realFile
                    : $"{realFile} {nativeArgs}";

                var final = BuildWrappedCommandLine(inner, wrappers);
                return $"> {final}".Trim();
            }

            return "";
        }
    }

    private List<LaunchWrapper> ResolveEffectiveNativeWrappersForPreview()
    {
        // 1) Item-level tri-state (based on current UI state)
        switch (NativeWrapperMode)
        {
            case WrapperMode.None:
                return new List<LaunchWrapper>(); // explicit "no wrappers"

            case WrapperMode.Override:
                return NativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

            case WrapperMode.Inherit:
            default:
                break;
        }

        // 2) Node-level inheritance (nearest override wins)
        if (_parentNode == null || _rootNodes.Count == 0)
            return new List<LaunchWrapper>();

        var chain = GetNodeChain(_parentNode, _rootNodes);

        // nearest wins => walk from leaf to root
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            if (node.NativeWrappersOverride == null)
                continue; // inherit

            // empty => none; non-empty => override
            return node.NativeWrappersOverride.ToList();
        }

        // 3) Global defaults (noch nicht im Edit-Dialog verfügbar) => none for now
        return new List<LaunchWrapper>();
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
        // 1. Metadaten zurückschreiben
        _originalItem.Title = Title;
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.MediaType = MediaType;

        // Prefix: store null when not used
        _originalItem.PrefixPath = string.IsNullOrWhiteSpace(PrefixPath) ? null : PrefixPath.Trim();
        
        // 2. Launch Config zurückschreiben
        if (SelectedEmulatorProfile != null && SelectedEmulatorProfile.Id != null)
        {
            _originalItem.EmulatorId = SelectedEmulatorProfile.Id;
        }
        else
        {
            _originalItem.EmulatorId = null;

            // Manual emulator only: LauncherPath is the tool/emulator path.
            if (MediaType == MediaType.Emulator)
            {
                _originalItem.LauncherPath = LauncherPath;
                _originalItem.LauncherArgs = LauncherArgs;
            }
            else
            {
                // Native: LauncherPath is deprecated (wrapper chain handles launching).
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
    }
}