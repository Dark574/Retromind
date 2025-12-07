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

public partial class SettingsViewModel : ViewModelBase
{
    // Referenz auf die globalen Settings
    private readonly AppSettings _appSettings;

    // Der aktuell in der Liste ausgewählte Emulator
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RemoveEmulatorCommand))]
    private EmulatorConfig? _selectedEmulator;

    // Der aktuell in der Liste ausgewählte Scraper
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RemoveScraperCommand))]
    private ScraperConfig? _selectedScraper;
    
    // ÄNDERUNG: Wir filtern "None" aus der Liste für die ComboBox heraus.
    public ScraperType[] AvailableScraperTypes { get; } = Enum.GetValues<ScraperType>()
        .Where(t => t != ScraperType.None)
        .ToArray();
    
    public SettingsViewModel(AppSettings settings)
    {
        _appSettings = settings;

        // Vorhandene Emulatoren in die ObservableCollection laden
        foreach (var emu in _appSettings.Emulators) Emulators.Add(emu);

        // Vorhandene Scraper laden
        foreach (var scraper in _appSettings.Scrapers) Scrapers.Add(scraper);

        AddEmulatorCommand = new RelayCommand(AddEmulator);
        RemoveEmulatorCommand = new RelayCommand(RemoveEmulator, () => SelectedEmulator != null);
        
        AddScraperCommand = new RelayCommand(AddScraper);
        RemoveScraperCommand = new RelayCommand(RemoveScraper, () => SelectedScraper != null);
        
        SaveCommand = new RelayCommand(Save);
        BrowsePathCommand = new AsyncRelayCommand(BrowsePathAsync);
    }

    // --- Dynamische Properties für die UI-Hinweise ---
    // Diese hängen vom aktuell gewählten Scraper ab
    
    public bool IsTmdbSelected => SelectedScraper?.Type == ScraperType.TMDB;
    public bool IsIgdbSelected => SelectedScraper?.Type == ScraperType.IGDB;
    public bool IsEmuMoviesSelected => SelectedScraper?.Type == ScraperType.EmuMovies;
    // ScreenScraper gibt es ja nicht mehr :)
    
    // Wenn sich der ausgewählte Scraper ändert, müssen wir:
    // 1. Die UI updaten (Hints anzeigen/verstecken)
    // 2. Uns am neuen Objekt "anhängen", um Änderungen am Typ mitzubekommen
    partial void OnSelectedScraperChanged(ScraperConfig? oldValue, ScraperConfig? newValue)
    {
        RemoveScraperCommand.NotifyCanExecuteChanged();

        if (oldValue != null)
            oldValue.PropertyChanged -= OnScraperPropertyChanged;

        if (newValue != null)
            newValue.PropertyChanged += OnScraperPropertyChanged;

        RefreshHintProperties();
    }

    // Wenn sich Eigenschaften IM Scraper ändern (z.B. der Typ)
    private void OnScraperPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScraperConfig.Type))
        {
            RefreshHintProperties();
        }
    }

    private void RefreshHintProperties()
    {
        // Sagt der View, dass sich diese Computed Properties geändert haben könnten
        OnPropertyChanged(nameof(IsTmdbSelected));
        OnPropertyChanged(nameof(IsIgdbSelected));
        OnPropertyChanged(nameof(IsEmuMoviesSelected));
    }
    
    // Die Liste für die UI (wir wrappen die List<T> in eine ObservableCollection)
    public ObservableCollection<EmulatorConfig> Emulators { get; } = new();

    // Liste der Scraper
    public ObservableCollection<ScraperConfig> Scrapers { get; } = new();
    
    // Commands
    public IRelayCommand AddEmulatorCommand { get; }
    public IRelayCommand RemoveEmulatorCommand { get; }
    public IRelayCommand AddScraperCommand { get; }
    public IRelayCommand RemoveScraperCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand BrowsePathCommand { get; }

    public event Action? RequestClose;

    private void AddEmulator()
    {
        var newEmu = new EmulatorConfig { Name = Strings.NewProfile };
        Emulators.Add(newEmu);
        SelectedEmulator = newEmu; // Direkt auswählen
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
        // ÄNDERUNG: Wir erstellen den Scraper mit dem Default "None".
        // Da "None" nicht in AvailableScraperTypes enthalten ist, 
        // bleibt die ComboBox in der GUI leer (keine Auswahl).
        
        var newScraper = new ScraperConfig 
        { 
            // Name ist automatisch "Neuer Scraper"
            // Type ist automatisch ScraperType.None
        };
        
        Scrapers.Add(newScraper);
        SelectedScraper = newScraper;
    }

    private void RemoveScraper()
    {
        if (SelectedScraper != null)
        {
            Scrapers.Remove(SelectedScraper);
            SelectedScraper = null;
        }
    }
    
    private void Save()
    {
        // Änderungen zurück in das AppSettings-Objekt schreiben
        _appSettings.Emulators.Clear();
        _appSettings.Emulators.AddRange(Emulators);

        _appSettings.Scrapers.Clear();
        _appSettings.Scrapers.AddRange(Scrapers);
        
        RequestClose?.Invoke();
    }

    private async Task BrowsePathAsync()
    {
        if (SelectedEmulator == null) return;

        // Wir holen uns das Fenster über die ApplicationLifetime (etwas hacky, aber einfach im VM)
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.Windows.LastOrDefault(w => w.IsActive) is Window activeWindow)
        {
            var result = await activeWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Emulator auswählen",
                AllowMultiple = false
            });

            if (result != null && result.Count > 0)
                // Pfad in das ausgewählte Profil schreiben
                SelectedEmulator.Path = result[0].Path.LocalPath;
        }
    }
}