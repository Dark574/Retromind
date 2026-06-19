using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel : ViewModelBase, IDisposable
{
    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    private readonly EmulatorConfig? _inheritedEmulator;
    private EmulatorConfig? _resolvedInheritedEmulator;
    private string? _resolvedInheritedEmulatorSource;
    private readonly MediaItem _originalItem;
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;
    private NotifyCollectionChangedEventHandler? _assetsChangedHandler;
    
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private readonly MediaNode? _parentNode;
    private readonly MetadataSuggestionService _metadataSuggestionService;
    
    // Keep a reference to global settings so preview can resolve emulator profiles
    // and default native wrappers in the same way as the runtime launcher.
    private readonly AppSettings _settings;

    // --- Prefix (Wine/Proton/UMU) ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrefix))]
    [NotifyCanExecuteChangedFor(nameof(OpenPrefixFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearPrefixCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunWinetricksCommand))]
    [NotifyPropertyChangedFor(nameof(ShowWineArchExistingPrefixWarning))]
    private string _prefixPath = string.Empty;

    public bool HasPrefix => !string.IsNullOrWhiteSpace(PrefixPath);

    public IRelayCommand GeneratePrefixCommand { get; }
    public IRelayCommand OpenPrefixFolderCommand { get; }
    public IRelayCommand ClearPrefixCommand { get; }
    public IAsyncRelayCommand<Window?> RunWinetricksCommand { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunWinetricksCommand))]
    private string _winetricksVerbs = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunWinetricksCommand))]
    private bool _isWinetricksRunning;

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
        [ObservableProperty] private string _source = string.Empty;
        [ObservableProperty] private bool _isInherited;
    }

    public sealed partial class CustomFieldRow : ObservableObject
    {
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private string _value = string.Empty;
    }

    public sealed class EmulatorProfileOption
    {
        public enum OptionKind
        {
            Inherit,
            Native,
            Manual,
            Emulator
        }

        public EmulatorProfileOption(OptionKind kind, string name, EmulatorConfig? emulator = null)
        {
            Kind = kind;
            Name = name ?? string.Empty;
            Emulator = emulator;
        }

        public OptionKind Kind { get; }
        public string Name { get; }
        public EmulatorConfig? Emulator { get; }
    }

    public sealed class RunnerVersionOption
    {
        public RunnerVersionOption(string? id, string name, RunnerVersionKind? kind = null)
        {
            Id = id;
            Name = name ?? string.Empty;
            Kind = kind;
        }

        public string? Id { get; }
        public string Name { get; }
        public RunnerVersionKind? Kind { get; }
    }

    /// <summary>
    /// Editable list of per-item environment overrides.
    /// </summary>
    public ObservableCollection<EnvVarRow> EnvironmentOverrides { get; } = new();
    public ObservableCollection<CustomFieldRow> CustomFields { get; } = new();
    public ObservableCollection<RunnerVersionOption> AvailableRunnerVersions { get; } = new();

    public IRelayCommand AddEnvironmentVariableCommand { get; }
    public IRelayCommand<EnvVarRow?> RemoveEnvironmentVariableCommand { get; }
    public IRelayCommand AddCustomFieldCommand { get; }
    public IRelayCommand<CustomFieldRow?> RemoveCustomFieldCommand { get; }
    public IRelayCommand<string?> AcceptMetadataSuggestionCommand { get; }

    public enum WineArchOption
    {
        Auto,
        Win64,
        Win32
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWineArchOverrideActive))]
    [NotifyPropertyChangedFor(nameof(ShowWineArchExistingPrefixWarning))]
    private WineArchOption _wineArchSelection = WineArchOption.Auto;

    public List<WineArchOption> WineArchOptions { get; } =
        new() { WineArchOption.Auto, WineArchOption.Win64, WineArchOption.Win32 };

    public bool IsWineArchOverrideActive => WineArchSelection != WineArchOption.Auto;

    public bool ShowWineArchExistingPrefixWarning => HasPrefix && IsWineArchOverrideActive;

    public string RunnerVersionLabel => T("EditMedia_RunnerVersionLabel", "Wine/Proton version");
    public string RunnerVersionHint => T("EditMedia_RunnerVersionHint", "Optional per-item override. Takes precedence over emulator default.");
    public bool HasInheritedRunnerVersionInfo => !string.IsNullOrWhiteSpace(InheritedRunnerVersionInfo);
    public string InheritedRunnerVersionInfo => _inheritedRunnerVersionInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private RunnerVersionOption? _selectedRunnerVersion;

    private string _inheritedRunnerVersionInfo = string.Empty;

    // --- Metadata Properties (Temporary Buffer) ---
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AssetFilePrefix))]
    [NotifyPropertyChangedFor(nameof(AssetFileExample))]
    private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeveloperSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptDeveloperSuggestion))]
    private string? _developer;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublisherSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptPublisherSuggestion))]
    private string? _publisher;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptPlatformSuggestion))]
    private string? _platform;
    [ObservableProperty] private string? _source;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenreSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptGenreSuggestion))]
    private string? _genre;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptSeriesSuggestion))]
    private string? _series;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseTypeSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptReleaseTypeSuggestion))]
    private string? _releaseType;
    [ObservableProperty] private string? _sortTitle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayModeSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptPlayModeSuggestion))]
    private string? _playMode;
    [ObservableProperty] private string? _maxPlayers;
    [ObservableProperty] private DateTimeOffset? _releaseDate; 
    [ObservableProperty] private PlayStatus _status;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeveloperSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptDeveloperSuggestion))]
    private string _developerSuggestion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublisherSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptPublisherSuggestion))]
    private string _publisherSuggestion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenreSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptGenreSuggestion))]
    private string _genreSuggestion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptPlatformSuggestion))]
    private string _platformSuggestion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeriesSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptSeriesSuggestion))]
    private string _seriesSuggestion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReleaseTypeSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptReleaseTypeSuggestion))]
    private string _releaseTypeSuggestion = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayModeSuggestionSuffix))]
    [NotifyPropertyChangedFor(nameof(CanAcceptPlayModeSuggestion))]
    private string _playModeSuggestion = string.Empty;

    // --- Launch Config Properties ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsEmulatorMode))]
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(IsNativeMode))]
    [NotifyPropertyChangedFor(nameof(IsWrapperEditorVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private MediaType _mediaType;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private EmulatorProfileOption? _selectedEmulatorProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomLauncherVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherArgs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _workingDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgConfigPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgDataPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgCachePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string _xdgStatePath = string.Empty;

    [ObservableProperty]
    private string _xdgBasePath = string.Empty;
    
    /// <summary>
    /// Ensures that switching from "manual emulator" defaults to a proper
    /// emulator profile does not accidentally shadow the profile's own
    /// default arguments. If the current per-item arguments are still
    /// trivial (empty / "{file}"), they are cleared so the profile template
    /// can act alone.
    /// </summary>
    partial void OnSelectedEmulatorProfileChanged(EmulatorProfileOption? value)
    {
        if (value == null)
            return;

        switch (value.Kind)
        {
            case EmulatorProfileOption.OptionKind.Native:
                MediaType = MediaType.Native;
                break;

            case EmulatorProfileOption.OptionKind.Inherit:
            case EmulatorProfileOption.OptionKind.Manual:
            case EmulatorProfileOption.OptionKind.Emulator:
                MediaType = MediaType.Emulator;
                break;
        }

        if (value.Kind == EmulatorProfileOption.OptionKind.Emulator &&
            IsTrivialLauncherArgs(LauncherArgs))
        {
            LauncherArgs = string.Empty;
        }

        if (value.Kind == EmulatorProfileOption.OptionKind.Inherit &&
            ResolveInheritedEmulator() != null &&
            IsTrivialLauncherArgs(LauncherArgs))
        {
            LauncherArgs = string.Empty;
        }

        if (value.Kind == EmulatorProfileOption.OptionKind.Manual &&
            string.IsNullOrWhiteSpace(LauncherArgs))
        {
            LauncherArgs = "{file}";
        }

        // Keep preview and "Copy" button state in sync with the new profile
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
        RefreshInheritedRunnerVersionInfo();

        RefreshInheritedWrappers();
        RebuildEnvironmentOverridesFromInheritance(CaptureCurrentEnvironmentOverrides());
    }
    
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

    partial void OnSelectedRunnerVersionChanged(RunnerVersionOption? value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
        RefreshInheritedRunnerVersionInfo();
    }

    partial void OnWorkingDirectoryChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(EffectiveWorkingDirectory));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgConfigPathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgDataPathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgCachePathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgStatePathChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
        CopyPreviewCommand.NotifyCanExecuteChanged();
    }

    partial void OnXdgBasePathChanged(string value)
    {
        ApplyXdgBaseCommand.NotifyCanExecuteChanged();
    }

    partial void OnTitleChanged(string value)
    {
        CopyAssetPrefixCommand.NotifyCanExecuteChanged();
    }

    partial void OnDeveloperChanged(string? value)
    {
        RefreshDeveloperSuggestion();
    }

    partial void OnPublisherChanged(string? value)
    {
        RefreshPublisherSuggestion();
    }

    partial void OnGenreChanged(string? value)
    {
        RefreshGenreSuggestion();
    }

    partial void OnPlatformChanged(string? value)
    {
        RefreshPlatformSuggestion();
    }

    partial void OnSeriesChanged(string? value)
    {
        RefreshSeriesSuggestion();
    }

    partial void OnReleaseTypeChanged(string? value)
    {
        RefreshReleaseTypeSuggestion();
    }

    partial void OnPlayModeChanged(string? value)
    {
        RefreshPlayModeSuggestion();
    }
    
    [ObservableProperty] private string _overrideWatchProcess = string.Empty;

    public bool IsCustomLauncherVisible => IsManualEmulator;
    
    public bool IsNativeMode => MediaType == MediaType.Native;
    
    /// <summary>
    /// Controls visibility of the wrapper editor section in the UI.
    /// Wrappers are meaningful for both Native and Emulator items,
    /// but not for pure Command-type entries.
    /// </summary>
    public bool IsWrapperEditorVisible =>
        MediaType == MediaType.Native ||
        MediaType == MediaType.Emulator;
    
    // --- Asset Management ---
    
    // We bind directly to the item's Assets collection.
    // Since FileService updates the list live, the UI immediately reflects all changes.
    public ObservableCollection<MediaAsset> Assets => _originalItem.Assets;

    public string AssetFilePrefix => FileManagementService.BuildItemAssetPrefix(Title, _originalItem.Id);

    public string AssetFileExample => string.Format(Strings.EditMedia_AssetsPrefixExampleFormat, AssetFilePrefix);
    public string DeveloperSuggestionSuffix => GetSuggestionSuffix(Developer, DeveloperSuggestion);
    public bool CanAcceptDeveloperSuggestion => !string.IsNullOrEmpty(DeveloperSuggestionSuffix);
    public string PublisherSuggestionSuffix => GetSuggestionSuffix(Publisher, PublisherSuggestion);
    public bool CanAcceptPublisherSuggestion => !string.IsNullOrEmpty(PublisherSuggestionSuffix);
    public string GenreSuggestionSuffix => GetSuggestionSuffix(Genre, GenreSuggestion);
    public bool CanAcceptGenreSuggestion => !string.IsNullOrEmpty(GenreSuggestionSuffix);
    public string PlatformSuggestionSuffix => GetSuggestionSuffix(Platform, PlatformSuggestion);
    public bool CanAcceptPlatformSuggestion => !string.IsNullOrEmpty(PlatformSuggestionSuffix);
    public string SeriesSuggestionSuffix => GetSuggestionSuffix(Series, SeriesSuggestion);
    public bool CanAcceptSeriesSuggestion => !string.IsNullOrEmpty(SeriesSuggestionSuffix);
    public string ReleaseTypeSuggestionSuffix => GetSuggestionSuffix(ReleaseType, ReleaseTypeSuggestion);
    public bool CanAcceptReleaseTypeSuggestion => !string.IsNullOrEmpty(ReleaseTypeSuggestionSuffix);
    public string PlayModeSuggestionSuffix => GetSuggestionSuffix(PlayMode, PlayModeSuggestion);
    public bool CanAcceptPlayModeSuggestion => !string.IsNullOrEmpty(PlayModeSuggestionSuffix);

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(DeleteAssetCommand))]
    private MediaAsset? _selectedAsset;

    // --- Commands ---
    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }
    public IAsyncRelayCommand DeleteAssetCommand { get; }
    
    public IAsyncRelayCommand BrowseLauncherCommand { get; }
    public IAsyncRelayCommand BrowseWorkingDirectoryCommand { get; }
    public IAsyncRelayCommand BrowseXdgConfigCommand { get; }
    public IAsyncRelayCommand BrowseXdgDataCommand { get; }
    public IAsyncRelayCommand BrowseXdgCacheCommand { get; }
    public IAsyncRelayCommand BrowseXdgStateCommand { get; }
    public IAsyncRelayCommand BrowseXdgBaseCommand { get; }
    public IRelayCommand ApplyXdgBaseCommand { get; }
    public IRelayCommand ApplyPortableXdgPresetCommand { get; }
    public IRelayCommand ApplyPortableXdgAndHomePresetCommand { get; }
    public IRelayCommand<Window?> SaveAndCloseCommand { get; }
    public IRelayCommand<Window?> CancelAndCloseCommand { get; }


    public IStorageProvider? StorageProvider { get; set; }
    
    public bool HasAssetChanges { get; private set; }

    // --- UI Lists ---
    public ObservableCollection<EmulatorProfileOption> AvailableEmulators { get; } = new();
    public List<PlayStatus> StatusOptions { get; } = Enum.GetValues<PlayStatus>().ToList();

    public IAsyncRelayCommand<Window?> CopyPreviewCommand { get; }
    public IAsyncRelayCommand<Window?> CopyAssetPrefixCommand { get; }
    
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
        _metadataSuggestionService = new MetadataSuggestionService(_rootNodes);
        _assetsChangedHandler = (_, _) => ScheduleSortAssets();
        _originalItem.Assets.CollectionChanged += _assetsChangedHandler;

        // Prefix commands
        GeneratePrefixCommand = new RelayCommand(GeneratePrefix);
        OpenPrefixFolderCommand = new RelayCommand(OpenPrefixFolder, () => HasPrefix);
        ClearPrefixCommand = new RelayCommand(ClearPrefix, () => HasPrefix);
        RunWinetricksCommand = new AsyncRelayCommand<Window?>(RunWinetricksAsync, CanRunWinetricks);
        
        // Primary launch file command
        ChangePrimaryFileCommand = new AsyncRelayCommand(ChangePrimaryFileAsync);
        
        // Environment overrides commands
        AddEnvironmentVariableCommand = new RelayCommand(AddEnvironmentVariable);
        RemoveEnvironmentVariableCommand = new RelayCommand<EnvVarRow?>(RemoveEnvironmentVariable);
        AddCustomFieldCommand = new RelayCommand(AddCustomField);
        RemoveCustomFieldCommand = new RelayCommand<CustomFieldRow?>(RemoveCustomField);
        AcceptMetadataSuggestionCommand = new RelayCommand<string?>(
            fieldKey => TryAcceptMetadataSuggestion(fieldKey ?? string.Empty),
            fieldKey => CanAcceptMetadataSuggestion(fieldKey ?? string.Empty));

        // General commands.
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);
        BrowseWorkingDirectoryCommand = new AsyncRelayCommand(BrowseWorkingDirectoryAsync);
        BrowseXdgConfigCommand = new AsyncRelayCommand(BrowseXdgConfigAsync);
        BrowseXdgDataCommand = new AsyncRelayCommand(BrowseXdgDataAsync);
        BrowseXdgCacheCommand = new AsyncRelayCommand(BrowseXdgCacheAsync);
        BrowseXdgStateCommand = new AsyncRelayCommand(BrowseXdgStateAsync);
        BrowseXdgBaseCommand = new AsyncRelayCommand(BrowseXdgBaseAsync);
        ApplyXdgBaseCommand = new RelayCommand(ApplyXdgBase, CanApplyXdgBase);
        ApplyPortableXdgPresetCommand = new RelayCommand(ApplyPortableXdgPreset);
        ApplyPortableXdgAndHomePresetCommand = new RelayCommand(ApplyPortableXdgAndHomePreset);
        
        // Generic asset commands
        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
        DeleteAssetCommand = new AsyncRelayCommand(DeleteSelectedAssetAsync, () => SelectedAsset != null);

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
        CopyAssetPrefixCommand = new AsyncRelayCommand<Window?>(CopyAssetPrefixAsync, CanCopyAssetPrefix);
        
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
        
        // Keep environment overrides in sync with the preview. Whenever rows
        // are added/removed, we attach/detach change tracking so that edits
        // to Key/Value immediately refresh the preview prefix.
        EnvironmentOverrides.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (var oldItem in e.OldItems.OfType<EnvVarRow>())
                    UnwireEnvRow(oldItem);

            if (e.NewItems != null)
                foreach (var newItem in e.NewItems.OfType<EnvVarRow>())
                    WireEnvRow(newItem);

            OnPropertyChanged(nameof(PreviewText));
            CopyPreviewCommand.NotifyCanExecuteChanged();
        };
        
        // Dialog closes itself (less window manager / modal noise)
        SaveAndCloseCommand = new RelayCommand<Window?>(win =>
        {
            try
            {
                Save();
            }
            finally
            {
                DetachAssetHandlers();
                win?.Close(true);
            }
        });

        CancelAndCloseCommand = new RelayCommand<Window?>(win =>
        {
            DetachAssetHandlers();
            win?.Close(false);
        });
        
        LoadItemData();
        InitializeEmulators(settings);
        InitializeRunnerVersions(settings);
        InitializeNativeWrapperUiFromItem();
        RefreshInheritedWrappers();
        InitializeEnvironmentOverridesFromItem();
        
        // After initialization, ensure commands reflect the current list state
        MoveNativeWrapperUpCommand.NotifyCanExecuteChanged();
        MoveNativeWrapperDownCommand.NotifyCanExecuteChanged();
        
        // Ensure preview reflects the current primary file at startup
        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(PreviewText));

        SortAssets();
    }

    private bool CanCopyPreview(Window? _)
        => !string.IsNullOrWhiteSpace(PreviewText);

    private bool CanCopyAssetPrefix(Window? _)
        => !string.IsNullOrWhiteSpace(AssetFilePrefix);
    
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

    private async Task CopyAssetPrefixAsync(Window? win)
    {
        try
        {
            var text = AssetFilePrefix ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (win?.Clipboard != null)
                await win.Clipboard.SetTextAsync(text.Trim());
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
        Publisher = _originalItem.Publisher;
        Platform = _originalItem.Platform;
        Source = _originalItem.Source;
        Developer = _originalItem.Developer;
        Genre = _originalItem.Genre;
        Series = _originalItem.Series;
        ReleaseType = _originalItem.ReleaseType;
        SortTitle = _originalItem.SortTitle;
        PlayMode = _originalItem.PlayMode;
        MaxPlayers = _originalItem.MaxPlayers;
        ReleaseDate = _originalItem.ReleaseDate.HasValue ? new DateTimeOffset(_originalItem.ReleaseDate.Value) : null;
        Status = _originalItem.Status;
        Description = _originalItem.Description;
        MediaType = _originalItem.MediaType;
        InitializeCustomFieldsFromItem();
        
        // Load launch configuration
        LauncherPath = _originalItem.LauncherPath;
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        WorkingDirectory = _originalItem.WorkingDirectory ?? string.Empty;
        XdgConfigPath = _originalItem.XdgConfigPath ?? string.Empty;
        XdgDataPath = _originalItem.XdgDataPath ?? string.Empty;
        XdgCachePath = _originalItem.XdgCachePath ?? string.Empty;
        XdgStatePath = _originalItem.XdgStatePath ?? string.Empty;
        XdgBasePath = _originalItem.XdgBasePath ?? string.Empty;
        
        // Prefix
        PrefixPath = _originalItem.PrefixPath ?? string.Empty;
        WineArchSelection = ResolveWineArchSelection(_originalItem.WineArchOverride, _originalItem.EnvironmentOverrides);
        
        // Arguments: load exactly what is stored on the item
        LauncherArgs = _originalItem.LauncherArgs ?? string.Empty;
        
        // Assets do not need to be loaded separately because we bind directly to _originalItem.Assets
        // The FileService should ensure the assets list is up to date before opening this dialog
        // (via something like RefreshItemAssets)
    }

    private void InitializeCustomFieldsFromItem()
    {
        CustomFields.Clear();

        if (_originalItem.CustomFields == null || _originalItem.CustomFields.Count == 0)
            return;

        foreach (var kv in _originalItem.CustomFields.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            CustomFields.Add(new CustomFieldRow
            {
                Key = kv.Key,
                Value = kv.Value
            });
        }
    }

    public bool TryAcceptMetadataSuggestion(string fieldKey)
    {
        switch (fieldKey)
        {
            case MetadataSuggestionService.DeveloperField:
                if (!CanAcceptDeveloperSuggestion)
                    return false;
                Developer = DeveloperSuggestion;
                return true;

            case MetadataSuggestionService.PublisherField:
                if (!CanAcceptPublisherSuggestion)
                    return false;
                Publisher = PublisherSuggestion;
                return true;

            case MetadataSuggestionService.GenreField:
                if (!CanAcceptGenreSuggestion)
                    return false;
                Genre = GenreSuggestion;
                return true;

            case MetadataSuggestionService.PlatformField:
                if (!CanAcceptPlatformSuggestion)
                    return false;
                Platform = PlatformSuggestion;
                return true;

            case MetadataSuggestionService.SeriesField:
                if (!CanAcceptSeriesSuggestion)
                    return false;
                Series = SeriesSuggestion;
                return true;

            case MetadataSuggestionService.ReleaseTypeField:
                if (!CanAcceptReleaseTypeSuggestion)
                    return false;
                ReleaseType = ReleaseTypeSuggestion;
                return true;

            case MetadataSuggestionService.PlayModeField:
                if (!CanAcceptPlayModeSuggestion)
                    return false;
                PlayMode = PlayModeSuggestion;
                return true;

            default:
                return false;
        }
    }

    private bool CanAcceptMetadataSuggestion(string fieldKey)
    {
        return fieldKey switch
        {
            MetadataSuggestionService.DeveloperField => CanAcceptDeveloperSuggestion,
            MetadataSuggestionService.PublisherField => CanAcceptPublisherSuggestion,
            MetadataSuggestionService.GenreField => CanAcceptGenreSuggestion,
            MetadataSuggestionService.PlatformField => CanAcceptPlatformSuggestion,
            MetadataSuggestionService.SeriesField => CanAcceptSeriesSuggestion,
            MetadataSuggestionService.ReleaseTypeField => CanAcceptReleaseTypeSuggestion,
            MetadataSuggestionService.PlayModeField => CanAcceptPlayModeSuggestion,
            _ => false
        };
    }

    private void RefreshDeveloperSuggestion()
    {
        DeveloperSuggestion = _metadataSuggestionService.GetBestMatch(
                                MetadataSuggestionService.DeveloperField,
                                Developer)
                            ?? string.Empty;
    }

    private void RefreshPublisherSuggestion()
    {
        PublisherSuggestion = _metadataSuggestionService.GetBestMatch(
                                MetadataSuggestionService.PublisherField,
                                Publisher)
                              ?? string.Empty;
    }

    private void RefreshGenreSuggestion()
    {
        GenreSuggestion = _metadataSuggestionService.GetBestMatch(
                              MetadataSuggestionService.GenreField,
                              Genre)
                          ?? string.Empty;
    }

    private void RefreshPlatformSuggestion()
    {
        PlatformSuggestion = _metadataSuggestionService.GetBestMatch(
                                 MetadataSuggestionService.PlatformField,
                                 Platform)
                             ?? string.Empty;
    }

    private void RefreshSeriesSuggestion()
    {
        SeriesSuggestion = _metadataSuggestionService.GetBestMatch(
                               MetadataSuggestionService.SeriesField,
                               Series)
                           ?? string.Empty;
    }

    private void RefreshReleaseTypeSuggestion()
    {
        ReleaseTypeSuggestion = _metadataSuggestionService.GetBestMatch(
                                    MetadataSuggestionService.ReleaseTypeField,
                                    ReleaseType)
                                ?? string.Empty;
    }

    private void RefreshPlayModeSuggestion()
    {
        PlayModeSuggestion = _metadataSuggestionService.GetBestMatch(
                                 MetadataSuggestionService.PlayModeField,
                                 PlayMode)
                             ?? string.Empty;
    }

    private static string GetSuggestionSuffix(string? input, string? suggestion)
    {
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(suggestion))
            return string.Empty;

        var trimmedInput = input.Trim();
        if (!suggestion.StartsWith(trimmedInput, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (trimmedInput.Length >= suggestion.Length)
            return string.Empty;

        return suggestion[trimmedInput.Length..];
    }

    private void DetachAssetHandlers()
    {
        if (_assetsChangedHandler == null)
            return;

        _originalItem.Assets.CollectionChanged -= _assetsChangedHandler;
        _assetsChangedHandler = null;
    }

    public void Dispose()
    {
        DetachAssetHandlers();
    }

    private static readonly AssetType[] AssetTypeOrder =
    {
        AssetType.Cover,
        AssetType.Wallpaper,
        AssetType.Screenshot,
        AssetType.Logo,
        AssetType.Video,
        AssetType.Music,
        AssetType.Marquee,
        AssetType.Banner,
        AssetType.Bezel,
        AssetType.ControlPanel,
        AssetType.Manual
    };

    private bool _isSortingAssets;
    private bool _isSortAssetsScheduled;

    private void ScheduleSortAssets()
    {
        if (_isSortingAssets || _isSortAssetsScheduled)
            return;

        _isSortAssetsScheduled = true;

        // Defer sorting to avoid modifying the collection inside CollectionChanged.
        // NOTE: UiThreadHelper.Post executes immediately when already on the UI thread,
        // so we must use Dispatcher.UIThread.Post to ensure true deferral.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _isSortAssetsScheduled = false;
            SortAssets();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void SortAssets()
    {
        if (_isSortingAssets)
            return;

        _isSortingAssets = true;
        try
        {
            if (_originalItem.Assets.Count <= 1)
                return;

            var orderMap = new Dictionary<AssetType, int>(AssetTypeOrder.Length);
            for (var i = 0; i < AssetTypeOrder.Length; i++)
                orderMap[AssetTypeOrder[i]] = i;

            var indexed = _originalItem.Assets
                .Select((asset, index) => new { asset, index })
                .ToList();

            var sorted = indexed
                .OrderBy(entry => orderMap.TryGetValue(entry.asset.Type, out var order) ? order : int.MaxValue)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.asset)
                .ToList();

            for (var i = 0; i < sorted.Count; i++)
            {
                var asset = sorted[i];
                var oldIndex = _originalItem.Assets.IndexOf(asset);
                if (oldIndex != i)
                    _originalItem.Assets.Move(oldIndex, i);
            }
        }
        finally
        {
            _isSortingAssets = false;
        }
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
        }

        // When switching to Emulator and we are in manual-emulator mode (no profile selected),
        // insert a default {file} placeholder if the field is empty
        if (value == MediaType.Emulator && IsManualEmulator)
        {
            if (string.IsNullOrWhiteSpace(LauncherArgs))
                LauncherArgs = "{file}";
        }

        RefreshInheritedWrappers();
        RebuildEnvironmentOverridesFromInheritance(CaptureCurrentEnvironmentOverrides());
    }
    
    private void InitializeEmulators(AppSettings settings)
    {
        AvailableEmulators.Clear();
        ResolveInheritedEmulatorInfo();

        var inheritedEmulatorName = _resolvedInheritedEmulator != null
            ? (string.IsNullOrWhiteSpace(_resolvedInheritedEmulator.Name)
                ? _resolvedInheritedEmulator.Id
                : _resolvedInheritedEmulator.Name)
            : null;

        var inheritedLabel = !string.IsNullOrWhiteSpace(inheritedEmulatorName) &&
                             !string.IsNullOrWhiteSpace(_resolvedInheritedEmulatorSource)
            ? string.Format(Strings.NodeSettings_InheritedEmulatorInfoFormat, inheritedEmulatorName, _resolvedInheritedEmulatorSource)
            : $"{Strings.NodeSettings_InheritedEmulatorNone} {Strings.EditMedia_InheritedEmulatorFallbackHint}";

        AvailableEmulators.Add(new EmulatorProfileOption(
            EmulatorProfileOption.OptionKind.Inherit,
            inheritedLabel,
            _resolvedInheritedEmulator));

        AvailableEmulators.Add(new EmulatorProfileOption(
            EmulatorProfileOption.OptionKind.Native,
            Strings.Type_Native));

        AvailableEmulators.Add(new EmulatorProfileOption(
            EmulatorProfileOption.OptionKind.Manual,
            "Custom / Manual"));

        foreach (var emu in settings.Emulators)
        {
            AvailableEmulators.Add(new EmulatorProfileOption(
                EmulatorProfileOption.OptionKind.Emulator,
                emu.Name,
                emu));
        }

        if (!string.IsNullOrEmpty(_originalItem.EmulatorId))
        {
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                e => e.Kind == EmulatorProfileOption.OptionKind.Emulator &&
                     string.Equals(e.Emulator?.Id, _originalItem.EmulatorId, StringComparison.Ordinal));
        }
        else if (_originalItem.MediaType == MediaType.Emulator)
        {
            if (!string.IsNullOrWhiteSpace(_originalItem.LauncherPath))
            {
                SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                    e => e.Kind == EmulatorProfileOption.OptionKind.Manual);
            }
            else
            {
                SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                    e => e.Kind == EmulatorProfileOption.OptionKind.Inherit);
            }
        }
        else
        {
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(
                e => e.Kind == EmulatorProfileOption.OptionKind.Native);
        }

        SelectedEmulatorProfile ??= AvailableEmulators.FirstOrDefault();
    }

    private void InitializeRunnerVersions(AppSettings settings)
    {
        AvailableRunnerVersions.Clear();
        AvailableRunnerVersions.Add(new RunnerVersionOption(
            id: null,
            name: Strings.NodeSettings_ModeNone));

        foreach (var version in settings.RunnerVersions.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var suffix = version.Kind == RunnerVersionKind.Wine ? "Wine" : "Proton";
            var name = string.IsNullOrWhiteSpace(version.Name)
                ? $"({suffix})"
                : $"{version.Name} ({suffix})";

            AvailableRunnerVersions.Add(new RunnerVersionOption(
                id: version.Id,
                name: name,
                kind: version.Kind));
        }

        var selected = AvailableRunnerVersions.FirstOrDefault(v =>
            string.Equals(v.Id, _originalItem.RunnerVersionId, StringComparison.Ordinal));

        SelectedRunnerVersion = selected ?? AvailableRunnerVersions.FirstOrDefault();
        RefreshInheritedRunnerVersionInfo();
    }

    private void RefreshInheritedRunnerVersionInfo()
    {
        var emulator = ResolveSelectedEmulatorConfig();
        if (string.IsNullOrWhiteSpace(emulator?.DefaultRunnerVersionId))
        {
            SetInheritedRunnerVersionInfo(string.Empty);
            return;
        }

        var defaultRunnerId = emulator.DefaultRunnerVersionId;
        var configuredVersion = _settings.RunnerVersions.FirstOrDefault(v =>
            string.Equals(v.Id, defaultRunnerId, StringComparison.Ordinal));

        var runnerName = configuredVersion == null
            ? string.Format(
                T("EditMedia_RunnerVersionInheritedMissingFormat", "(missing: {0})"),
                defaultRunnerId)
            : string.IsNullOrWhiteSpace(configuredVersion.Name)
                ? (configuredVersion.Kind == RunnerVersionKind.Wine
                    ? $"({T("EditMedia_RunnerVersionKindWine", "Wine")})"
                    : $"({T("EditMedia_RunnerVersionKindProton", "Proton")})")
                : $"{configuredVersion.Name} ({(configuredVersion.Kind == RunnerVersionKind.Wine
                    ? T("EditMedia_RunnerVersionKindWine", "Wine")
                    : T("EditMedia_RunnerVersionKindProton", "Proton"))})";

        var selectedItemRunnerId = SelectedRunnerVersion?.Id;
        var hasItemRunnerSelection = !string.IsNullOrWhiteSpace(selectedItemRunnerId);
        var overrideActive = hasItemRunnerSelection &&
                             !string.Equals(selectedItemRunnerId, defaultRunnerId, StringComparison.Ordinal);

        var message = string.Format(
            overrideActive
                ? T("EditMedia_RunnerVersionInheritedFromEmulatorOverrideFormat",
                    "Inherited from emulator: {0}. Item override is active.")
                : hasItemRunnerSelection
                    ? T("EditMedia_RunnerVersionInheritedFromEmulatorMatchFormat",
                        "Inherited from emulator: {0}. Item selection matches inherited value.")
                    : T("EditMedia_RunnerVersionInheritedFromEmulatorFormat",
                        "Inherited from emulator: {0}."),
            runnerName);

        SetInheritedRunnerVersionInfo(message);
    }

    private void SetInheritedRunnerVersionInfo(string value)
    {
        value ??= string.Empty;
        if (string.Equals(_inheritedRunnerVersionInfo, value, StringComparison.Ordinal))
            return;

        _inheritedRunnerVersionInfo = value;
        OnPropertyChanged(nameof(InheritedRunnerVersionInfo));
        OnPropertyChanged(nameof(HasInheritedRunnerVersionInfo));
    }

    private void ResolveInheritedEmulatorInfo()
    {
        _resolvedInheritedEmulator = null;
        _resolvedInheritedEmulatorSource = null;

        if (_parentNode != null && _rootNodes.Count > 0)
        {
            var chain = PathHelper.GetNodeChain(_parentNode, _rootNodes);
            chain.Reverse();

            foreach (var node in chain)
            {
                if (string.IsNullOrWhiteSpace(node.DefaultEmulatorId))
                    continue;

                _resolvedInheritedEmulator = _settings.Emulators
                    .FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
                _resolvedInheritedEmulatorSource = node.Name;
                break;
            }
        }

        if (_resolvedInheritedEmulator == null && _inheritedEmulator != null)
            _resolvedInheritedEmulator = _inheritedEmulator;
    }

    private EmulatorConfig? ResolveInheritedEmulator()
    {
        if (_resolvedInheritedEmulator == null && _resolvedInheritedEmulatorSource == null)
            ResolveInheritedEmulatorInfo();

        return _resolvedInheritedEmulator;
    }

    private EmulatorConfig? ResolveSelectedEmulatorConfig()
    {
        var selection = SelectedEmulatorProfile;
        if (selection == null)
            return null;

        return selection.Kind switch
        {
            EmulatorProfileOption.OptionKind.Emulator => selection.Emulator,
            EmulatorProfileOption.OptionKind.Inherit => ResolveInheritedEmulator(),
            _ => null
        };
    }

    // --- Asset Actions ---

    private async Task ImportAssetAsync(AssetType type)
    {
        if (StorageProvider == null) return;

        // Create file type filters based on the selected asset type.
        var fileTypes = type switch
        {
            AssetType.Music => new[]
            {
                new FilePickerFileType("Audio")
                {
                    Patterns = new[] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.sid" }
                }
            },
            AssetType.Video => new[]
            {
                new FilePickerFileType("Video")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.webm" }
                }
            },
            AssetType.Manual => new[]
            {
                new FilePickerFileType("Documents")
                {
                    Patterns = new[] { "*.pdf", "*.cbz", "*.txt", "*.md", "*.rtf", "*.html", "*.htm", "*.jpg", "*.jpeg", "*.png" }
                }
            },
            _ => new[] { FilePickerFileTypes.ImageAll } // Default for Cover, Logo, Wallpaper, Marquee, etc
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
            // The FileManagementService handles copying, renaming, and adding the asset to the list
            var imported = await _fileService.ImportAssetAsync(file.Path.LocalPath, _originalItem, _nodePath, type);
            if (imported != null)
            {
                await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Add(imported));
                HasAssetChanges = true;
            }
        }
    }

    private async Task DeleteSelectedAssetAsync()
    {
        if (SelectedAsset == null) 
            return;

        var asset = SelectedAsset;

        // 1) Remove from collection on UI thread (immediate UI feedback)
        await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Remove(asset));

        try
        {
            // 2) Delete file (IO-bound)
            _fileService.DeleteAssetFile(asset);
            HasAssetChanges = true;

            // Clear selection so the delete button hides/updates correctly
            SelectedAsset = null;
        }
        catch
        {
            // 3) Rollback in collection if delete failed
            await UiThreadHelper.InvokeAsync(() => _originalItem.Assets.Add(asset));
        }
    }

    private async Task ChangePrimaryFileAsync()
    {
        if (StorageProvider == null)
            return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select launch file (main executable / Disc 1)",
            AllowMultiple = false,
            // No FileTypeFilter on purpose: executables / scripts may have no extension
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
            if (PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
            {
                storedPath = relativePath;
                storedKind = MediaFileKind.LibraryRelative;
            }
            else
            {
                storedPath = path;
                storedKind = MediaFileKind.Absolute;
            }
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
        // - effective working directory
        // - preview command line (uses GetPrimaryLaunchPath())
        OnPropertyChanged(nameof(PrimaryFileDisplayPath));
        OnPropertyChanged(nameof(EffectiveWorkingDirectory));
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

        if (result == null || result.Count == 0)
            return;

        var path = result[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settings.PreferPortableLaunchPaths &&
            PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
        {
            LauncherPath = relativePath;
        }
        else
        {
            LauncherPath = path;
        }
    }

    private async Task BrowseWorkingDirectoryAsync()
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectWorkingDirectory,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return;

        var path = result[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settings.PreferPortableLaunchPaths &&
            PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
        {
            WorkingDirectory = relativePath;
        }
        else
        {
            WorkingDirectory = path;
        }
    }

    private async Task BrowseXdgConfigAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgConfigFolder, path => XdgConfigPath = path);

    private async Task BrowseXdgDataAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgDataFolder, path => XdgDataPath = path);

    private async Task BrowseXdgCacheAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgCacheFolder, path => XdgCachePath = path);

    private async Task BrowseXdgStateAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgStateFolder, path => XdgStatePath = path);

    private async Task BrowseXdgBaseAsync()
        => await BrowseXdgFolderAsync(Strings.Dialog_SelectXdgBaseFolder, path => XdgBasePath = path);

    private async Task BrowseXdgFolderAsync(string title, Action<string> setPath)
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return;

        var path = result[0].Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (_settings.PreferPortableLaunchPaths &&
            PortablePathHelper.TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
        {
            setPath(relativePath);
        }
        else
        {
            setPath(path);
        }
    }

    private bool CanApplyXdgBase()
        => !string.IsNullOrWhiteSpace(XdgBasePath);

    private void ApplyXdgBase()
    {
        var basePath = XdgBasePath?.Trim();
        if (string.IsNullOrWhiteSpace(basePath))
            return;

        XdgConfigPath = Path.Combine(basePath, "config");
        XdgDataPath = Path.Combine(basePath, "data");
        XdgCachePath = Path.Combine(basePath, "cache");
        XdgStatePath = Path.Combine(basePath, "state");
    }

    private void ApplyPortableXdgPreset()
    {
        ApplyPortableXdgOverrides(includeHome: false);
    }

    private void ApplyPortableXdgAndHomePreset()
    {
        ApplyPortableXdgOverrides(includeHome: true);
    }

    private void ApplyPortableXdgOverrides(bool includeHome)
    {
        XdgConfigPath = "Home/.config";
        XdgDataPath = "Home/.local/share";
        XdgCachePath = "Home/.cache";
        XdgStatePath = "Home/.local/state";

        if (includeHome)
            UpsertItemEnvironmentOverride("HOME", "Home");
    }

    private void UpsertItemEnvironmentOverride(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var existing = EnvironmentOverrides
            .FirstOrDefault(row => string.Equals(row.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Key = key;
            existing.Value = value;
            existing.IsInherited = false;
            existing.Source = Strings.Common_SourceItem;
            return;
        }

        EnvironmentOverrides.Add(new EnvVarRow
        {
            Key = key,
            Value = value,
            IsInherited = false,
            Source = Strings.Common_SourceItem
        });
    }
}
