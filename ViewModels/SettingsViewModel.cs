using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;

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
    private readonly SettingsService _settingsService;
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
    [NotifyCanExecuteChangedFor(nameof(RemoveHeroicEpicPathCommand))]
    private string? _selectedHeroicEpicPath;

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
    [NotifyCanExecuteChangedFor(nameof(RemoveRunnerVersionCommand))]
    private bool _isRemovingRunnerVersion;

    [ObservableProperty]
    private string _runnerVersionStatusText = string.Empty;

    [ObservableProperty]
    private string? _selectedEmulatorRunnerVersionId;

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

    /// <summary>
    /// Controls whether leading title articles should be ignored during title-based sorting.
    /// Explicit SortTitle values are never modified by this option.
    /// </summary>
    public bool IgnoreLeadingArticlesInSort
    {
        get => _appSettings.IgnoreLeadingArticlesInSort;
        set
        {
            if (_appSettings.IgnoreLeadingArticlesInSort == value)
                return;

            _appSettings.IgnoreLeadingArticlesInSort = value;
            MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(value);
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
    public string SortingSectionTitle => T("Settings_SectionSorting", "Sorting");
    public string IgnoreLeadingArticlesInSortText =>
        T("Settings_IgnoreLeadingArticlesInSort", "Ignore leading articles in title sorting");
    public string IgnoreLeadingArticlesInSortHint =>
        T("Settings_IgnoreLeadingArticlesInSort_Hint", "Applies to Title only. Sort Title is always used as entered.");
    public string ScraperImportHint => T("Settings_ScraperImportHint", "Applies to both manual scrape and bulk scrape.");
    public string ScraperExistingDataModeText => T("Settings_ScraperExistingDataMode", "If data already exists:");
    public string ScraperBulkAppendAssetsText => T("Settings_ScraperBulkAssetConflictPrompt", "In manual and bulk scrape, append new artwork when artwork already exists");
    public string ScraperBulkAppendAssetsHint => T("Settings_ScraperBulkAssetConflictHint", "Missing artwork is always imported.");
    public string ScraperMetadataFieldsText => T("Settings_ScraperMetadataFields", "Metadata fields");
    public string ScraperAssetFieldsText => T("Settings_ScraperAssetFields", "Artwork / assets");

    public string ParentalSectionTitle => T("Settings_SectionParental", "Parental control");
    public string ChangeParentalPasswordText => T("Settings_ChangeParentalPassword", "Change parental password");
    public string SettingsTabEmulatorsShort => T("Settings_TabEmulatorsShort", "Emu");
    public string SettingsTabMetadataShort => T("Settings_TabMetadataShort", "Meta");
    public string SettingsTabRunnerShort => T("Settings_TabRunnerShort", "Runner");
    public string SettingsTabMiscShort => T("Settings_TabMiscShort", "Misc");
    public string RunnerVersionsTabTitle => T("Settings_TabRunnerVersions", "Wine/Proton versions");
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
    public IRelayCommand AddHeroicEpicPathCommand { get; }
    public IRelayCommand RemoveHeroicEpicPathCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand BrowsePathCommand { get; }
    public IAsyncRelayCommand BrowseSteamLibraryPathCommand { get; }
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
    public IAsyncRelayCommand RemoveRunnerVersionCommand { get; }
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

    /// <summary>
    /// Raised before a configured runner is removed. The settings window owns
    /// the confirmation dialog; the view model owns the removal policy.
    /// </summary>
    public event Func<RunnerVersionRow, Task<bool>>? RequestRunnerVersionRemovalConfirmation;

    public bool LibraryModified { get; private set; }

    // Optional dependency injection for file dialogs (better for testing)
    public IStorageProvider? StorageProvider { get; set; }

    public SettingsViewModel(
        AppSettings settings,
        SettingsService settingsService,
        ObservableCollection<MediaNode>? rootNodes = null)
    {
        _appSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _rootNodes = rootNodes ?? new ObservableCollection<MediaNode>();

        MediaSortHelper.SetIgnoreLeadingArticlesInTitleSort(_appSettings.IgnoreLeadingArticlesInSort);

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

        if (_appSettings.HeroicEpicConfigPaths != null)
        {
            foreach (var path in _appSettings.HeroicEpicConfigPaths)
                HeroicEpicConfigPaths.Add(path);
        }

        if (_appSettings.RunnerVersions != null)
        {
            foreach (var version in _appSettings.RunnerVersions)
            {
                if (string.IsNullOrWhiteSpace(version.Id))
                    version.Id = Guid.NewGuid().ToString();

                RunnerVersions.Add(new RunnerVersionRow(version));
            }
        }

        SortRunnerVersions();

        AddEmulatorCommand = new RelayCommand(AddEmulator);
        RemoveEmulatorCommand = new RelayCommand(RemoveEmulator, () => SelectedEmulator != null);
        
        AddScraperCommand = new RelayCommand(AddScraper);
        RemoveScraperCommand = new RelayCommand(RemoveScraper, () => SelectedScraper != null);
        
        AddSteamLibraryPathCommand = new RelayCommand(AddSteamLibraryPath);
        RemoveSteamLibraryPathCommand = new RelayCommand(RemoveSteamLibraryPath, () => SelectedSteamLibraryPath != null);
        AddHeroicEpicPathCommand = new RelayCommand(AddHeroicEpicPath);
        RemoveHeroicEpicPathCommand = new RelayCommand(RemoveHeroicEpicPath, () => SelectedHeroicEpicPath != null);
        
        SaveCommand = new RelayCommand(Save, CanSave);
        BrowsePathCommand = new AsyncRelayCommand(BrowsePathAsync, () => SelectedEmulator != null);
        BrowseSteamLibraryPathCommand = new AsyncRelayCommand(BrowseSteamLibraryPathAsync);
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
        RemoveRunnerVersionCommand = new AsyncRelayCommand(RemoveRunnerVersionAsync, CanRemoveRunnerVersion);
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
    }

    private void OnAnyEmulatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmulatorConfig.RunnerType) ||
            e.PropertyName == nameof(EmulatorConfig.DefaultRunnerVersionId))
        {
            RecomputeRunnerUsageCounts();
            RebuildRunnerReplacementOptions();

            if (!ReferenceEquals(sender, SelectedEmulator))
                return;

            if (e.PropertyName == nameof(EmulatorConfig.RunnerType))
                RebuildSelectedEmulatorRunnerVersionOptions();
            else
                SyncSelectedEmulatorRunnerVersionSelection();
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

    partial void OnSelectedEmulatorRunnerVersionIdChanged(string? value)
    {
        if (SelectedEmulator == null)
            return;

        SelectedEmulator.DefaultRunnerVersionId = string.IsNullOrWhiteSpace(value)
            ? null
            : value;
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

}
