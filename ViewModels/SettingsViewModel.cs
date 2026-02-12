using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for the application settings dialog
/// Manages emulator profiles and scraper configurations
/// </summary>
public partial class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _appSettings;
    private bool _disposed;

    // Currently selected emulator profile
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(RemoveEmulatorCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowsePathCommand))]
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
    
    // Filtered list of scraper types for the UI (hiding 'None')
    public ScraperType[] AvailableScraperTypes { get; } = Enum.GetValues<ScraperType>()
        .Where(t => t != ScraperType.None)
        .ToArray();
    
    // UI Collections
    public ObservableCollection<EmulatorConfig> Emulators { get; } = new();
    public ObservableCollection<ScraperConfig> Scrapers { get; } = new();
    public ObservableCollection<string> SteamLibraryPaths { get; } = new();

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
            OnPropertyChanged();
        }
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
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmulatorWrapperListEnabled))]
    private bool _useGlobalWrapperDefaults;

    public bool IsEmulatorWrapperListEnabled => !UseGlobalWrapperDefaults;

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
    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand BrowsePathCommand { get; }
    public IAsyncRelayCommand BrowseSteamLibraryPathCommand { get; }
    public IAsyncRelayCommand ConvertExistingToPortableCommand { get; }

    // Emulator wrapper editor commands
    public IRelayCommand AddEmulatorWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveEmulatorWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveEmulatorWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveEmulatorWrapperDownCommand { get; }
    
    // Emulator environment editor commands
    public IRelayCommand AddEmulatorEnvVarCommand { get; }
    public IRelayCommand<EnvVarRow?> RemoveEmulatorEnvVarCommand { get; }

    public event Action? RequestClose;
    
    /// <summary>
    /// Raised when the user explicitly requests to convert existing launch
    /// file paths under the Retromind folder into portable (DataRoot-relative) paths
    /// The main window view model is responsible for performing the actual migration
    /// </summary>
    public event Func<Task>? RequestPortableMigration;

    // Optional dependency injection for file dialogs (better for testing)
    public IStorageProvider? StorageProvider { get; set; }

    public SettingsViewModel(AppSettings settings)
    {
        _appSettings = settings ?? throw new ArgumentNullException(nameof(settings));

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

        AddEmulatorCommand = new RelayCommand(AddEmulator);
        RemoveEmulatorCommand = new RelayCommand(RemoveEmulator, () => SelectedEmulator != null);
        
        AddScraperCommand = new RelayCommand(AddScraper);
        RemoveScraperCommand = new RelayCommand(RemoveScraper, () => SelectedScraper != null);
        
        AddSteamLibraryPathCommand = new RelayCommand(AddSteamLibraryPath);
        RemoveSteamLibraryPathCommand = new RelayCommand(RemoveSteamLibraryPath, () => SelectedSteamLibraryPath != null);
        
        SaveCommand = new RelayCommand(Save);
        BrowsePathCommand = new AsyncRelayCommand(BrowsePathAsync, () => SelectedEmulator != null);
        BrowseSteamLibraryPathCommand = new AsyncRelayCommand(BrowseSteamLibraryPathAsync);
        
        // command to request migration to portable launch paths
        ConvertExistingToPortableCommand = new AsyncRelayCommand(ConvertExistingToPortableAsync);
        
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
    }

    // --- Computed Properties for UI Hints ---
    
    public bool IsTmdbSelected => SelectedScraper?.Type == ScraperType.TMDB;
    public bool IsIgdbSelected => SelectedScraper?.Type == ScraperType.IGDB;
    public bool IsEmuMoviesSelected => SelectedScraper?.Type == ScraperType.EmuMovies;
    
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
    }

    private void OnScraperPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScraperConfig.Type))
        {
            RefreshHintProperties();
        }
    }

    private void RefreshHintProperties()
    {
        OnPropertyChanged(nameof(IsTmdbSelected));
        OnPropertyChanged(nameof(IsIgdbSelected));
        OnPropertyChanged(nameof(IsEmuMoviesSelected));
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
    
    // --- Actions ---

    private void AddEmulator()
    {
        var newEmu = new EmulatorConfig { Name = Strings.Profile_New };
        Emulators.Add(newEmu);
        SelectedEmulator = newEmu; 
    }

    private void RemoveEmulator()
    {
        if (SelectedEmulator != null)
        {
            Emulators.Remove(SelectedEmulator);
            SelectedEmulator = null;
        }
    }

    private void AddScraper()
    {
        var newScraper = new ScraperConfig 
        { 
            // Default: Name="New Scraper", Type=None
        };
        
        Scrapers.Add(newScraper);
        SelectedScraper = newScraper;
    }

    private void RemoveScraper()
    {
        if (SelectedScraper != null)
        {
            // Unsubscribe from event to prevent leaks
            SelectedScraper.PropertyChanged -= OnScraperPropertyChanged;
            
            Scrapers.Remove(SelectedScraper);
            SelectedScraper = null;
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
    
    private void Save()
    {
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
        
        // Persist changes back to the main settings object
        _appSettings.Emulators.Clear();
        _appSettings.Emulators.AddRange(Emulators);

        _appSettings.Scrapers.Clear();
        _appSettings.Scrapers.AddRange(Scrapers);

        _appSettings.SteamLibraryPaths.Clear();
        _appSettings.SteamLibraryPaths.AddRange(SteamLibraryPaths);
        
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
            SelectedEmulator.Path = result[0].Path.LocalPath;
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (SelectedScraper != null)
            SelectedScraper.PropertyChanged -= OnScraperPropertyChanged;
    }
}
