using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
/// ViewModel for the application settings dialog.
/// Manages emulator profiles and scraper configurations.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _appSettings;

    // Currently selected emulator profile
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(RemoveEmulatorCommand))]
    [NotifyCanExecuteChangedFor(nameof(BrowsePathCommand))]
    private EmulatorConfig? _selectedEmulator;

    // Currently selected scraper config
    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(RemoveScraperCommand))]
    private ScraperConfig? _selectedScraper;
    
    // Filtered list of scraper types for the UI (hiding 'None')
    public ScraperType[] AvailableScraperTypes { get; } = Enum.GetValues<ScraperType>()
        .Where(t => t != ScraperType.None)
        .ToArray();
    
    // UI Collections
    public ObservableCollection<EmulatorConfig> Emulators { get; } = new();
    public ObservableCollection<ScraperConfig> Scrapers { get; } = new();

    /// <summary>
    /// Lightweight UI row for editing a single wrapper (Path + Args) on emulator level.
    /// Mirrors <see cref="LaunchWrapper"/> but keeps the model decoupled from live editing.
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
    /// Current wrapper mode of the selected emulator (tri-state).
    /// Direct proxy for SelectedEmulator.NativeWrapperMode, but safe when null.
    /// </summary>
    public EmulatorConfig.WrapperMode EmulatorWrapperMode
    {
        get => SelectedEmulator?.NativeWrapperMode ?? EmulatorConfig.WrapperMode.Inherit;
        set
        {
            if (SelectedEmulator == null) return;
            if (SelectedEmulator.NativeWrapperMode == value) return;

            SelectedEmulator.NativeWrapperMode = value;
            OnPropertyChanged(nameof(EmulatorWrapperMode));
            OnPropertyChanged(nameof(IsNativeWrapperInherit));
            OnPropertyChanged(nameof(IsNativeWrapperNone));
            OnPropertyChanged(nameof(IsNativeWrapperOverride));
        }
    }

    /// <summary>
    /// UI collection bound to the emulator wrapper editor.
    /// This list is re-synchronized when SelectedEmulator changes.
    /// </summary>
    public ObservableCollection<LaunchWrapperRow> EmulatorNativeWrappers { get; } = new();

    public bool IsNativeWrapperInherit
    {
        get => EmulatorWrapperMode == EmulatorConfig.WrapperMode.Inherit;
        set
        {
            if (!value) return;
            EmulatorWrapperMode = EmulatorConfig.WrapperMode.Inherit;
        }
    }

    public bool IsNativeWrapperNone
    {
        get => EmulatorWrapperMode == EmulatorConfig.WrapperMode.None;
        set
        {
            if (!value) return;
            EmulatorWrapperMode = EmulatorConfig.WrapperMode.None;
        }
    }

    public bool IsNativeWrapperOverride
    {
        get => EmulatorWrapperMode == EmulatorConfig.WrapperMode.Override;
        set
        {
            if (!value) return;
            EmulatorWrapperMode = EmulatorConfig.WrapperMode.Override;
        }
    }
    
    // Commands
    public IRelayCommand AddEmulatorCommand { get; }
    public IRelayCommand RemoveEmulatorCommand { get; }
    public IRelayCommand AddScraperCommand { get; }
    public IRelayCommand RemoveScraperCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand BrowsePathCommand { get; }

    // Emulator wrapper editor commands
    public IRelayCommand AddEmulatorWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> RemoveEmulatorWrapperCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveEmulatorWrapperUpCommand { get; }
    public IRelayCommand<LaunchWrapperRow?> MoveEmulatorWrapperDownCommand { get; }
    
    public event Action? RequestClose;

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

        AddEmulatorCommand = new RelayCommand(AddEmulator);
        RemoveEmulatorCommand = new RelayCommand(RemoveEmulator, () => SelectedEmulator != null);
        
        AddScraperCommand = new RelayCommand(AddScraper);
        RemoveScraperCommand = new RelayCommand(RemoveScraper, () => SelectedScraper != null);
        
        SaveCommand = new RelayCommand(Save);
        BrowsePathCommand = new AsyncRelayCommand(BrowsePathAsync, () => SelectedEmulator != null);
        
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
        // Rebuild wrapper UI collection based on the newly selected emulator.
        EmulatorNativeWrappers.Clear();

        if (newValue?.NativeWrappersOverride == null)
        {
            EmulatorWrapperMode = newValue?.NativeWrapperMode ?? EmulatorConfig.WrapperMode.Inherit;
        }
        else if (newValue.NativeWrappersOverride.Count == 0)
        {
            EmulatorWrapperMode = EmulatorConfig.WrapperMode.None;
        }
        else
        {
            EmulatorWrapperMode = EmulatorConfig.WrapperMode.Override;

            foreach (var w in newValue.NativeWrappersOverride)
            {
                EmulatorNativeWrappers.Add(new LaunchWrapperRow(w));
            }
        }

        OnPropertyChanged(nameof(EmulatorWrapperMode));
        OnPropertyChanged(nameof(IsNativeWrapperInherit));
        OnPropertyChanged(nameof(IsNativeWrapperNone));
        OnPropertyChanged(nameof(IsNativeWrapperOverride));

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
    
    private void Save()
    {
        // Persist emulator wrapper configuration from UI into the model
        if (SelectedEmulator != null)
        {
            switch (EmulatorWrapperMode)
            {
                case EmulatorConfig.WrapperMode.Inherit:
                    SelectedEmulator.NativeWrappersOverride = null;
                    break;

                case EmulatorConfig.WrapperMode.None:
                    SelectedEmulator.NativeWrappersOverride = new System.Collections.Generic.List<LaunchWrapper>();
                    break;

                case EmulatorConfig.WrapperMode.Override:
                    SelectedEmulator.NativeWrappersOverride = EmulatorNativeWrappers
                        .Select(x => x.ToModel())
                        .Where(x => !string.IsNullOrWhiteSpace(x.Path))
                        .ToList();
                    break;
            }
        }
        
        // Persist changes back to the main settings object
        _appSettings.Emulators.Clear();
        _appSettings.Emulators.AddRange(Emulators);

        _appSettings.Scrapers.Clear();
        _appSettings.Scrapers.AddRange(Scrapers);
        
        RequestClose?.Invoke();
    }

    private async Task BrowsePathAsync()
    {
        if (SelectedEmulator == null) return;

        // Try to resolve StorageProvider:
        // 1. Injected property (Priority)
        // 2. Fallback to active window (Pragmatic approach for dialogs)
        var provider = StorageProvider;
        if (provider == null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var activeWindow = desktop.Windows.LastOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            provider = activeWindow?.StorageProvider;
        }

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
}