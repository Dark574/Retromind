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

    // Commands
    public IRelayCommand AddEmulatorCommand { get; }
    public IRelayCommand RemoveEmulatorCommand { get; }
    public IRelayCommand AddScraperCommand { get; }
    public IRelayCommand RemoveScraperCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand BrowsePathCommand { get; }

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