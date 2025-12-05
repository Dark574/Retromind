using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;
using Retromind.Services;
using Retromind.Views;
// Für IBrush, SolidColorBrush

// WICHTIG: Namespace für die Ressourcen

namespace Retromind.ViewModels;

/// <summary>
///     Das Haupt-ViewModel für das gesamte Fenster.
///     Hier läuft die Logik für den Baum, die Navigation und die Menü-Befehle zusammen.
/// </summary>
public class MainWindowViewModel : ViewModelBase
{
    private readonly AudioService _audioService = new();
    private readonly MediaDataService _dataService = new();
    private readonly FileManagementService _fileService = new();
    private readonly ImportService _importService = new();
    private readonly LauncherService _launcherService = new();
    private readonly StoreImportService _storeService = new();

    // --- Services ---
    private readonly SettingsService _settingsService = new();

    // Metadata Service
    private MetadataService _metadataService; // Initialisieren wir in LoadData oder Konstruktor
    
    // --- Zustände ---
    private AppSettings _currentSettings = new();

    // Breite der rechten Spalte (Details) - wird gespeichert
    private GridLength _detailPaneWidth = new(300);

    // aktuell eingestellter Zoom Slider - wird gespeichert
    private double _itemWidth;

    // --- Properties für die UI ---

    // Die Wurzel-Elemente für den TreeView links
    private ObservableCollection<MediaNode> _rootItems = new();

    // Der aktuell im Baum ausgewählte Knoten (Bereich/Gruppe)
    private MediaNode? _selectedNode;

    // Der Inhalt, der in der Mitte angezeigt wird (Aktuell: MediaAreaViewModel)
    private object? _selectedNodeContent;

    // Breite der linken Spalte (Baum) - wird gespeichert
    private GridLength _treePaneWidth = new(250);

    // --- Konstruktor ---
    public MainWindowViewModel()
    {
        // Initialisierung mit leeren Settings, um NullWarning zu vermeiden. 
        // Echte Initialisierung passiert in LoadData().
        _metadataService = new MetadataService(new AppSettings());
        
        // Verknüpfen der Commands mit den Methoden
        AddCategoryCommand = new RelayCommand<MediaNode?>(AddCategoryAsync);
        AddMediaCommand = new RelayCommand<MediaNode?>(AddMediaAsync);
        DeleteCommand = new RelayCommand<MediaNode?>(DeleteNodeAsync);
        SetCoverCommand = new RelayCommand<MediaItem?>(SetCoverAsync);
        SetLogoCommand = new RelayCommand<MediaItem?>(SetLogoAsync);
        SetWallpaperCommand = new RelayCommand<MediaItem?>(SetWallpaperAsync);
        SetMusicCommand = new RelayCommand<MediaItem?>(SetMusicAsync);
        EditMediaCommand = new RelayCommand<MediaItem?>(EditMediaAsync);
        DeleteMediaCommand = new RelayCommand<MediaItem?>(DeleteMediaAsync);
        PlayCommand = new RelayCommand<MediaItem?>(PlayMedia);
        OpenSettingsCommand = new RelayCommand(OpenSettingsAsync);
        EditNodeCommand = new RelayCommand<MediaNode?>(EditNodeAsync);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ImportRomsCommand = new RelayCommand<MediaNode?>(ImportRomsAsync);
        ImportSteamCommand = new RelayCommand<MediaNode?>(ImportSteamAsync);
        ImportGogCommand = new RelayCommand<MediaNode?>(ImportGogAsync);
        ScrapeMediaCommand = new RelayCommand<MediaItem?>(ScrapeMediaAsync);
        ScrapeNodeCommand = new RelayCommand<MediaNode?>(ScrapeNodeAsync);
        OpenSearchCommand = new RelayCommand(OpenIntegratedSearch);

        // Beim Start: Daten laden
        LoadData();
    }

    // Helper, um an das aktuelle Fenster zu kommen (z.B. für Dialoge)
    private Window? CurrentWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    // Für Datei-Zugriffe (Mockbar für Tests)
    public IStorageProvider? StorageProvider { get; set; }

    public ObservableCollection<MediaNode> RootItems
    {
        get => _rootItems;
        set => SetProperty(ref _rootItems, value);
    }

    public MediaNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            // Wenn sich die Auswahl ändert...
            if (SetProperty(ref _selectedNode, value))
            {
                // 1. Aktualisiere den mittleren Bereich
                UpdateContent();

                // 2. Merke dir die Auswahl in den Settings (für den nächsten Neustart)
                if (value != null)
                {
                    _currentSettings.LastSelectedNodeId = value.Id;
                    SaveSettingsOnly();
                }
            }
        }
    }

    public object? SelectedNodeContent
    {
        get => _selectedNodeContent;
        set => SetProperty(ref _selectedNodeContent, value);
    }

    public GridLength TreePaneWidth
    {
        get => _treePaneWidth;
        set
        {
            if (SetProperty(ref _treePaneWidth, value)) _currentSettings.TreeColumnWidth = value.Value;
        }
    }

    public GridLength DetailPaneWidth
    {
        get => _detailPaneWidth;
        set
        {
            if (SetProperty(ref _detailPaneWidth, value))
            {
                _currentSettings.DetailColumnWidth = value.Value;
                SaveSettingsOnly();
            }
        }
    }

    public double ItemWidth
    {
        get => _itemWidth;
        set
        {
            if (SetProperty(ref _itemWidth, value))
            {
                _currentSettings.ItemWidth = value;
                SaveSettingsOnly();
            }
        }
    }

    // --- Commands (Aktionen, die von Buttons/Menüs ausgelöst werden) ---
    public ICommand AddCategoryCommand { get; }
    public ICommand AddMediaCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SetCoverCommand { get; }
    public ICommand SetLogoCommand { get; }
    public ICommand SetWallpaperCommand { get; }
    public ICommand SetMusicCommand { get; }
    public ICommand EditMediaCommand { get; }
    public ICommand DeleteMediaCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand EditNodeCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ImportRomsCommand { get; }
    public ICommand ImportSteamCommand { get; }
    public ICommand ImportGogCommand { get; }
    public ICommand ScrapeMediaCommand { get; }
    public ICommand ScrapeNodeCommand { get; }
    public ICommand OpenSearchCommand { get; }

    // --- Theme Properties ---

    private async void ImportSteamAsync(MediaNode? targetNode)
    {
         // Fallback auf SelectedNode wie beim ROM Import
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportSteamGamesAsync();

        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, "Keine installierten Steam-Spiele gefunden (Default Pfade).");
            return;
        }

        bool doImport = await ShowConfirmDialog(owner, $"{items.Count} Steam-Spiele gefunden. Importieren nach '{targetNode.Name}'?");
        
        if (doImport)
        {
            foreach (var item in items)
            {
                // Duplikat Check
                if (!targetNode.Items.Any(x => x.Title == item.Title))
                {
                    targetNode.Items.Add(item);
                }
            }
            
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    private async void ImportGogAsync(MediaNode? targetNode)
    {
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var items = await _storeService.ImportHeroicGogAsync();

        if (items.Count == 0)
        {
            await ShowConfirmDialog(owner, "Keine Heroic/GOG Installationen gefunden (~/.config/heroic).");
            return;
        }

        bool doImport = await ShowConfirmDialog(owner, $"{items.Count} GOG-Spiele (Heroic) gefunden. Importieren?");
        
        if (doImport)
        {
            foreach (var item in items)
            {
                if (!targetNode.Items.Any(x => x.Title == item.Title))
                {
                    targetNode.Items.Add(item);
                }
            }
            
            SortMediaItems(targetNode.Items);
            await SaveData();
            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }
        
    // Integrierte Suche öffnen
    private void OpenIntegratedSearch()
    {
        // 1. Auswahl im Baum entfernen (visuelles Feedback, dass wir nicht mehr in einem Ordner sind)
        _selectedNode = null;
        OnPropertyChanged(nameof(SelectedNode));

        // 2. Das neue ViewModel erstellen
        var searchVm = new SearchAreaViewModel(RootItems)
        {
            // Aktuellen Zoom übernehmen, damit es smooth wirkt
            ItemWidth = ItemWidth 
        };

        // 3. Events verknüpfen (damit Abspielen funktioniert)
        searchVm.RequestPlay += item => { PlayMedia(item); };
            
        // Listener für Detail-Ansicht (Rechts)
        searchVm.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(SearchAreaViewModel.SelectedMediaItem))
            {
                // Musik etc. updaten wenn nötig (Logik aus UpdateContent)
                var item = searchVm.SelectedMediaItem;
                if (item != null && !string.IsNullOrEmpty(item.MusicPath))
                {
                    var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath);
                    _audioService.PlayMusic(fullPath);
                }
                else
                {
                    _audioService.StopMusic();
                }
            }
        };

        // 4. In die Mitte setzen!
        SelectedNodeContent = searchVm;
    }
    
    public bool IsDarkTheme
    {
        get => _currentSettings.IsDarkTheme;
        set
        {
            if (_currentSettings.IsDarkTheme != value)
            {
                _currentSettings.IsDarkTheme = value;
                OnPropertyChanged();

                // Trigger updates für die Farben
                OnPropertyChanged(nameof(PanelBackground));
                OnPropertyChanged(nameof(TextColor));
                OnPropertyChanged(nameof(WindowBackground));

                // Globales Avalonia Theme umschalten (beeinflusst Titlebar, Menüs, Scrollbars)
                if (Application.Current != null)
                    Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;

                SaveSettingsOnly();
            }
        }
    }

    // Dynamische Farben basierend auf dem Theme
    public IBrush PanelBackground => IsDarkTheme
        ? new SolidColorBrush(Color.Parse("#CC252526")) // Dark: Dunkles Grau, leicht transparent
        : new SolidColorBrush(Color.Parse("#CCF5F5F5")); // Light: Weiß, leicht transparent

    public IBrush TextColor => IsDarkTheme
        ? Brushes.White
        : Brushes.Black;

    // Command zum Umschalten (optional für Button)

    // Voll-deckender Hintergrund für das Fenster (wenn kein Wallpaper da ist)
    public IBrush WindowBackground => IsDarkTheme
        ? new SolidColorBrush(Color.Parse("#252526")) // Dark: Dunkelgrau (solide)
        : Brushes.WhiteSmoke; // Light: Helles Grau/Weiß

    // --- Lade- und Speicherlogik ---

    private async void LoadData()
    {
        // 1. Daten und Einstellungen von Festplatte laden
        RootItems = await _dataService.LoadAsync();
        _currentSettings = await _settingsService.LoadAsync();
        
        // Metadata Service initialisieren, NACHDEM Settings geladen sind
        _metadataService = new MetadataService(_currentSettings);

        // WICHTIG: Wir müssen der UI sagen, dass sich die Theme-Properties geändert haben könnten!
        // Denn wir haben das darunterliegende _currentSettings Objekt ausgetauscht.
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(PanelBackground));
        OnPropertyChanged(nameof(TextColor));
        OnPropertyChanged(nameof(WindowBackground));
        OnPropertyChanged(nameof(TreePaneWidth));
        OnPropertyChanged(nameof(DetailPaneWidth));
        OnPropertyChanged(nameof(ItemWidth));

        // Initiales Theme setzen (damit die App im richtigen Modus startet)
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant =
                _currentSettings.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

        // Rescan nach Assets
        await RescanAllAssetsAsync();

        // Sortieren aller Items
        SortAllNodesRecursive(RootItems);

        // 2. Spaltenbreiten wiederherstellen
        TreePaneWidth = new GridLength(_currentSettings.TreeColumnWidth);
        DetailPaneWidth = new GridLength(_currentSettings.DetailColumnWidth);
        ItemWidth = _currentSettings.ItemWidth;

        // 3. Letzte Auswahl wiederherstellen
        if (!string.IsNullOrEmpty(_currentSettings.LastSelectedNodeId))
        {
            var node = FindNodeById(RootItems, _currentSettings.LastSelectedNodeId);
            if (node != null)
            {
                SelectedNode = node;
                ExpandPathToNode(RootItems, node);
            }
            else
            {
                if (RootItems.Count > 0) SelectedNode = RootItems[0];
            }
        }
        else if (RootItems.Count > 0)
        {
            SelectedNode = RootItems[0];
        }
    }

    // Einzelnes Item scrapen
    private async void ScrapeMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return; // Sicherheitscheck
        
        var vm = new ScrapeDialogViewModel(item, _currentSettings, _metadataService);
        
        vm.OnResultSelected += async (result) => 
        {
            // --- 1. DESCRIPTION ---
            bool updateDesc = true;
            if (!string.IsNullOrWhiteSpace(item.Description) && 
                !string.IsNullOrWhiteSpace(result.Description) &&
                item.Description != result.Description)
            {
                var preview = item.Description.Length > 30 ? item.Description.Substring(0, 30) + "..." : item.Description;
                updateDesc = await ShowConfirmDialog(owner, 
                    $"Konflikt Beschreibung ('{preview}').\nDurch neue ersetzen?");
            }
            else if (string.IsNullOrWhiteSpace(result.Description)) updateDesc = false;

            if (updateDesc) item.Description = result.Description;

            // --- 2. DEVELOPER ---
            bool updateDev = true;
            // StringComparison.OrdinalIgnoreCase sorgt dafür, dass "Nintendo" und "nintendo" nicht als Konflikt gelten
            if (!string.IsNullOrWhiteSpace(item.Developer) && 
                !string.IsNullOrWhiteSpace(result.Developer) &&
                !string.Equals(item.Developer, result.Developer, StringComparison.OrdinalIgnoreCase))
            {
                updateDev = await ShowConfirmDialog(owner, 
                    $"Konflikt Entwickler:\nAlt: {item.Developer}\nNeu: {result.Developer}\n\nÜberschreiben?");
            }
            else if (string.IsNullOrWhiteSpace(result.Developer)) updateDev = false;

            if (updateDev) item.Developer = result.Developer;

            // --- 3. RELEASE DATE ---
            bool updateDate = true;
            if (item.ReleaseDate.HasValue && result.ReleaseDate.HasValue &&
                item.ReleaseDate.Value.Date != result.ReleaseDate.Value.Date)
            {
                // :d formatiert das Datum kurz (z.B. 01.01.2000)
                updateDate = await ShowConfirmDialog(owner, 
                    $"Konflikt Datum:\nAlt: {item.ReleaseDate.Value:d}\nNeu: {result.ReleaseDate.Value:d}\n\nÜberschreiben?");
            }
            else if (!result.ReleaseDate.HasValue) updateDate = false;

            if (updateDate) item.ReleaseDate = result.ReleaseDate;

            // --- 4. GENRE & RATING (weniger kritisch) ---
            // Rating nehmen wir mit, wenn es im Ergebnis vorhanden ist
            if (result.Rating.HasValue) item.Rating = result.Rating.Value;
            
            // Genre füllen wir nur auf, wenn es leer war (optional, kannst du auch mit Dialog machen)
            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre))
                item.Genre = result.Genre;
            
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
                        
            // DOWNLOADS (Manuell): Hier wollen wir das neue Bild sehen -> setAsActive = true
            if (!string.IsNullOrEmpty(result.CoverUrl))
                await DownloadAndSetAsset(result.CoverUrl, item, nodePath, MediaFileType.Cover, true);
                
            if (!string.IsNullOrEmpty(result.WallpaperUrl))
                await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, MediaFileType.Wallpaper, true);

            await SaveData();
                
            if (owner.OwnedWindows.FirstOrDefault(w => w.DataContext == vm) is Window dlg)
                dlg.Close();
        };

        var dialog = new ScrapeDialogView { DataContext = vm };
        await dialog.ShowDialog(owner);
    }

    // Bereich scrapen
    private async void ScrapeNodeAsync(MediaNode? node)
    {
        if (SelectedNode != null && node != null && node.Id == SelectedNode.Id &&
            node != SelectedNode) node = SelectedNode;
        if (node == null) node = SelectedNode;
        if (node == null || CurrentWindow is not { } owner) return;

        var vm = new BulkScrapeViewModel(node, _currentSettings, _metadataService);
        
        vm.OnItemScraped = async (item, result) =>
        {
            var parent = FindParentNode(RootItems, item);
            if (parent == null) return;
            var nodePath = PathHelper.GetNodePath(parent, RootItems);
            
            // LOGIK-ÄNDERUNG: Bulk-Mode = "Nur Lücken füllen" (Safe Mode)
            // Wir überschreiben niemals existierende Daten, um den Prozess nicht zu blockieren
            // und keine manuellen Einträge zu zerstören.
                
            if (string.IsNullOrWhiteSpace(item.Description) && !string.IsNullOrWhiteSpace(result.Description)) 
                item.Description = result.Description;
                
            if (string.IsNullOrWhiteSpace(item.Developer) && !string.IsNullOrWhiteSpace(result.Developer)) 
                item.Developer = result.Developer;
                
            if (string.IsNullOrWhiteSpace(item.Genre) && !string.IsNullOrWhiteSpace(result.Genre))
                item.Genre = result.Genre;
                
            if (!item.ReleaseDate.HasValue && result.ReleaseDate.HasValue) 
                item.ReleaseDate = result.ReleaseDate;

            if (item.Rating == 0 && result.Rating.HasValue) 
                item.Rating = result.Rating.Value;
            
            // DOWNLOADS (Bulk): 
            // Wir laden IMMER runter (für das Archiv).
            // Wir setzen es aber nur als AKTIV, wenn vorher keins da war.
            
            if (!string.IsNullOrEmpty(result.CoverUrl))
            {
                bool shouldActivate = string.IsNullOrEmpty(item.CoverPath);
                await DownloadAndSetAsset(result.CoverUrl, item, nodePath, MediaFileType.Cover, shouldActivate);
            }
                
            if (!string.IsNullOrEmpty(result.WallpaperUrl))
            {
                bool shouldActivate = string.IsNullOrEmpty(item.WallpaperPath);
                await DownloadAndSetAsset(result.WallpaperUrl, item, nodePath, MediaFileType.Wallpaper, shouldActivate);
            }
        };
        
        var dialog = new BulkScrapeView { DataContext = vm };
        await dialog.ShowDialog(owner);
        
        await SaveData();
        if (IsNodeInCurrentView(node)) UpdateContent();
    }

    // Helper zum Downloaden
    private async Task DownloadAndSetAsset(string url, MediaItem item, List<string> nodePath, MediaFileType type, bool setAsActive)
    {
        try
        {
            // Temporär speichern
            var tempFile = Path.GetTempFileName();
            var ext = Path.GetExtension(url).Split('?')[0];
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            var tempPathWithExt = Path.ChangeExtension(tempFile, ext);
            File.Move(tempFile, tempPathWithExt);

            bool success = false;

            // 1. Versuchen aus Cache zu speichern (vermeidet erneuten Download/403)
            if (await AsyncImageHelper.SaveCachedImageAsync(url, tempPathWithExt))
            {
                Console.WriteLine($"Bild aus Cache gespeichert: {url}");
                success = true;
            }
            else
            {
                // 2. Fallback: Download (mit Headern)
                try
                {
                    using var client = new HttpClient();
                    // Header
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                    
                    var data = await client.GetByteArrayAsync(url);
                    await File.WriteAllBytesAsync(tempPathWithExt, data);
                    success = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Download failed: {ex.Message}");
                }
            }

            if (success)
            {
                // Importieren
                var relativePath = _fileService.ImportAsset(tempPathWithExt, item, nodePath, type);

                if (setAsActive && !string.IsNullOrEmpty(relativePath))
                {
                    if (type == MediaFileType.Cover) item.CoverPath = relativePath;
                    if (type == MediaFileType.Wallpaper) item.WallpaperPath = relativePath;
                    if (type == MediaFileType.Logo) item.LogoPath = relativePath;
                }
            }

            // Cleanup
            if (File.Exists(tempPathWithExt)) File.Delete(tempPathWithExt);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"General Error: {ex.Message}");
        }
    }
        
    // Rekursives Sortieren (Baum + Items)
    private void SortAllNodesRecursive(IEnumerable<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            SortMediaItems(node.Items);
            SortAllNodesRecursive(node.Children);
        }
    }

    private void SortMediaItems(ObservableCollection<MediaItem> items)
    {
        var sorted = items.OrderBy(i => i.Title).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            var oldIndex = items.IndexOf(item);

            if (oldIndex != i) items.Move(oldIndex, i);
        }
    }

    public void Cleanup()
    {
        _audioService.StopMusic();
    }

    public async Task SaveData()
    {
        await _dataService.SaveAsync(RootItems);
        await _settingsService.SaveAsync(_currentSettings);
    }

    private async void SaveSettingsOnly()
    {
        await _settingsService.SaveAsync(_currentSettings);
    }

    // --- Baum-Operationen (Hinzufügen/Löschen) ---

    // NEU: Ersetzt AddAreaAsync und AddGroupAsync
    private async void AddCategoryAsync(MediaNode? parentNode)
    {
        if (CurrentWindow is not { } owner) return;

        // Texte aus den Ressourcen laden
        var promptMsg = Strings.MsgEnterName;

        // Dialog nach Namen fragen
        var name = await PromptForName(owner, promptMsg);

        if (!string.IsNullOrWhiteSpace(name))
        {
            // Wenn kein Parent da ist -> Root (Plattform)
            if (parentNode == null)
            {
                // Wir nutzen intern NodeType.Area für das Root-Icon, für den User ist es aber eine "Kategorie"
                RootItems.Add(new MediaNode(name, NodeType.Area));
            }
            // Wenn Parent da ist -> Child (Unterordner)
            else
            {
                parentNode.Children.Add(new MediaNode(name, NodeType.Group));
                parentNode.IsExpanded = true; // Aufklappen
            }

            await SaveData();
        }
    }

    private async void DeleteNodeAsync(MediaNode? nodeToDelete)
    {
        if (nodeToDelete == null || CurrentWindow is not { } owner) return;

        // Sicherheitsabfrage
        var confirmed = await ShowConfirmDialog(owner,
            $"Möchtest du '{nodeToDelete.Name}' und alle Unterelemente wirklich löschen?");

        if (!confirmed) return;

        // Löschen aus Root oder Unterelementen
        if (RootItems.Contains(nodeToDelete))
            RootItems.Remove(nodeToDelete);
        else
            RemoveNodeRecursive(RootItems, nodeToDelete);

        await SaveData();
    }

    // Neue Methode für den Rescan
    private Task RescanAllAssetsAsync()
    {
        return Task.Run(() =>
        {
            foreach (var rootNode in RootItems) RescanNodeRecursive(rootNode);
        });
    }

    private void RescanNodeRecursive(MediaNode node)
    {
        var nodePath = PathHelper.GetNodePath(node, RootItems);

        foreach (var item in node.Items)
        {
            if (string.IsNullOrEmpty(item.CoverPath))
            {
                var foundCover = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Cover);
                if (foundCover != null) item.CoverPath = foundCover;
            }

            if (string.IsNullOrEmpty(item.LogoPath))
            {
                var foundLogo = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Logo);
                if (foundLogo != null) item.LogoPath = foundLogo;
            }

            if (string.IsNullOrEmpty(item.WallpaperPath))
            {
                var foundWP = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Wallpaper);
                if (foundWP != null) item.WallpaperPath = foundWP;
            }

            if (string.IsNullOrEmpty(item.MusicPath))
            {
                var foundMusic = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Music);
                if (foundMusic != null) item.MusicPath = foundMusic;
            }
        }

        foreach (var child in node.Children) RescanNodeRecursive(child);
    }

    // --- Medien-Operationen (Spiele/Dateien hinzufügen) ---

    private async void AddMediaAsync(MediaNode? node)
    {
        // Wenn node der Fake-Node aus der Anzeige ist, müssen wir den echten Node nehmen.
        // Wir erkennen das daran, dass node nicht in RootItems enthalten ist (Referenzgleichheit),
        // aber vielleicht die gleiche ID hat wie SelectedNode.

        var targetNode = node;

        // Fall 1: Aufruf kam ohne Parameter oder Parameter ist null -> Nimm SelectedNode
        if (targetNode == null)
            targetNode = SelectedNode;
        // Fall 2: Aufruf kam mit Parameter (z.B. aus Kontextmenü), aber es ist der Fake-Node
        else if (SelectedNode != null && targetNode.Id == SelectedNode.Id && targetNode != SelectedNode)
            // Es ist der Fake-Node (gleiche ID, andere Instanz)
            targetNode = SelectedNode;

        if (targetNode == null || CurrentWindow is not { } owner) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Medium auswählen (Executable, ROM, Video...)",
            AllowMultiple = true
        });

        if (result != null && result.Count > 0)
        {
            foreach (var file in result)
            {
                var sourcePath = file.Path.LocalPath;
                var filename = file.Name;
                var rawTitle = Path.GetFileNameWithoutExtension(filename);

                var title = await PromptForName(owner, $"Titel für '{filename}' festlegen:") ?? rawTitle;

                if (string.IsNullOrWhiteSpace(title)) title = rawTitle;

                var newItem = new MediaItem
                {
                    Title = title,
                    FilePath = sourcePath,
                    MediaType = MediaType.Native
                };

                targetNode.Items.Add(newItem); // Nutze targetNode statt node

                var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                var existingCover = _fileService.FindExistingAsset(newItem, nodePath, MediaFileType.Cover);
                if (existingCover != null) newItem.CoverPath = existingCover;

                var existingLogo = _fileService.FindExistingAsset(newItem, nodePath, MediaFileType.Logo);
                if (existingLogo != null) newItem.LogoPath = existingLogo;
            }

            SortMediaItems(targetNode.Items);
            await SaveData();

            if (IsNodeInCurrentView(targetNode)) UpdateContent();
        }
    }

    // Hilfsmethode: Prüft, ob der modifizierte Node Teil des aktuell angezeigten Baums ist
    private bool IsNodeInCurrentView(MediaNode modifiedNode)
    {
        if (SelectedNode == null) return false;

        // 1. Ist es der ausgewählte Node selbst?
        // Wir prüfen auf ID, falls modifiedNode doch mal eine Kopie/Fake ist, 
        // aber eigentlich auf Referenzgleichheit, da wir ja jetzt targetNode nutzen.
        if (modifiedNode == SelectedNode || modifiedNode.Id == SelectedNode.Id) return true;

        // 2. Ist der modifizierte Node ein Kind des ausgewählten Nodes?
        // (Da wir rekursiv anzeigen, muss auch dann aktualisiert werden)
        return IsChildOf(SelectedNode, modifiedNode);
    }

    private bool IsChildOf(MediaNode parent, MediaNode potentialChild)
    {
        foreach (var child in parent.Children)
        {
            // Referenzgleichheit oder ID-Check
            if (child == potentialChild || child.Id == potentialChild.Id) return true;

            if (IsChildOf(child, potentialChild)) return true;
        }

        return false;
    }

    private async void SetCoverAsync(MediaItem? item)
    {
        await SetAssetAsync(item, "Cover auswählen", MediaFileType.Cover, (i, path) => i.CoverPath = path);
    }

    private async void SetLogoAsync(MediaItem? item)
    {
        await SetAssetAsync(item, "Logo auswählen", MediaFileType.Logo, (i, path) => i.LogoPath = path);
    }

    private async void SetWallpaperAsync(MediaItem? item)
    {
        await SetAssetAsync(item, "Wallpaper auswählen", MediaFileType.Wallpaper, (i, path) => i.WallpaperPath = path);
    }

    // Musik ist speziell wegen AudioService, lassen wir ggf. separat oder passen es an.
    // Hier die generische Methode:
    private async Task SetAssetAsync(MediaItem? item, string title, MediaFileType type,
        Action<MediaItem, string> updateAction)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        if (result != null && result.Count == 1)
        {
            var sourceFile = result[0].Path.LocalPath;
            // Wichtig: Hier muss die Logik greifen, um den ECHTEN Node-Pfad zu finden, falls item in einem Sub-Node ist
            // Aktuell nimmst du SelectedNode, was okay ist, wenn wir nur im Detail-View des SelectedNodes sind.
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);

            var relativePath = _fileService.ImportAsset(sourceFile, item, nodePath, type);

            if (!string.IsNullOrEmpty(relativePath))
            {
                // Null-Trick für UI-Update
                updateAction(item, null!);
                updateAction(item, relativePath);
                await SaveData();
            }
        }
    }

    private async void SetMusicAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        if (SelectedNode == null) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;
        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Musik auswählen",
            AllowMultiple = false,
            FileTypeFilter = new[]
                { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.ogg", "*.flac" } } }
        });

        if (result != null && result.Count == 1)
        {
            // WICHTIG: Musik stoppen, bevor wir die Datei überschreiben!
            // Sonst könnte die Datei gesperrt sein und der Import fehlschlagen.
            _audioService.StopMusic();

            var sourceFile = result[0].Path.LocalPath;
            var nodePath = PathHelper.GetNodePath(SelectedNode, RootItems);
            var relativePath = _fileService.ImportAsset(sourceFile, item, nodePath, MediaFileType.Music);

            if (!string.IsNullOrEmpty(relativePath))
            {
                // Null-Trick für Konsistenz (falls UI-Elemente gebunden sind)
                item.MusicPath = null;
                item.MusicPath = relativePath;
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
                _audioService.PlayMusic(fullPath);
                await SaveData();
            }
        }
    }

    // Neue Hilfsmethode: Findet den vererbten Emulator für ein Item
    private EmulatorConfig? FindInheritedEmulator(MediaItem item)
    {
        // 1. Finde den Node, der das Item enthält
        // (Da wir keine Parent-Pointer haben, suchen wir von oben)
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode == null) return null;

        // 2. Suche die Kette nach oben
        var nodeChain = GetNodeChain(parentNode, RootItems);
        nodeChain.Reverse();

        foreach (var node in nodeChain)
            if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                return _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);

        return null;
    }

    // Helper: Findet den Parent-Node eines Items
    private MediaNode? FindParentNode(IEnumerable<MediaNode> nodes, MediaItem item)
    {
        foreach (var node in nodes)
        {
            if (node.Items.Contains(item)) return node;

            var found = FindParentNode(node.Children, item);
            if (found != null) return found;
        }

        return null;
    }

    private async void EditMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;

        // Emulator suchen
        var inherited = FindInheritedEmulator(item);

        // Konstruktor mit Inherited-Parameter aufrufen
        var editVm = new EditMediaViewModel(item, _currentSettings, inherited)
        {
            // StorageProvider übergeben für FilePicker im Dialog
            StorageProvider = StorageProvider ?? owner.StorageProvider
        };

        var dialog = new EditMediaView
        {
            DataContext = editVm
        };

        editVm.RequestClose += saved => { dialog.Close(saved); };

        var result = await dialog.ShowDialog<bool>(owner);

        if (result) await SaveData();
    }

    private async void DeleteMediaAsync(MediaItem? item)
    {
        if (item == null || CurrentWindow is not { } owner) return;
        // Hinweis: SelectedNode ist hier ggf. die Kategorie oben drüber

        var confirmed = await ShowConfirmDialog(owner,
            $"Möchtest du '{item.Title}' wirklich löschen? (Dateien bleiben erhalten)");

        if (!confirmed) return;

        if (item == (SelectedNodeContent as MediaAreaViewModel)?.SelectedMediaItem) _audioService.StopMusic();

        // Echten Parent suchen und löschen
        var parentNode = FindParentNode(RootItems, item);
        if (parentNode != null)
        {
            parentNode.Items.Remove(item);
            await SaveData();

            // Ansicht aktualisieren (neu einsammeln)
            UpdateContent();
        }
    }

    private async void PlayMedia(MediaItem? item)
    {
        if (item == null || SelectedNode == null) return;

        _audioService.StopMusic();

        EmulatorConfig? emulator = null;

        // --- SCHRITT 1: Hat das Item selbst einen Emulator? ---
        if (!string.IsNullOrEmpty(item.EmulatorId))
            emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);
        // -----------------------------------------------------

        // Wir brauchen die Kette, um den Emulator (FALLS NOCH NULL) UND den Pfad zu finden
        var trueParent = FindParentNode(RootItems, item);
        if (trueParent == null) trueParent = SelectedNode; // Fallback

        var nodePath = PathHelper.GetNodePath(trueParent, RootItems); // Pfad holen!

        // --- SCHRITT 2: Nur suchen, wenn wir noch keinen haben ---
        if (emulator == null)
        {
            var nodeChain = GetNodeChain(trueParent, RootItems);
            nodeChain.Reverse();
            foreach (var node in nodeChain)
                if (!string.IsNullOrEmpty(node.DefaultEmulatorId))
                {
                    emulator = _currentSettings.Emulators.FirstOrDefault(e => e.Id == node.DefaultEmulatorId);
                    if (emulator != null) break;
                }
        }

        await _launcherService.LaunchAsync(item, emulator, nodePath);

        // Musik wieder starten (falls wir noch auf dem gleichen Item sind)
        if (SelectedNodeContent is MediaAreaViewModel vm &&
            vm.SelectedMediaItem == item &&
            !string.IsNullOrEmpty(item.MusicPath))
        {
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath);
            _audioService.PlayMusic(fullPath);
        }

        await SaveData();
    }

    private async void OpenSettingsAsync()
    {
        if (CurrentWindow is not { } owner) return;

        // Settings VM erstellen (wir übergeben die ECHTE Referenz von _currentSettings)
        // Damit arbeiten wir direkt auf den globalen Daten
        var settingsVm = new SettingsViewModel(_currentSettings);

        var dialog = new SettingsView
        {
            DataContext = settingsVm
        };

        // Fenster schließen Logik
        settingsVm.RequestClose += () => { dialog.Close(); };

        // Dialog anzeigen (wartet bis geschlossen)
        await dialog.ShowDialog(owner);

        // Nach dem Schließen speichern wir sicherheitshalber alles
        SaveSettingsOnly();
    }

    // Methode zum Bearbeiten des Ordners
    private async void EditNodeAsync(MediaNode? node)
    {
        if (node == null || CurrentWindow is not { } owner) return;

        var vm = new NodeSettingsViewModel(node, _currentSettings);
        var dialog = new NodeSettingsView
        {
            DataContext = vm
        };

        vm.RequestClose += saved => { dialog.Close(); };

        await dialog.ShowDialog(owner);

        // Speichern, da sich Name oder Emulator geändert haben könnte
        await SaveData();
    }

    private async void ImportRomsAsync(MediaNode? targetNode)
    {
        // Wenn der Parameter der Fake-Node ist, tauschen wir ihn gegen das Original
        if (SelectedNode != null && targetNode != null && targetNode.Id == SelectedNode.Id &&
            targetNode != SelectedNode) targetNode = SelectedNode;

        // Fallback auf SelectedNode, wenn null (z.B. über Button ausgelöst)
        if (targetNode == null) targetNode = SelectedNode;
        if (targetNode == null || CurrentWindow is not { } owner) return;

        var storageProvider = StorageProvider ?? owner.StorageProvider;

        // 1. Ordner auswählen
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "ROM-Ordner auswählen (Rekursiv)",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var sourcePath = folders[0].Path.LocalPath;

        // 2. Extensions abfragen
        var defaultExt = "iso,bin,cue,rom,smc,sfc,nes,gb,gba,nds,md,n64,z64,v64";
        var extensionsStr = await PromptForName(owner, "Dateiendungen (kommagetrennt):") ?? defaultExt;

        if (string.IsNullOrWhiteSpace(extensionsStr)) return;

        var extensions = extensionsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

        // 3. Importieren (Service Aufruf)
        var importedItems = await _importService.ImportFromFolderAsync(sourcePath, extensions);

        if (importedItems.Count > 0)
        {
            // 4. Items zum Node hinzufügen
            foreach (var item in importedItems)
            {
                // Duplikat-Check (einfach): Pfad schon vorhanden?
                var exists = targetNode.Items.Any(i => i.FilePath == item.FilePath);
                if (!exists)
                {
                    // Assets suchen (Cover etc.) - direkt beim Import!
                    var nodePath = PathHelper.GetNodePath(targetNode, RootItems);
                    var existingCover = _fileService.FindExistingAsset(item, nodePath, MediaFileType.Cover);
                    if (existingCover != null) item.CoverPath = existingCover;
                    // ... andere Assets auch ...

                    targetNode.Items.Add(item);
                }
            }

            SortMediaItems(targetNode.Items);
            await SaveData();

            // Refresh View
            if (IsNodeInCurrentView(targetNode)) UpdateContent();

            // Optional: Erfolgsmeldung?
            // await ShowConfirmDialog(owner, $"{importedItems.Count} Spiele importiert!");
        }
    }

    private List<MediaNode> GetNodeChain(MediaNode target, ObservableCollection<MediaNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node == target) return new List<MediaNode> { node };

            var childChain = GetNodeChain(target, node.Children);
            if (childChain.Count > 0)
            {
                childChain.Insert(0, node);
                return childChain;
            }
        }

        return new List<MediaNode>();
    }

    // --- Helper ---
    private bool RemoveNodeRecursive(ObservableCollection<MediaNode> nodes, MediaNode nodeToDelete)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Remove(nodeToDelete)) return true;

            if (RemoveNodeRecursive(node.Children, nodeToDelete)) return true;
        }

        return false;
    }

    private MediaNode? FindNodeById(IEnumerable<MediaNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;

            var foundInChild = FindNodeById(node.Children, id);
            if (foundInChild != null) return foundInChild;
        }

        return null;
    }

    private bool ExpandPathToNode(IEnumerable<MediaNode> nodes, MediaNode target)
    {
        foreach (var node in nodes)
        {
            if (node == target) return true;

            if (ExpandPathToNode(node.Children, target))
            {
                node.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private async Task<bool> ShowConfirmDialog(Window owner, string message)
    {
        var dialog = new ConfirmView { DataContext = message };
        return await dialog.ShowDialog<bool>(owner);
    }

    private async Task<string?> PromptForName(Window owner, string message)
    {
        var dialog = new NamePromptView
        {
            DataContext = new NamePromptViewModel(message, message)
        };

        var result = await dialog.ShowDialog<bool>(owner);
        if (result && dialog.DataContext is NamePromptViewModel vm) return vm.InputText;
        return null;
    }

    // Prüft rekursiv nach oben, ob RandomizeCovers aktiv ist
    private bool IsRandomizeActive(MediaNode targetNode)
    {
        // 1. Kette von Root bis zum Target holen
        var chain = GetNodeChain(targetNode, RootItems);
            
        // 2. Rückwärts durchlaufen (vom Kind zum Elternteil zum Root)
        chain.Reverse();

        foreach (var node in chain)
        {
            // Wenn ein Node explizit true oder false sagt, gilt das!
            // Wenn er null ist (erben), geht die Schleife weiter zum Elternteil.
            if (node.RandomizeCovers.HasValue)
            {
                return node.RandomizeCovers.Value;
            }
        }

        // 3. Wenn wir ganz oben angekommen sind und alle auf "Erben" stehen -> Standard ist Aus
        return false; 
    }
    
    // Prüft rekursiv nach oben, ob RandomizeMusic aktiv ist
    private bool IsRandomizeMusicActive(MediaNode targetNode)
    {
        // 1. Kette von Root bis zum Target holen
        var chain = GetNodeChain(targetNode, RootItems);
            
        // 2. Rückwärts durchlaufen (vom Kind zum Elternteil zum Root)
        chain.Reverse();

        foreach (var node in chain)
        {
            // Wenn ein Node explizit true oder false sagt, gilt das!
            // Wenn er null ist (erben), geht die Schleife weiter zum Elternteil.
            if (node.RandomizeMusic.HasValue)
            {
                return node.RandomizeMusic.Value;
            }
        }

        // 3. Wenn wir ganz oben angekommen sind und alle auf "Erben" stehen -> Standard ist Aus
        return false; 
    }
    
    private void UpdateContent()
    {
        _audioService.StopMusic();

        if (SelectedNode is null || SelectedNode.Type == NodeType.Area)
        {
            SelectedNodeContent = null;
            return;
        }

        // REKURSIV: Alle Items aus diesem Node und allen Unter-Nodes sammeln
        var allItems = new ObservableCollection<MediaItem>();
        CollectItemsRecursive(SelectedNode, allItems);

        // Wir erstellen einen temporären Anzeige-Node.
        // Dieser dient nur als Container für die View, damit wir nicht die echte Struktur verändern.
        var displayNode = new MediaNode(SelectedNode.Name, SelectedNode.Type)
        {
            Id = SelectedNode.Id, // ID behalten für Wiedererkennung
            Items = allItems
        };

        // Helper für Musik-Status (analog zu Covers)
        bool randomizeMusic = IsRandomizeMusicActive(SelectedNode);
        
        // ZUFALLS-LOGIK START ---
        if (IsRandomizeActive(SelectedNode) || randomizeMusic)
        {
            // Wir starten einen Hintergrund-Task, um die UI nicht zu blockieren
            Task.Run(() => 
            {
                foreach (var item in allItems)
                {
                    // COVERS
                    if (IsRandomizeActive(SelectedNode)) 
                    {
                        var imgs = MediaSearchHelper.FindPotentialImages(item);
                        var rndImg = RandomHelper.PickRandom(imgs);
                        if (rndImg != null && rndImg != item.CoverPath)
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => item.CoverPath = rndImg);
                    }

                    // MUSIK
                    if (randomizeMusic)
                    {
                        var audios = MediaSearchHelper.FindPotentialAudio(item);
                        var rndAudio = RandomHelper.PickRandom(audios);
                    
                        // Nur setzen wenn unterschiedlich
                        if (rndAudio != null && rndAudio != item.MusicPath)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => item.MusicPath = rndAudio);
                        }
                    }
                }
            });
        }
        
        // ViewModel mit dem Display-Node initialisieren
        var mediaVm = new MediaAreaViewModel(displayNode, ItemWidth);

        // Auf das Start-Event hören
        mediaVm.RequestPlay += item => { PlayMedia(item); };

        mediaVm.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(MediaAreaViewModel.ItemWidth))
            {
                ItemWidth = mediaVm.ItemWidth;
                SaveSettingsOnly();
            }

            if (args.PropertyName == nameof(MediaAreaViewModel.SelectedMediaItem))
            {
                var item = mediaVm.SelectedMediaItem;

                if (item != null)
                {
                    _currentSettings.LastSelectedMediaId = item.Id;
                    SaveSettingsOnly();
                }
                else
                {
                    _currentSettings.LastSelectedMediaId = null;
                    SaveSettingsOnly();
                }

                if (item != null && !string.IsNullOrEmpty(item.MusicPath))
                {
                    var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, item.MusicPath);
                    _audioService.PlayMusic(fullPath);
                }
                else
                {
                    _audioService.StopMusic();
                }
            }
        };

        if (!string.IsNullOrEmpty(_currentSettings.LastSelectedMediaId))
        {
            // Suchen in der neuen flachen Liste
            var itemToSelect = allItems.FirstOrDefault(i => i.Id == _currentSettings.LastSelectedMediaId);
            if (itemToSelect != null) mediaVm.SelectedMediaItem = itemToSelect;
        }

        SelectedNodeContent = mediaVm;
    }

    // Hilfsmethode zum rekursiven Einsammeln
    private void CollectItemsRecursive(MediaNode node, ObservableCollection<MediaItem> targetList)
    {
        // 1. Items des aktuellen Nodes hinzufügen
        foreach (var item in node.Items) targetList.Add(item);

        // 2. Rekursiv in Kinder absteigen
        foreach (var child in node.Children) CollectItemsRecursive(child, targetList);
    }
}