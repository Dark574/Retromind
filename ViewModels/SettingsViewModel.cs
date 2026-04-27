using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Compression;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for the application settings dialog
/// Manages emulator profiles and scraper configurations
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private static readonly HttpClient GitHubHttpClient = CreateGitHubHttpClient();
    private const string GeProtonReleasesApiUrl = "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases";
    private const int GeProtonPerPage = 100;
    private const int GeProtonMaxPages = 6;
    private const int GeProtonMaxItems = 300;

    private readonly AppSettings _appSettings;
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private readonly Dictionary<string, int> _runnerUsageById = new(StringComparer.Ordinal);
    private bool _hasAutoLoadedGeReleases;
    private bool _disposed;

    private static string T(string key, string fallback)
        => Strings.ResourceManager.GetString(key, Strings.Culture) ?? fallback;

    // Currently selected emulator profile
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(RemoveEmulatorCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowsePathCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPortableXdgPresetCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPortableXdgAndHomePresetCommand))]
    private EmulatorConfig? _selectedEmulator;

    // Currently selected scraper config
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(RemoveScraperCommand))]
    private ScraperConfig? _selectedScraper;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSteamLibraryPathCommand))]
    private string? _selectedSteamLibraryPath;

    [ObservableProperty]
    private string _steamLibraryPathInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveHeroicGogPathCommand))]
    private string? _selectedHeroicGogPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveHeroicEpicPathCommand))]
    private string? _selectedHeroicEpicPath;

    [ObservableProperty]
    private string _heroicGogPathInput = string.Empty;

    [ObservableProperty]
    private string _heroicEpicPathInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRunnerVersionCommand))]
    [NotifyPropertyChangedFor(nameof(IsRunnerReplacementVisible))]
    [NotifyPropertyChangedFor(nameof(RunnerReplacementHint))]
    private RunnerVersionRow? _selectedRunnerVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRunnerVersionCommand))]
    private string _runnerVersionNameInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRunnerVersionCommand))]
    private string _runnerVersionPathInput = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveRunnerVersionCommand))]
    private RunnerVersionSelectionOption? _selectedRunnerReplacement;

    [ObservableProperty]
    private RunnerVersionSelectionOption? _selectedEmulatorRunnerVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedGeReleaseCommand))]
    private GeProtonReleaseOption? _selectedGeProtonRelease;

    [ObservableProperty]
    private int _selectedSettingsTabIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSelectedGeReleaseCommand))]
    private bool _isGeReleaseBusy;

    [ObservableProperty]
    private string _geReleaseStatusText = string.Empty;
    
    // Available scraper types for the UI.
    // Keep "None" so new entries can stay intentionally unconfigured.
    // EmuMovies is temporarily disabled until their API is back.
    public ScraperType[] AvailableScraperTypes { get; } = Enum.GetValues<ScraperType>()
        .Where(t => t != ScraperType.EmuMovies)
        .ToArray();

    public EmulatorConfig.XdgOverrideMode[] AvailableEmulatorXdgModes { get; } = Enum.GetValues<EmulatorConfig.XdgOverrideMode>();
    public EmulatorConfig.RunnerIntent[] AvailableEmulatorRunnerTypes { get; } = Enum.GetValues<EmulatorConfig.RunnerIntent>();
    
    // UI Collections
    public ObservableCollection<EmulatorConfig> Emulators { get; } = new();
    public ObservableCollection<ScraperConfig> Scrapers { get; } = new();
    public ObservableCollection<string> SteamLibraryPaths { get; } = new();
    public ObservableCollection<string> HeroicGogConfigPaths { get; } = new();
    public ObservableCollection<string> HeroicEpicConfigPaths { get; } = new();
    public ObservableCollection<RunnerVersionRow> RunnerVersions { get; } = new();
    public ObservableCollection<RunnerVersionSelectionOption> SelectedEmulatorRunnerVersionOptions { get; } = new();
    public ObservableCollection<RunnerVersionSelectionOption> RunnerReplacementOptions { get; } = new();
    public ObservableCollection<GeProtonReleaseOption> GeProtonReleases { get; } = new();

    /// <summary>
    /// Controls whether newly selected launch file paths are stored as portable
    /// DataRoot-relative paths or as absolute file system paths
    /// </summary>
    public bool PreferPortableLaunchPaths
    {
        get => _appSettings.PreferPortableLaunchPaths;
        set
        {
            if (_appSettings.PreferPortableLaunchPaths == value)
                return;

            _appSettings.PreferPortableLaunchPaths = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Controls whether the AppImage redirects HOME and XDG_* into a local folder
    /// next to the AppImage for portability. Requires restart to apply.
    /// </summary>
    public bool UsePortableHomeInAppImage
    {
        get => _appSettings.UsePortableHomeInAppImage;
        set
        {
            if (_appSettings.UsePortableHomeInAppImage == value)
                return;

            _appSettings.UsePortableHomeInAppImage = value;
            if (!value)
                _appSettings.ForcePortableHomeInAppImage = false;
            OnPropertyChanged();
        }
    }

    public void SetPortableHomeInAppImageMode(bool enabled, bool force)
    {
        _appSettings.UsePortableHomeInAppImage = enabled;
        _appSettings.ForcePortableHomeInAppImage = enabled && force;
        OnPropertyChanged(nameof(UsePortableHomeInAppImage));
    }
    
    /// <summary>
    /// Controls whether selecting an item in the main media grid should
    /// automatically start playback of its primary music asset (if present)
    /// </summary>
    public bool EnableSelectionMusicPreview
    {
        get => _appSettings.EnableSelectionMusicPreview;
        set
        {
            if (_appSettings.EnableSelectionMusicPreview == value)
                return;

            _appSettings.EnableSelectionMusicPreview = value;
            OnPropertyChanged();
        }
    }

    private ScraperImportSettings ScraperImportSettings
    {
        get
        {
            _appSettings.ScraperImport ??= new ScraperImportSettings();
            return _appSettings.ScraperImport;
        }
    }

    public ScraperExistingDataMode[] AvailableScraperExistingDataModes { get; } =
        Enum.GetValues<ScraperExistingDataMode>();

    public ScraperExistingDataMode ScraperExistingDataMode
    {
        get => ScraperImportSettings.ExistingDataMode;
        set
        {
            if (ScraperImportSettings.ExistingDataMode == value)
                return;

            ScraperImportSettings.ExistingDataMode = value;
            OnPropertyChanged();
        }
    }

    // Metadata switches
    public bool ScraperImportDescription
    {
        get => ScraperImportSettings.ImportDescription;
        set { if (ScraperImportSettings.ImportDescription != value) { ScraperImportSettings.ImportDescription = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportReleaseDate
    {
        get => ScraperImportSettings.ImportReleaseDate;
        set { if (ScraperImportSettings.ImportReleaseDate != value) { ScraperImportSettings.ImportReleaseDate = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportRating
    {
        get => ScraperImportSettings.ImportRating;
        set { if (ScraperImportSettings.ImportRating != value) { ScraperImportSettings.ImportRating = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportDeveloper
    {
        get => ScraperImportSettings.ImportDeveloper;
        set { if (ScraperImportSettings.ImportDeveloper != value) { ScraperImportSettings.ImportDeveloper = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportGenre
    {
        get => ScraperImportSettings.ImportGenre;
        set { if (ScraperImportSettings.ImportGenre != value) { ScraperImportSettings.ImportGenre = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportPlatform
    {
        get => ScraperImportSettings.ImportPlatform;
        set { if (ScraperImportSettings.ImportPlatform != value) { ScraperImportSettings.ImportPlatform = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportPublisher
    {
        get => ScraperImportSettings.ImportPublisher;
        set { if (ScraperImportSettings.ImportPublisher != value) { ScraperImportSettings.ImportPublisher = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportSeries
    {
        get => ScraperImportSettings.ImportSeries;
        set { if (ScraperImportSettings.ImportSeries != value) { ScraperImportSettings.ImportSeries = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportReleaseType
    {
        get => ScraperImportSettings.ImportReleaseType;
        set { if (ScraperImportSettings.ImportReleaseType != value) { ScraperImportSettings.ImportReleaseType = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportSortTitle
    {
        get => ScraperImportSettings.ImportSortTitle;
        set { if (ScraperImportSettings.ImportSortTitle != value) { ScraperImportSettings.ImportSortTitle = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportPlayMode
    {
        get => ScraperImportSettings.ImportPlayMode;
        set { if (ScraperImportSettings.ImportPlayMode != value) { ScraperImportSettings.ImportPlayMode = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportMaxPlayers
    {
        get => ScraperImportSettings.ImportMaxPlayers;
        set { if (ScraperImportSettings.ImportMaxPlayers != value) { ScraperImportSettings.ImportMaxPlayers = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportSource
    {
        get => ScraperImportSettings.ImportSource;
        set { if (ScraperImportSettings.ImportSource != value) { ScraperImportSettings.ImportSource = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportCustomFields
    {
        get => ScraperImportSettings.ImportCustomFields;
        set { if (ScraperImportSettings.ImportCustomFields != value) { ScraperImportSettings.ImportCustomFields = value; OnPropertyChanged(); } }
    }

    // Asset switches
    public bool ScraperImportCover
    {
        get => ScraperImportSettings.ImportCover;
        set { if (ScraperImportSettings.ImportCover != value) { ScraperImportSettings.ImportCover = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportWallpaper
    {
        get => ScraperImportSettings.ImportWallpaper;
        set { if (ScraperImportSettings.ImportWallpaper != value) { ScraperImportSettings.ImportWallpaper = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportScreenshot
    {
        get => ScraperImportSettings.ImportScreenshot;
        set { if (ScraperImportSettings.ImportScreenshot != value) { ScraperImportSettings.ImportScreenshot = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportLogo
    {
        get => ScraperImportSettings.ImportLogo;
        set { if (ScraperImportSettings.ImportLogo != value) { ScraperImportSettings.ImportLogo = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportMarquee
    {
        get => ScraperImportSettings.ImportMarquee;
        set { if (ScraperImportSettings.ImportMarquee != value) { ScraperImportSettings.ImportMarquee = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportBezel
    {
        get => ScraperImportSettings.ImportBezel;
        set { if (ScraperImportSettings.ImportBezel != value) { ScraperImportSettings.ImportBezel = value; OnPropertyChanged(); } }
    }
    public bool ScraperImportControlPanel
    {
        get => ScraperImportSettings.ImportControlPanel;
        set { if (ScraperImportSettings.ImportControlPanel != value) { ScraperImportSettings.ImportControlPanel = value; OnPropertyChanged(); } }
    }
    public bool ScraperAppendAssetsDuringBulkScrape
    {
        get => ScraperImportSettings.AppendAssetsDuringBulkScrape;
        set
        {
            if (ScraperImportSettings.AppendAssetsDuringBulkScrape != value)
            {
                ScraperImportSettings.AppendAssetsDuringBulkScrape = value;
                OnPropertyChanged();
            }
        }
    }

    public string ScraperImportSectionTitle => T("Settings_SectionScraperImport", "Scraper import");
    public string ScraperImportHint => T("Settings_ScraperImportHint", "Applies to both manual scrape and bulk scrape.");
    public string ScraperExistingDataModeText => T("Settings_ScraperExistingDataMode", "If data already exists:");
    public string ScraperBulkAppendAssetsText => T("Settings_ScraperBulkAssetConflictPrompt", "In bulk scrape, append new artwork when artwork already exists");
    public string ScraperBulkAppendAssetsHint => T("Settings_ScraperBulkAssetConflictHint", "Missing artwork is always imported.");
    public string ScraperMetadataFieldsText => T("Settings_ScraperMetadataFields", "Metadata fields");
    public string ScraperAssetFieldsText => T("Settings_ScraperAssetFields", "Artwork / assets");

    public string ParentalSectionTitle => T("Settings_SectionParental", "Parental control");
    public string ChangeParentalPasswordText => T("Settings_ChangeParentalPassword", "Change parental password");
    public string SettingsTabEmulatorsShort => T("Settings_TabEmulatorsShort", "Emu");
    public string SettingsTabMetadataShort => T("Settings_TabMetadataShort", "Meta");
    public string SettingsTabRunnerShort => T("Settings_TabRunnerShort", "Runner");
    public string SettingsTabMiscShort => T("Settings_TabMiscShort", "Misc");
    public string RunnerVersionsTabTitle => T("Settings_TabRunnerVersions", "Wine-/Proton-Versionen");
    public string RunnerVersionsSectionTitle => T("Settings_SectionRunnerVersions", "Wine/Proton versions");
    public string RunnerVersionNameLabel => T("Settings_RunnerVersionNameLabel", "Name");
    public string RunnerVersionPathLabel => T("Settings_RunnerVersionPathLabel", "Path");
    public string RunnerVersionBrowseTitle => T("Settings_RunnerVersionBrowseTitle", "Select Wine/Proton directory");
    public string RunnerVersionDefaultPathHint => T("Settings_RunnerVersionDefaultPathHint", "Managed downloads are stored under Emulators/ProtonVersions in the portable root.");
    public string RunnerVersionUsageLabel => T("Settings_RunnerVersionUsageLabel", "Games using this version");
    public string RunnerReplacementLabel => T("Settings_RunnerReplacementLabel", "Replace with");
    public string RunnerReplacementHint => IsRunnerReplacementVisible
        ? T("Settings_RunnerReplacementHint", "Select a replacement before removing this version.")
        : string.Empty;
    public string GeProtonSectionTitle => T("Settings_GeProtonSectionTitle", "GE-Proton download");
    public string GeProtonSelectionLabel => T("Settings_GeProtonSelectionLabel", "Available releases");
    public string GeProtonRefreshLabel => T("Settings_GeProtonRefreshLabel", "Refresh list");
    public string GeProtonDownloadLabel => T("Settings_GeProtonDownloadLabel", "Download selected");
    public string GeProtonStatusLabel => T("Settings_GeProtonStatusLabel", "Status");
    public string EmulatorRunnerTypeLabel => T("Settings_EmulatorRunnerTypeLabel", "Runner type");
    public string EmulatorRunnerVersionLabel => T("Settings_EmulatorRunnerVersionLabel", "Default runner version");
    public string EmulatorRunnerDisabledHint => T("Settings_EmulatorRunnerDisabledHint", "Enable per-game prefixes to activate emulator-level defaults.");

    public bool IsRunnerReplacementVisible => SelectedRunnerVersion?.UsedByGames > 0;
    public bool IsEmulatorRunnerSelectionEnabled => SelectedEmulator?.UsesWinePrefix == true;
    
    /// <summary>
    /// Lightweight UI row for editing a single wrapper (Path + Args) on emulator level
    /// Mirrors <see cref="LaunchWrapper"/> but keeps the model decoupled from live editing
    /// </summary>
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

    /// <summary>
    /// Simple UI row for editing a single environment variable (Key/Value)
    /// on emulator profile level
    /// </summary>
    public sealed partial class EnvVarRow : ObservableObject
    {
        [ObservableProperty] private string _key = string.Empty;
        [ObservableProperty] private string _value = string.Empty;
    }

    public sealed partial class RunnerVersionRow : ObservableObject
    {
        [ObservableProperty] private string _id = Guid.NewGuid().ToString();
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private RunnerVersionKind _kind = RunnerVersionKind.Proton;
        [ObservableProperty] private RunnerVersionSourceType _sourceType = RunnerVersionSourceType.ExternalPath;
        [ObservableProperty] private string _path = string.Empty;
        [ObservableProperty] private string? _releaseTag;
        [ObservableProperty] private int _usedByGames;

        public string KindDisplay => Kind == RunnerVersionKind.Wine ? "Wine" : "Proton";
        public string SourceDisplay => SourceType == RunnerVersionSourceType.ManagedDownload ? "ManagedDownload" : "ExternalPath";

        public RunnerVersionRow()
        {
        }

        public RunnerVersionRow(RunnerVersionConfig model)
        {
            Id = model.Id;
            Name = model.Name ?? string.Empty;
            Kind = model.Kind;
            SourceType = model.SourceType;
            Path = model.Path ?? string.Empty;
            ReleaseTag = model.ReleaseTag;
        }

        public RunnerVersionConfig ToModel()
            => new()
            {
                Id = Id,
                Name = Name?.Trim() ?? string.Empty,
                Kind = Kind,
                SourceType = SourceType,
                Path = Path?.Trim() ?? string.Empty,
                ReleaseTag = string.IsNullOrWhiteSpace(ReleaseTag) ? null : ReleaseTag.Trim()
            };

        partial void OnKindChanged(RunnerVersionKind value)
        {
            OnPropertyChanged(nameof(KindDisplay));
        }

        partial void OnSourceTypeChanged(RunnerVersionSourceType value)
        {
            OnPropertyChanged(nameof(SourceDisplay));
        }
    }

    public sealed class RunnerVersionSelectionOption
    {
        public RunnerVersionSelectionOption(string? id, string name, RunnerVersionKind? kind = null)
        {
            Id = id;
            Name = name ?? string.Empty;
            Kind = kind;
        }

        public string? Id { get; }
        public string Name { get; }
        public RunnerVersionKind? Kind { get; }
    }

    public sealed class GeProtonReleaseOption
    {
        public GeProtonReleaseOption(string tagName, string assetName, string downloadUrl)
        {
            TagName = tagName ?? string.Empty;
            AssetName = assetName ?? string.Empty;
            DownloadUrl = downloadUrl ?? string.Empty;
        }

        public string TagName { get; }
        public string AssetName { get; }
        public string DownloadUrl { get; }
        public string DisplayName => string.IsNullOrWhiteSpace(AssetName) ? TagName : $"{TagName} ({AssetName})";
    }
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmulatorWrapperListEnabled))]
    private bool _useGlobalWrapperDefaults;

    public bool IsEmulatorWrapperListEnabled => !UseGlobalWrapperDefaults;

    public bool IsEmulatorXdgCustomSelected => SelectedEmulator?.XdgMode == EmulatorConfig.XdgOverrideMode.Custom;

    /// <summary>
    /// UI collection bound to the emulator wrapper editor
    /// This list is re-synchronized when SelectedEmulator changes
    /// </summary>
    public ObservableCollection<LaunchWrapperRow> EmulatorNativeWrappers { get; } = new();

    /// <summary>
    /// UI collection for the environment overrides of the selected emulator
    /// Changes are synchronized back into SelectedEmulator.EnvironmentOverrides on Save()
    /// </summary>
    public ObservableCollection<EnvVarRow> EmulatorEnvironmentOverrides { get; } = new();
    
    // Commands
    public IRelayCommand AddEmulatorCommand { get; }
    public IRelayCommand RemoveEmulatorCommand { get; }
    public IRelayCommand AddScraperCommand { get; }
    public IRelayCommand RemoveScraperCommand { get; }
    public IRelayCommand AddSteamLibraryPathCommand { get; }
    public IRelayCommand RemoveSteamLibraryPathCommand { get; }
    public IRelayCommand AddHeroicGogPathCommand { get; }
    public IRelayCommand RemoveHeroicGogPathCommand { get; }
    public IRelayCommand AddHeroicEpicPathCommand { get; }
    public IRelayCommand RemoveHeroicEpicPathCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand BrowsePathCommand { get; }
    public IAsyncRelayCommand BrowseSteamLibraryPathCommand { get; }
    public IAsyncRelayCommand BrowseHeroicGogPathCommand { get; }
    public IAsyncRelayCommand BrowseHeroicEpicPathCommand { get; }
    public IAsyncRelayCommand ConvertExistingToPortableCommand { get; }
    public IAsyncRelayCommand ChangeParentalPasswordCommand { get; }

    // Emulator wrapper editor commands
    public IRelayCommand AddEmulatorWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveEmulatorWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveEmulatorWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveEmulatorWrapperDownCommand { get; }
    
    // Emulator environment editor commands
    public IRelayCommand AddEmulatorEnvVarCommand { get; }
    public IRelayCommand<EnvVarRow?> RemoveEmulatorEnvVarCommand { get; }
    public IRelayCommand ApplyPortableXdgPresetCommand { get; }
    public IRelayCommand ApplyPortableXdgAndHomePresetCommand { get; }
    public IRelayCommand AddRunnerVersionCommand { get; }
    public IRelayCommand RemoveRunnerVersionCommand { get; }
    public IAsyncRelayCommand BrowseRunnerVersionPathCommand { get; }
    public IAsyncRelayCommand RefreshGeReleasesCommand { get; }
    public IAsyncRelayCommand DownloadSelectedGeReleaseCommand { get; }

    public event Action? RequestClose;
    
    /// <summary>
    /// Raised when the user explicitly requests to convert existing launch
    /// file paths under the Retromind folder into portable (DataRoot-relative) paths
    /// The main window view model is responsible for performing the actual migration
    /// </summary>
    public event Func<Task>? RequestPortableMigration;
    
    /// <summary>
    /// Raised when the user requests to change the parental-control password.
    /// The main window view model owns the parental lock flow and handles this request.
    /// </summary>
    public event Func<Task>? RequestParentalPasswordChange;

    public bool LibraryModified { get; private set; }

    // Optional dependency injection for file dialogs (better for testing)
    public IStorageProvider? StorageProvider { get; set; }

    public SettingsViewModel(AppSettings settings, ObservableCollection<MediaNode>? rootNodes = null)
    {
        _appSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _rootNodes = rootNodes ?? new ObservableCollection<MediaNode>();

        // Load existing emulators
        foreach (var emu in _appSettings.Emulators) 
        {
            Emulators.Add(emu);
        }

        // Load existing scrapers
        foreach (var scraper in _appSettings.Scrapers) 
        {
            Scrapers.Add(scraper);
        }

        if (_appSettings.SteamLibraryPaths != null)
        {
            foreach (var path in _appSettings.SteamLibraryPaths)
                SteamLibraryPaths.Add(path);
        }

        if (_appSettings.HeroicGogConfigPaths != null)
        {
            foreach (var path in _appSettings.HeroicGogConfigPaths)
                HeroicGogConfigPaths.Add(path);
        }

        if (_appSettings.HeroicEpicConfigPaths != null)
        {
            foreach (var path in _appSettings.HeroicEpicConfigPaths)
                HeroicEpicConfigPaths.Add(path);
        }

        if (_appSettings.RunnerVersions != null)
        {
            foreach (var version in _appSettings.RunnerVersions)
                RunnerVersions.Add(new RunnerVersionRow(version));
        }

        AddEmulatorCommand = new RelayCommand(AddEmulator);
        RemoveEmulatorCommand = new RelayCommand(RemoveEmulator, () => SelectedEmulator != null);
        
        AddScraperCommand = new RelayCommand(AddScraper);
        RemoveScraperCommand = new RelayCommand(RemoveScraper, () => SelectedScraper != null);
        
        AddSteamLibraryPathCommand = new RelayCommand(AddSteamLibraryPath);
        RemoveSteamLibraryPathCommand = new RelayCommand(RemoveSteamLibraryPath, () => SelectedSteamLibraryPath != null);
        
        AddHeroicGogPathCommand = new RelayCommand(AddHeroicGogPath);
        RemoveHeroicGogPathCommand = new RelayCommand(RemoveHeroicGogPath, () => SelectedHeroicGogPath != null);
        AddHeroicEpicPathCommand = new RelayCommand(AddHeroicEpicPath);
        RemoveHeroicEpicPathCommand = new RelayCommand(RemoveHeroicEpicPath, () => SelectedHeroicEpicPath != null);
        
        SaveCommand = new RelayCommand(Save, CanSave);
        BrowsePathCommand = new AsyncRelayCommand(BrowsePathAsync, () => SelectedEmulator != null);
        BrowseSteamLibraryPathCommand = new AsyncRelayCommand(BrowseSteamLibraryPathAsync);
        BrowseHeroicGogPathCommand = new AsyncRelayCommand(BrowseHeroicGogPathAsync);
        BrowseHeroicEpicPathCommand = new AsyncRelayCommand(BrowseHeroicEpicPathAsync);
        
        // command to request migration to portable launch paths
        ConvertExistingToPortableCommand = new AsyncRelayCommand(ConvertExistingToPortableAsync);
        ChangeParentalPasswordCommand = new AsyncRelayCommand(ChangeParentalPasswordAsync);
        
        // Emulator-wrapper commands
        AddEmulatorWrapperCommand = new RelayCommand(AddEmulatorWrapper);
        RemoveEmulatorWrapperCommand = new RelayCommand<LaunchWrapperRow?>(RemoveEmulatorWrapper);
        MoveEmulatorWrapperUpCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveEmulatorWrapperUp,
            row => row != null && EmulatorNativeWrappers.IndexOf(row) > 0);

        MoveEmulatorWrapperDownCommand = new RelayCommand<LaunchWrapperRow?>(
            MoveEmulatorWrapperDown,
            row =>
            {
                if (row == null) return false;
                var idx = EmulatorNativeWrappers.IndexOf(row);
                return idx >= 0 && idx < EmulatorNativeWrappers.Count - 1;
            });
        
        // Emulator env-var editor commands
        AddEmulatorEnvVarCommand = new RelayCommand(AddEmulatorEnvVar);
        RemoveEmulatorEnvVarCommand = new RelayCommand<EnvVarRow?>(RemoveEmulatorEnvVar);
        ApplyPortableXdgPresetCommand = new RelayCommand(ApplyPortableXdgPreset, () => SelectedEmulator != null);
        ApplyPortableXdgAndHomePresetCommand = new RelayCommand(ApplyPortableXdgAndHomePreset, () => SelectedEmulator != null);
        AddRunnerVersionCommand = new RelayCommand(AddRunnerVersion, CanAddRunnerVersion);
        RemoveRunnerVersionCommand = new RelayCommand(RemoveRunnerVersion, CanRemoveRunnerVersion);
        BrowseRunnerVersionPathCommand = new AsyncRelayCommand(BrowseRunnerVersionPathAsync);
        RefreshGeReleasesCommand = new AsyncRelayCommand(RefreshGeReleasesAsync, () => !IsGeReleaseBusy);
        DownloadSelectedGeReleaseCommand = new AsyncRelayCommand(DownloadSelectedGeReleaseAsync, CanDownloadSelectedGeRelease);

        foreach (var emulator in Emulators)
            emulator.PropertyChanged += OnAnyEmulatorPropertyChanged;

        RecomputeRunnerUsageCounts();
        RebuildSelectedEmulatorRunnerVersionOptions();
        RebuildRunnerReplacementOptions();
        GeReleaseStatusText = T("Settings_GeProtonStatusIdle", "Idle");
    }

    // --- Computed Properties for UI Hints ---
    
    public bool IsTmdbSelected => SelectedScraper?.Type == ScraperType.TMDB;
    public bool IsIgdbSelected => SelectedScraper?.Type == ScraperType.IGDB;
    public bool IsEmuMoviesSelected => SelectedScraper?.Type == ScraperType.EmuMovies;
    public bool IsTheGamesDbSelected => SelectedScraper?.Type == ScraperType.TheGamesDB;
    public bool IsGoogleBooksSelected => SelectedScraper?.Type == ScraperType.GoogleBooks;
    public bool IsComicVineSelected => SelectedScraper?.Type == ScraperType.ComicVine;
    public bool IsApiKeyUsedSelected => IsTmdbSelected || IsTheGamesDbSelected || IsComicVineSelected || IsGoogleBooksSelected;
    public bool IsApiKeyRequiredSelected => IsTmdbSelected || IsTheGamesDbSelected || IsComicVineSelected;
    public bool IsLanguageSelectionSupported => IsTmdbSelected || IsTheGamesDbSelected || IsGoogleBooksSelected;
    
    // Handle property changes on the selected scraper to update UI hints
    partial void OnSelectedScraperChanged(ScraperConfig? oldValue, ScraperConfig? newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= OnScraperPropertyChanged;

        if (newValue != null)
            newValue.PropertyChanged += OnScraperPropertyChanged;

        RefreshHintProperties();
    }

    partial void OnSelectedEmulatorChanged(EmulatorConfig? oldValue, EmulatorConfig? newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= OnEmulatorPropertyChanged;

        if (newValue != null)
            newValue.PropertyChanged += OnEmulatorPropertyChanged;

        // Rebuild wrapper UI collection based on the newly selected emulator
        EmulatorNativeWrappers.Clear();
        EmulatorEnvironmentOverrides.Clear();

        UseGlobalWrapperDefaults = newValue?.NativeWrapperMode == EmulatorConfig.WrapperMode.Inherit;

        if (newValue?.NativeWrappersOverride != null)
        {
            foreach (var w in newValue.NativeWrappersOverride)
                EmulatorNativeWrappers.Add(new LaunchWrapperRow(w));
        }

        // Load environment overrides from the emulator model into the UI list
        if (newValue?.EnvironmentOverrides is { Count: > 0 })
        {
            foreach (var kv in newValue.EnvironmentOverrides)
            {
                EmulatorEnvironmentOverrides.Add(new EnvVarRow
                {
                    Key = kv.Key,
                    Value = kv.Value
                });
            }
        }
        
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmulatorXdgCustomSelected));
        OnPropertyChanged(nameof(IsEmulatorRunnerSelectionEnabled));
        RebuildSelectedEmulatorRunnerVersionOptions();
    }

    private void OnEmulatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmulatorConfig.XdgMode))
            OnPropertyChanged(nameof(IsEmulatorXdgCustomSelected));

        if (e.PropertyName == nameof(EmulatorConfig.UsesWinePrefix))
            OnPropertyChanged(nameof(IsEmulatorRunnerSelectionEnabled));

        if (e.PropertyName == nameof(EmulatorConfig.RunnerType))
            RebuildSelectedEmulatorRunnerVersionOptions();

        if (e.PropertyName == nameof(EmulatorConfig.DefaultRunnerVersionId))
        {
            RecomputeRunnerUsageCounts();
            RebuildRunnerReplacementOptions();
        }
    }

    private void OnAnyEmulatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmulatorConfig.RunnerType) ||
            e.PropertyName == nameof(EmulatorConfig.DefaultRunnerVersionId))
        {
            RecomputeRunnerUsageCounts();
            RebuildRunnerReplacementOptions();
            if (ReferenceEquals(sender, SelectedEmulator))
                RebuildSelectedEmulatorRunnerVersionOptions();
        }
    }

    private void OnScraperPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScraperConfig.Type))
        {
            RefreshHintProperties();
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedRunnerVersionChanged(RunnerVersionRow? value)
    {
        RebuildRunnerReplacementOptions();
        RemoveRunnerVersionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRunnerReplacementVisible));
        OnPropertyChanged(nameof(RunnerReplacementHint));
    }

    partial void OnSelectedRunnerReplacementChanged(RunnerVersionSelectionOption? value)
    {
        RemoveRunnerVersionCommand.NotifyCanExecuteChanged();
    }

    partial void OnRunnerVersionNameInputChanged(string value)
    {
        AddRunnerVersionCommand.NotifyCanExecuteChanged();
    }

    partial void OnRunnerVersionPathInputChanged(string value)
    {
        AddRunnerVersionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedEmulatorRunnerVersionChanged(RunnerVersionSelectionOption? value)
    {
        if (SelectedEmulator == null)
            return;

        SelectedEmulator.DefaultRunnerVersionId = string.IsNullOrWhiteSpace(value?.Id)
            ? null
            : value.Id;
    }

    partial void OnIsGeReleaseBusyChanged(bool value)
    {
        RefreshGeReleasesCommand.NotifyCanExecuteChanged();
        DownloadSelectedGeReleaseCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSettingsTabIndexChanged(int value)
    {
        // Tab order in SettingsView:
        // 0 = Emulators, 1 = Metadata, 2 = Wine/Proton versions, 3 = Misc.
        if (value != 2)
            return;

        if (_hasAutoLoadedGeReleases || IsGeReleaseBusy || GeProtonReleases.Count > 0)
            return;

        _hasAutoLoadedGeReleases = true;
        _ = RefreshGeReleasesAsync();
    }

    private bool CanSave()
    {
        // Prevent persisting half-configured scraper entries.
        return Scrapers.All(s => s.Type != ScraperType.None);
    }

    private void RefreshHintProperties()
    {
        OnPropertyChanged(nameof(IsTmdbSelected));
        OnPropertyChanged(nameof(IsIgdbSelected));
        OnPropertyChanged(nameof(IsEmuMoviesSelected));
        OnPropertyChanged(nameof(IsTheGamesDbSelected));
        OnPropertyChanged(nameof(IsGoogleBooksSelected));
        OnPropertyChanged(nameof(IsComicVineSelected));
        OnPropertyChanged(nameof(IsApiKeyUsedSelected));
        OnPropertyChanged(nameof(IsApiKeyRequiredSelected));
        OnPropertyChanged(nameof(IsLanguageSelectionSupported));
    }

    // --- Emulator wrapper editor actions ---

    private void AddEmulatorWrapper()
    {
        EmulatorNativeWrappers.Add(new LaunchWrapperRow());
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void RemoveEmulatorWrapper(LaunchWrapperRow? row)
    {
        if (row == null) return;
        EmulatorNativeWrappers.Remove(row);
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveEmulatorWrapperUp(LaunchWrapperRow? row)
    {
        if (row == null) return;

        var idx = EmulatorNativeWrappers.IndexOf(row);
        if (idx <= 0) return;

        EmulatorNativeWrappers.Move(idx, idx - 1);
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }

    private void MoveEmulatorWrapperDown(LaunchWrapperRow? row)
    {
        if (row == null) return;

        var idx = EmulatorNativeWrappers.IndexOf(row);
        if (idx < 0 || idx >= EmulatorNativeWrappers.Count - 1) return;

        EmulatorNativeWrappers.Move(idx, idx + 1);
        MoveEmulatorWrapperUpCommand.NotifyCanExecuteChanged();
        MoveEmulatorWrapperDownCommand.NotifyCanExecuteChanged();
    }
    
    // --- Emulator env-var editor actions ---

    private void AddEmulatorEnvVar()
    {
        EmulatorEnvironmentOverrides.Add(new EnvVarRow());
    }

    private void RemoveEmulatorEnvVar(EnvVarRow? row)
    {
        if (row == null) return;
        EmulatorEnvironmentOverrides.Remove(row);
    }

    private void ApplyPortableXdgPreset()
    {
        ApplyPortableOverrides(includeHome: false);
    }

    private void ApplyPortableXdgAndHomePreset()
    {
        ApplyPortableOverrides(includeHome: true);
    }

    private void ApplyPortableOverrides(bool includeHome)
    {
        if (SelectedEmulator == null)
            return;

        SelectedEmulator.XdgMode = EmulatorConfig.XdgOverrideMode.Custom;
        SelectedEmulator.XdgConfigPath = "Home/.config";
        SelectedEmulator.XdgDataPath = "Home/.local/share";
        SelectedEmulator.XdgCachePath = "Home/.cache";
        SelectedEmulator.XdgStatePath = "Home/.local/state";

        if (includeHome)
            UpsertEmulatorEnvVar("HOME", "Home");
    }

    private void UpsertEmulatorEnvVar(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        var existing = EmulatorEnvironmentOverrides
            .FirstOrDefault(row => string.Equals(row.Key?.Trim(), key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        EmulatorEnvironmentOverrides.Add(new EnvVarRow
        {
            Key = key,
            Value = value
        });
    }

    private bool CanAddRunnerVersion()
        => !string.IsNullOrWhiteSpace(RunnerVersionPathInput);

    private void AddRunnerVersion()
    {
        var path = RunnerVersionPathInput?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalizedPath = PreferPortableLaunchPaths
            ? ConvertPathToPortableIfInsideDataRoot(path) ?? path
            : path;

        var name = string.IsNullOrWhiteSpace(RunnerVersionNameInput)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : RunnerVersionNameInput.Trim();

        var row = new RunnerVersionRow
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Kind = DetectRunnerKindFromPath(path),
            SourceType = RunnerVersionSourceType.ExternalPath,
            Path = normalizedPath
        };

        RunnerVersions.Add(row);
        SelectedRunnerVersion = row;
        RunnerVersionNameInput = string.Empty;
        RunnerVersionPathInput = string.Empty;

        RecomputeRunnerUsageCounts();
        RebuildSelectedEmulatorRunnerVersionOptions();
        RebuildRunnerReplacementOptions();
    }

    private bool CanRemoveRunnerVersion()
    {
        if (SelectedRunnerVersion == null)
            return false;

        if (SelectedRunnerVersion.UsedByGames <= 0)
            return true;

        return !string.IsNullOrWhiteSpace(SelectedRunnerReplacement?.Id);
    }

    private void RemoveRunnerVersion()
    {
        if (SelectedRunnerVersion == null)
            return;

        var removed = SelectedRunnerVersion;
        var removedId = removed.Id;
        var replacementId = SelectedRunnerReplacement?.Id;

        if (removed.UsedByGames > 0 && string.IsNullOrWhiteSpace(replacementId))
            return;

        // Remap emulator-level defaults
        foreach (var emulator in Emulators)
        {
            if (string.Equals(emulator.DefaultRunnerVersionId, removedId, StringComparison.Ordinal))
                emulator.DefaultRunnerVersionId = replacementId;
        }

        // Remap item-level overrides
        var libraryChanged = false;
        if (_rootNodes.Count > 0)
        {
            foreach (var root in _rootNodes)
            {
                if (RemapRunnerVersionRecursive(root, removedId, replacementId))
                    libraryChanged = true;
            }
        }

        if (libraryChanged)
            LibraryModified = true;

        RunnerVersions.Remove(removed);
        SelectedRunnerVersion = RunnerVersions.FirstOrDefault();

        RecomputeRunnerUsageCounts();
        RebuildSelectedEmulatorRunnerVersionOptions();
        RebuildRunnerReplacementOptions();
    }

    private async Task BrowseRunnerVersionPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = RunnerVersionBrowseTitle,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0)
            return;

        RunnerVersionPathInput = result[0].Path.LocalPath;
    }

    private bool CanDownloadSelectedGeRelease()
        => !IsGeReleaseBusy && SelectedGeProtonRelease != null;

    private async Task RefreshGeReleasesAsync()
    {
        if (IsGeReleaseBusy)
            return;

        IsGeReleaseBusy = true;
        GeReleaseStatusText = T("Settings_GeProtonStatusLoading", "Loading release list...");

        try
        {
            var releases = await FetchGeProtonReleasesAsync();

            GeProtonReleases.Clear();
            foreach (var release in releases)
                GeProtonReleases.Add(release);

            SelectedGeProtonRelease = GeProtonReleases.FirstOrDefault();

            GeReleaseStatusText = releases.Count > 0
                ? string.Format(T("Settings_GeProtonStatusLoadedFormat", "Loaded {0} release(s)."), releases.Count)
                : T("Settings_GeProtonStatusNoReleases", "No downloadable GE-Proton release found.");
        }
        catch (Exception ex)
        {
            GeReleaseStatusText = string.Format(
                T("Settings_GeProtonStatusLoadFailedFormat", "Failed to load release list: {0}"),
                ex.Message);
        }
        finally
        {
            IsGeReleaseBusy = false;
        }
    }

    private async Task DownloadSelectedGeReleaseAsync()
    {
        var selected = SelectedGeProtonRelease;
        if (!CanDownloadSelectedGeRelease() || selected == null)
            return;

        IsGeReleaseBusy = true;
        GeReleaseStatusText = string.Format(
            T("Settings_GeProtonStatusDownloadingFormat", "Downloading {0} ..."),
            selected.TagName);

        try
        {
            var relativePath = await DownloadAndInstallGeReleaseAsync(selected);

            var existing = RunnerVersions.FirstOrDefault(r =>
                string.Equals(r.Path, relativePath, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.SourceType = RunnerVersionSourceType.ManagedDownload;
                existing.Kind = RunnerVersionKind.Proton;
                existing.ReleaseTag = selected.TagName;
                SelectedRunnerVersion = existing;
            }
            else
            {
                var folderName = Path.GetFileName(relativePath);
                var row = new RunnerVersionRow
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = folderName,
                    Kind = RunnerVersionKind.Proton,
                    SourceType = RunnerVersionSourceType.ManagedDownload,
                    Path = relativePath,
                    ReleaseTag = selected.TagName
                };

                RunnerVersions.Add(row);
                SelectedRunnerVersion = row;
            }

            RecomputeRunnerUsageCounts();
            RebuildSelectedEmulatorRunnerVersionOptions();
            RebuildRunnerReplacementOptions();

            GeReleaseStatusText = string.Format(
                T("Settings_GeProtonStatusInstalledFormat", "Installed: {0}"),
                relativePath);
        }
        catch (Exception ex)
        {
            GeReleaseStatusText = string.Format(
                T("Settings_GeProtonStatusInstallFailedFormat", "Installation failed: {0}"),
                ex.Message);
        }
        finally
        {
            IsGeReleaseBusy = false;
        }
    }

    private static async Task<List<GeProtonReleaseOption>> FetchGeProtonReleasesAsync()
    {
        var result = new List<GeProtonReleaseOption>();

        for (var page = 1; page <= GeProtonMaxPages; page++)
        {
            var url = $"{GeProtonReleasesApiUrl}?per_page={GeProtonPerPage}&page={page}";
            using var response = await GitHubHttpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

            if (json.RootElement.ValueKind != JsonValueKind.Array)
                break;

            var releaseCountOnPage = json.RootElement.GetArrayLength();
            if (releaseCountOnPage == 0)
                break;

            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (!TryGetStringProperty(release, "tag_name", out var tagName))
                    continue;

                if (release.TryGetProperty("draft", out var draftProp) && draftProp.ValueKind == JsonValueKind.True)
                    continue;

                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    if (!TryGetStringProperty(asset, "name", out var assetName))
                        continue;

                    if (!assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!TryGetStringProperty(asset, "browser_download_url", out var downloadUrl))
                        continue;

                    result.Add(new GeProtonReleaseOption(tagName, assetName, downloadUrl));
                    break; // One download asset per release is enough.
                }

                if (result.Count >= GeProtonMaxItems)
                    return result;
            }

            if (releaseCountOnPage < GeProtonPerPage)
                break;
        }

        return result;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task<string> DownloadAndInstallGeReleaseAsync(GeProtonReleaseOption release)
    {
        if (release == null)
            throw new ArgumentNullException(nameof(release));

        var baseRelativePath = Path.Combine("Emulators", "ProtonVersions");
        var baseAbsolutePath = AppPaths.ResolveDataPath(baseRelativePath);
        Directory.CreateDirectory(baseAbsolutePath);

        var tempArchivePath = Path.Combine(Path.GetTempPath(), $"retromind_ge_{Guid.NewGuid():N}.tar.gz");

        try
        {
            await using (var remote = await GitHubHttpClient.GetStreamAsync(release.DownloadUrl).ConfigureAwait(false))
            await using (var local = File.Create(tempArchivePath))
            {
                await remote.CopyToAsync(local).ConfigureAwait(false);
            }

            var rootFolder = DetectArchiveRootFolderName(tempArchivePath);
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                rootFolder = Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(release.AssetName));
            }

            rootFolder = SanitizeFolderName(rootFolder);
            if (string.IsNullOrWhiteSpace(rootFolder))
                throw new InvalidOperationException("Unable to determine installation folder name.");

            var targetDir = Path.Combine(baseAbsolutePath, rootFolder);
            var relativeInstalledPath = NormalizeRelativePath(Path.Combine(baseRelativePath, rootFolder));

            if (Directory.Exists(targetDir))
                return relativeInstalledPath;

            var stagingDir = Path.Combine(baseAbsolutePath, $".tmp_ge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDir);

            try
            {
                await using var archiveStream = File.OpenRead(tempArchivePath);
                await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzipStream, stagingDir, overwriteFiles: false);

                var expectedRoot = Path.Combine(stagingDir, rootFolder);
                if (Directory.Exists(expectedRoot))
                {
                    Directory.Move(expectedRoot, targetDir);
                }
                else
                {
                    var extractedDirs = Directory.GetDirectories(stagingDir);
                    if (extractedDirs.Length == 1)
                    {
                        Directory.Move(extractedDirs[0], targetDir);
                    }
                    else
                    {
                        Directory.CreateDirectory(targetDir);
                        MoveDirectoryContents(stagingDir, targetDir);
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, recursive: true);
                }
                catch
                {
                    // best-effort cleanup
                }
            }

            return relativeInstalledPath;
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchivePath))
                    File.Delete(tempArchivePath);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static string DetectArchiveRootFolderName(string tarGzPath)
    {
        if (string.IsNullOrWhiteSpace(tarGzPath) || !File.Exists(tarGzPath))
            return string.Empty;

        using var fileStream = File.OpenRead(tarGzPath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream, leaveOpen: false);

        TarEntry? entry;
        while ((entry = tarReader.GetNextEntry()) != null)
        {
            var name = entry.Name?.Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var firstSegment = name.Split(new[] { '/', '\\' }, 2)[0];
            if (!string.IsNullOrWhiteSpace(firstSegment))
                return firstSegment;
        }

        return string.Empty;
    }

    private static void MoveDirectoryContents(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
            return;

        Directory.CreateDirectory(destinationDir);

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(dir));
            if (Directory.Exists(target))
                throw new IOException($"Target directory already exists: {target}");

            Directory.Move(dir, target);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var target = Path.Combine(destinationDir, Path.GetFileName(file));
            if (File.Exists(target))
                throw new IOException($"Target file already exists: {target}");

            File.Move(file, target);
        }
    }

    private static string SanitizeFolderName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            result = result.Replace(c.ToString(), string.Empty, StringComparison.Ordinal);

        return result.Trim();
    }

    private static string NormalizeRelativePath(string path)
        => (path ?? string.Empty).Replace('\\', '/');

    private static RunnerVersionKind DetectRunnerKindFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RunnerVersionKind.Proton;

        var trimmed = path.Trim();
        var normalized = trimmed.Replace('\\', '/');
        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("/wine") || lower.Contains("wine64"))
            return RunnerVersionKind.Wine;

        if (lower.Contains("proton"))
            return RunnerVersionKind.Proton;

        try
        {
            var candidateDir = Directory.Exists(trimmed)
                ? trimmed
                : (File.Exists(trimmed) ? Path.GetDirectoryName(trimmed) : null);

            if (!string.IsNullOrWhiteSpace(candidateDir))
            {
                var wineBin = Path.Combine(candidateDir, "bin", "wine");
                var wineBin64 = Path.Combine(candidateDir, "bin", "wine64");
                if (File.Exists(wineBin) || File.Exists(wineBin64))
                    return RunnerVersionKind.Wine;

                var protonScript = Path.Combine(candidateDir, "proton");
                var protonFixes = Path.Combine(candidateDir, "protonfixes");
                if (File.Exists(protonScript) || Directory.Exists(protonFixes))
                    return RunnerVersionKind.Proton;
            }
        }
        catch
        {
            // best-effort detection
        }

        return RunnerVersionKind.Proton;
    }

    private void RebuildSelectedEmulatorRunnerVersionOptions()
    {
        SelectedEmulatorRunnerVersionOptions.Clear();
        SelectedEmulatorRunnerVersionOptions.Add(new RunnerVersionSelectionOption(
            id: null,
            name: Strings.NodeSettings_ModeNone));

        var selected = SelectedEmulator;
        var intent = InferEffectiveRunnerIntent(selected);

        foreach (var row in OrderRunnerRowsForIntent(intent))
        {
            var suffix = row.Kind == RunnerVersionKind.Wine ? "Wine" : "Proton";
            SelectedEmulatorRunnerVersionOptions.Add(new RunnerVersionSelectionOption(
                id: row.Id,
                name: $"{row.Name} ({suffix})",
                kind: row.Kind));
        }

        var defaultId = selected?.DefaultRunnerVersionId;
        SelectedEmulatorRunnerVersion = SelectedEmulatorRunnerVersionOptions.FirstOrDefault(o =>
            string.Equals(o.Id, defaultId, StringComparison.Ordinal))
            ?? SelectedEmulatorRunnerVersionOptions.FirstOrDefault();
    }

    private void RebuildRunnerReplacementOptions()
    {
        RunnerReplacementOptions.Clear();

        if (SelectedRunnerVersion == null)
        {
            SelectedRunnerReplacement = null;
            return;
        }

        foreach (var row in RunnerVersions.Where(r => !string.Equals(r.Id, SelectedRunnerVersion.Id, StringComparison.Ordinal)))
        {
            var suffix = row.Kind == RunnerVersionKind.Wine ? "Wine" : "Proton";
            RunnerReplacementOptions.Add(new RunnerVersionSelectionOption(
                id: row.Id,
                name: $"{row.Name} ({suffix})",
                kind: row.Kind));
        }

        var preferredKind = SelectedRunnerVersion.Kind;
        SelectedRunnerReplacement = RunnerReplacementOptions.FirstOrDefault(o => o.Kind == preferredKind)
            ?? RunnerReplacementOptions.FirstOrDefault();
    }

    private IEnumerable<RunnerVersionRow> OrderRunnerRowsForIntent(EmulatorConfig.RunnerIntent intent)
    {
        RunnerVersionKind? preferredKind = intent switch
        {
            EmulatorConfig.RunnerIntent.UmuProton => RunnerVersionKind.Proton,
            EmulatorConfig.RunnerIntent.Wine => RunnerVersionKind.Wine,
            _ => null
        };

        return RunnerVersions
            .OrderBy(r => preferredKind.HasValue && r.Kind == preferredKind.Value ? 0 : 1)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static EmulatorConfig.RunnerIntent InferEffectiveRunnerIntent(EmulatorConfig? emulator)
    {
        if (emulator == null)
            return EmulatorConfig.RunnerIntent.Auto;

        if (emulator.RunnerType != EmulatorConfig.RunnerIntent.Auto)
            return emulator.RunnerType;

        var executable = emulator.Path ?? string.Empty;
        if (executable.Contains("umu", StringComparison.OrdinalIgnoreCase) ||
            executable.Contains("proton", StringComparison.OrdinalIgnoreCase) ||
            emulator.EnvironmentOverrides.Keys.Any(k => string.Equals(k, "PROTONPATH", StringComparison.OrdinalIgnoreCase)))
        {
            return EmulatorConfig.RunnerIntent.UmuProton;
        }

        if (executable.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
            emulator.EnvironmentOverrides.Keys.Any(k => string.Equals(k, "WINE", StringComparison.OrdinalIgnoreCase)))
        {
            return EmulatorConfig.RunnerIntent.Wine;
        }

        return EmulatorConfig.RunnerIntent.Generic;
    }

    private void RecomputeRunnerUsageCounts()
    {
        _runnerUsageById.Clear();

        if (_rootNodes.Count > 0)
        {
            foreach (var root in _rootNodes)
                CountRunnerUsageRecursive(root, inheritedDefaultEmulatorId: null);
        }

        foreach (var row in RunnerVersions)
        {
            row.UsedByGames = _runnerUsageById.TryGetValue(row.Id, out var count)
                ? count
                : 0;
        }

        OnPropertyChanged(nameof(IsRunnerReplacementVisible));
        OnPropertyChanged(nameof(RunnerReplacementHint));
        RemoveRunnerVersionCommand.NotifyCanExecuteChanged();
    }

    private void CountRunnerUsageRecursive(MediaNode node, string? inheritedDefaultEmulatorId)
    {
        var effectiveDefaultEmulatorId = !string.IsNullOrWhiteSpace(node.DefaultEmulatorId)
            ? node.DefaultEmulatorId
            : inheritedDefaultEmulatorId;

        foreach (var item in node.Items)
        {
            var runnerId = ResolveEffectiveRunnerVersionId(item, effectiveDefaultEmulatorId);
            if (string.IsNullOrWhiteSpace(runnerId))
                continue;

            _runnerUsageById[runnerId] = _runnerUsageById.TryGetValue(runnerId, out var count)
                ? count + 1
                : 1;
        }

        foreach (var child in node.Children)
            CountRunnerUsageRecursive(child, effectiveDefaultEmulatorId);
    }

    private string? ResolveEffectiveRunnerVersionId(MediaItem item, string? inheritedDefaultEmulatorId)
    {
        if (!string.IsNullOrWhiteSpace(item.RunnerVersionId))
            return item.RunnerVersionId;

        if (item.MediaType != MediaType.Emulator)
            return null;

        EmulatorConfig? emulator = null;
        if (!string.IsNullOrWhiteSpace(item.EmulatorId))
        {
            emulator = Emulators.FirstOrDefault(e => string.Equals(e.Id, item.EmulatorId, StringComparison.Ordinal));
        }
        else if (string.IsNullOrWhiteSpace(item.LauncherPath) && !string.IsNullOrWhiteSpace(inheritedDefaultEmulatorId))
        {
            emulator = Emulators.FirstOrDefault(e => string.Equals(e.Id, inheritedDefaultEmulatorId, StringComparison.Ordinal));
        }

        return emulator?.DefaultRunnerVersionId;
    }

    private static bool RemapRunnerVersionRecursive(MediaNode node, string removedId, string? replacementId)
    {
        var changed = false;

        foreach (var item in node.Items)
        {
            if (string.Equals(item.RunnerVersionId, removedId, StringComparison.Ordinal))
            {
                item.RunnerVersionId = replacementId;
                changed = true;
            }
        }

        foreach (var child in node.Children)
        {
            if (RemapRunnerVersionRecursive(child, removedId, replacementId))
                changed = true;
        }

        return changed;
    }
    
    // --- Actions ---

    private void AddEmulator()
    {
        var newEmu = new EmulatorConfig { Name = Strings.Profile_New };
        newEmu.PropertyChanged += OnAnyEmulatorPropertyChanged;
        Emulators.Add(newEmu);
        SelectedEmulator = newEmu; 
    }

    private void RemoveEmulator()
    {
        if (SelectedEmulator != null)
        {
            SelectedEmulator.PropertyChanged -= OnAnyEmulatorPropertyChanged;
            Emulators.Remove(SelectedEmulator);
            SelectedEmulator = null;
            RecomputeRunnerUsageCounts();
            RebuildSelectedEmulatorRunnerVersionOptions();
        }
    }

    private void AddScraper()
    {
        var newScraper = new ScraperConfig
        {
            // Start unconfigured; user picks the provider manually.
            Type = ScraperType.None
        };
        
        Scrapers.Add(newScraper);
        SelectedScraper = newScraper;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void RemoveScraper()
    {
        if (SelectedScraper != null)
        {
            // Unsubscribe from event to prevent leaks
            SelectedScraper.PropertyChanged -= OnScraperPropertyChanged;
            
            Scrapers.Remove(SelectedScraper);
            SelectedScraper = null;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }
    
    private async Task ConvertExistingToPortableAsync()
    {
        // Forward the request to whoever is listening (typically MainWindowViewModel)
        if (RequestPortableMigration != null)
        {
            try
            {
                await RequestPortableMigration.Invoke();
            }
            catch
            {
                // Best-effort: migration errors are handled by the subscriber
            }
        }
    }

    private async Task ChangeParentalPasswordAsync()
    {
        if (RequestParentalPasswordChange == null)
            return;

        try
        {
            await RequestParentalPasswordChange.Invoke();
        }
        catch
        {
            // best-effort: caller owns error handling/UI feedback
        }
    }
    
    private void Save()
    {
        if (!CanSave())
            return;

        // Persist emulator wrapper & env configuration from UI into the selected emulator model
        if (SelectedEmulator != null)
        {
            if (UseGlobalWrapperDefaults)
            {
                SelectedEmulator.NativeWrapperMode = EmulatorConfig.WrapperMode.Inherit;
                SelectedEmulator.NativeWrappersOverride = null;
            }
            else
            {
                var wrappers = EmulatorNativeWrappers
                    .Select(x => x.ToModel())
                    .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                    .ToList();

                if (PreferPortableLaunchPaths)
                    ConvertWrapperPathsToPortable(wrappers);

                SelectedEmulator.NativeWrappersOverride = wrappers;
                SelectedEmulator.NativeWrapperMode = wrappers.Count == 0
                    ? EmulatorConfig.WrapperMode.None
                    : EmulatorConfig.WrapperMode.Override;
            }
            
            // Sync environment overrides back into the model dictionary
            SelectedEmulator.EnvironmentOverrides.Clear();
            foreach (var row in EmulatorEnvironmentOverrides)
            {
                if (string.IsNullOrWhiteSpace(row.Key))
                    continue;

                SelectedEmulator.EnvironmentOverrides[row.Key.Trim()] = row.Value ?? string.Empty;
            }
        }

        if (PreferPortableLaunchPaths)
        {
            foreach (var emulator in Emulators)
            {
                if (emulator == null)
                    continue;

                ConvertEmulatorPathsToPortable(emulator);
            }

            ConvertWrapperPathsToPortable(_appSettings.DefaultNativeWrappers);

            foreach (var runner in RunnerVersions)
            {
                runner.Path = ConvertPathToPortableIfInsideDataRoot(runner.Path) ?? runner.Path;
            }
        }
        
        // Persist changes back to the main settings object
        _appSettings.Emulators.Clear();
        _appSettings.Emulators.AddRange(Emulators);

        _appSettings.Scrapers.Clear();
        _appSettings.Scrapers.AddRange(Scrapers);

        _appSettings.RunnerVersions.Clear();
        _appSettings.RunnerVersions.AddRange(RunnerVersions.Select(r => r.ToModel()));

        _appSettings.SteamLibraryPaths.Clear();
        _appSettings.SteamLibraryPaths.AddRange(SteamLibraryPaths);

        _appSettings.HeroicGogConfigPaths.Clear();
        _appSettings.HeroicGogConfigPaths.AddRange(HeroicGogConfigPaths);

        _appSettings.HeroicEpicConfigPaths.Clear();
        _appSettings.HeroicEpicConfigPaths.AddRange(HeroicEpicConfigPaths);
        
        RequestClose?.Invoke();
    }

    private async Task BrowsePathAsync()
    {
        if (SelectedEmulator == null) return;

        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Emulator Executable",
            AllowMultiple = false
        });

        if (result != null && result.Count > 0)
        {
            var path = result[0].Path.LocalPath;
            if (PreferPortableLaunchPaths &&
                TryMakeDataRelativeIfInsideDataRoot(path, out var relativePath))
            {
                SelectedEmulator.Path = relativePath;
            }
            else
            {
                SelectedEmulator.Path = path;
            }
        }
    }

    private static bool TryMakeDataRelativeIfInsideDataRoot(string absolutePath, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(absolutePath))
            return false;

        if (!Path.IsPathRooted(absolutePath))
            return false;

        var dataRoot = Path.GetFullPath(AppPaths.DataRoot);
        var dataRootWithSep = dataRoot.EndsWith(Path.DirectorySeparatorChar)
            ? dataRoot
            : dataRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(absolutePath);

        if (string.Equals(fullPath, dataRoot, StringComparison.Ordinal) ||
            fullPath.StartsWith(dataRootWithSep, StringComparison.Ordinal))
        {
            relativePath = Path.GetRelativePath(dataRoot, fullPath);
            return true;
        }

        return false;
    }

    private static void ConvertEmulatorPathsToPortable(EmulatorConfig emulator)
    {
        emulator.Path = ConvertPathToPortableIfInsideDataRoot(emulator.Path) ?? emulator.Path;
        emulator.XdgConfigPath = ConvertPathToPortableIfInsideDataRoot(emulator.XdgConfigPath);
        emulator.XdgDataPath = ConvertPathToPortableIfInsideDataRoot(emulator.XdgDataPath);
        emulator.XdgCachePath = ConvertPathToPortableIfInsideDataRoot(emulator.XdgCachePath);
        emulator.XdgStatePath = ConvertPathToPortableIfInsideDataRoot(emulator.XdgStatePath);
        ConvertWrapperPathsToPortable(emulator.NativeWrappersOverride);

        if (emulator.EnvironmentOverrides is not { Count: > 0 })
            return;

        var keys = emulator.EnvironmentOverrides.Keys.ToList();
        foreach (var key in keys)
        {
            if (!EnvironmentPathHelper.IsDataRootPathKey(key))
                continue;

            if (!emulator.EnvironmentOverrides.TryGetValue(key, out var rawValue))
                continue;

            var converted = ConvertPathToPortableIfInsideDataRoot(rawValue);
            if (!string.IsNullOrWhiteSpace(converted))
                emulator.EnvironmentOverrides[key] = converted;
        }
    }

    private static string? ConvertPathToPortableIfInsideDataRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
            return trimmed;

        return TryMakeDataRelativeIfInsideDataRoot(trimmed, out var relativePath)
            ? relativePath
            : trimmed;
    }

    private static void ConvertWrapperPathsToPortable(System.Collections.Generic.List<LaunchWrapper>? wrappers)
    {
        if (wrappers is not { Count: > 0 })
            return;

        foreach (var wrapper in wrappers)
        {
            if (wrapper == null || string.IsNullOrWhiteSpace(wrapper.Path))
                continue;

            wrapper.Path = ConvertPathToPortableIfInsideDataRoot(wrapper.Path) ?? wrapper.Path;
        }
    }

    private async Task BrowseSteamLibraryPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectSteamLibraryFolder,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0) return;

        AddSteamLibraryPath(result[0].Path.LocalPath);
    }

    private async Task BrowseHeroicGogPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectHeroicGogFolder,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0) return;

        AddHeroicGogPath(result[0].Path.LocalPath);
    }

    private async Task BrowseHeroicEpicPathAsync()
    {
        var provider = ResolveStorageProvider();
        if (provider == null) return;

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Dialog_SelectHeroicEpicFolder,
            AllowMultiple = false
        });

        if (result == null || result.Count == 0) return;

        AddHeroicEpicPath(result[0].Path.LocalPath);
    }

    private void AddSteamLibraryPath()
    {
        AddSteamLibraryPath(SteamLibraryPathInput);
    }

    private void AddSteamLibraryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.Trim();
        var normalized = NormalizePathSafe(trimmed);

        if (SteamLibraryPaths.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SteamLibraryPathInput = string.Empty;
            return;
        }

        SteamLibraryPaths.Add(normalized);
        SteamLibraryPathInput = string.Empty;
    }

    private void RemoveSteamLibraryPath()
    {
        if (SelectedSteamLibraryPath == null) return;
        SteamLibraryPaths.Remove(SelectedSteamLibraryPath);
        SelectedSteamLibraryPath = null;
    }

    private void AddHeroicGogPath()
    {
        AddHeroicGogPath(HeroicGogPathInput);
    }

    private void AddHeroicGogPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.Trim();
        var normalized = NormalizePathSafe(trimmed);

        if (HeroicGogConfigPaths.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            HeroicGogPathInput = string.Empty;
            return;
        }

        HeroicGogConfigPaths.Add(normalized);
        HeroicGogPathInput = string.Empty;
    }

    private void RemoveHeroicGogPath()
    {
        if (SelectedHeroicGogPath == null) return;
        HeroicGogConfigPaths.Remove(SelectedHeroicGogPath);
        SelectedHeroicGogPath = null;
    }

    private void AddHeroicEpicPath()
    {
        AddHeroicEpicPath(HeroicEpicPathInput);
    }

    private void AddHeroicEpicPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var trimmed = path.Trim();
        var normalized = NormalizePathSafe(trimmed);

        if (HeroicEpicConfigPaths.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            HeroicEpicPathInput = string.Empty;
            return;
        }

        HeroicEpicConfigPaths.Add(normalized);
        HeroicEpicPathInput = string.Empty;
    }

    private void RemoveHeroicEpicPath()
    {
        if (SelectedHeroicEpicPath == null) return;
        HeroicEpicConfigPaths.Remove(SelectedHeroicEpicPath);
        SelectedHeroicEpicPath = null;
    }

    private static string NormalizePathSafe(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private IStorageProvider? ResolveStorageProvider()
    {
        // Try to resolve StorageProvider:
        // 1. Injected property (Priority)
        // 2. Fallback to active window (Pragmatic approach for dialogs)
        var provider = StorageProvider;
        if (provider == null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var activeWindow = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            provider = activeWindow?.StorageProvider;
        }

        return provider;
    }

    private static HttpClient CreateGitHubHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };

        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Retromind", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (SelectedScraper != null)
            SelectedScraper.PropertyChanged -= OnScraperPropertyChanged;

        if (SelectedEmulator != null)
            SelectedEmulator.PropertyChanged -= OnEmulatorPropertyChanged;

        foreach (var emulator in Emulators)
            emulator.PropertyChanged -= OnAnyEmulatorPropertyChanged;
    }
}
