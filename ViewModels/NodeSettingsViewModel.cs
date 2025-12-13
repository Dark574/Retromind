using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for configuring a MediaNode (Folder/Group).
/// Allows changing the name, default emulator, and display options.
/// </summary>
public partial class NodeSettingsViewModel : ViewModelBase
{
    private readonly MediaNode _node;
    private readonly AppSettings _settings;

    // Dependencies
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;
    
    [ObservableProperty] 
    private string _name = string.Empty;

    [ObservableProperty] 
    private string _description = string.Empty;

    // Keine festen Pfade mehr, wir schauen in die Assets Liste
    public ObservableCollection<MediaAsset> Assets => _node.Assets;
    
    // --- VORSCHAU ---
    [ObservableProperty] private Bitmap? _coverPreview;
    [ObservableProperty] private Bitmap? _logoPreview;
    [ObservableProperty] private Bitmap? _wallpaperPreview;
    [ObservableProperty] private string _videoName = "Kein Video";

    
    [ObservableProperty] 
    private bool? _randomizeCovers;
    
    [ObservableProperty] 
    private bool? _randomizeMusic;

    [ObservableProperty] 
    private EmulatorConfig? _selectedEmulator;

    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    // Theme clear command for UI
    public IRelayCommand ClearThemeCommand { get; }
    
    // Import Commands
    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }
    public IAsyncRelayCommand<AssetType> DeleteAssetCommand { get; }

    // Storage Provider Property (wird von View gesetzt oder übergeben)
    public IStorageProvider? StorageProvider { get; set; }
    
    // Event to signal the view to close (true = saved, false = cancelled)
    public event Action<bool>? RequestClose;

    // =========================
    // THEME (BigMode per Node)
    // ThemePath is treated as THEME FOLDER NAME under "<AppBase>/Themes/<ThemeId>/theme.axaml"
    // null/empty means: use convention "<SafeNodeName>/theme.axaml" or fallback to Default
    // =========================

    public ObservableCollection<string> AvailableThemes { get; } = new();

    [ObservableProperty]
    private string? _selectedTheme;
    
    // true, wenn explizit ein Theme gewählt ist (auch "Default")
    public bool IsThemeExplicitlySelected => !string.IsNullOrWhiteSpace(SelectedTheme);

    // Wird vom MVVM Toolkit automatisch aufgerufen, wenn SelectedTheme sich ändert
    partial void OnSelectedThemeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsThemeExplicitlySelected));
    }
    
    public NodeSettingsViewModel(
        MediaNode node, 
        AppSettings settings, 
        FileManagementService fileService, 
        List<string> nodePath)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _nodePath = nodePath ?? new List<string>();

        InitializeData();
        InitializeEmulators();

        // Theme list (folder scan)
        LoadAvailableThemes();
        SelectedTheme = string.IsNullOrWhiteSpace(_node.ThemePath) ? null : _node.ThemePath;
        
        // Initial einmal die Bilder laden
        LoadPreviews();
        
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        
        // Generischer Import-Command (Parameter: "Cover", "Logo" etc.)
        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
        DeleteAssetCommand = new AsyncRelayCommand<AssetType>(DeleteAssetAsync);
        ClearThemeCommand = new RelayCommand(() => SelectedTheme = null);
    }

    private void LoadAvailableThemes()
    {
        AvailableThemes.Clear();

        var themesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Themes");
        if (!Directory.Exists(themesRoot)) return;

        try
        {
            var themes = new List<string>();

            foreach (var dir in Directory.EnumerateDirectories(themesRoot))
            {
                var folderName = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(folderName)) continue;

                var themeFile = Path.Combine(dir, "theme.axaml");
                if (!File.Exists(themeFile)) continue;

                themes.Add(folderName);
            }

            // Sort: "Default" first, then alphabetical
            themes = themes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => !string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase)) // false (=Default) first
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var name in themes)
                AvailableThemes.Add(name);
        }
        catch
        {
            // Best-effort: if directory scan fails, keep list empty
        }
    }
    
    // --- ASSET LOGIC ---

    private void LoadPreviews()
    {
        // Wir suchen im Assets-Array des Nodes nach dem passenden Typ
        CoverPreview = LoadBitmapForType(AssetType.Cover);
        LogoPreview = LoadBitmapForType(AssetType.Logo);
        WallpaperPreview = LoadBitmapForType(AssetType.Wallpaper);

        var vidAsset = _node.Assets.FirstOrDefault(a => a.Type == AssetType.Video);
        VideoName = vidAsset != null ? Path.GetFileName(vidAsset.RelativePath) : "Kein Video";
    }
    
    private Bitmap? LoadBitmapForType(AssetType type)
    {
        var asset = _node.Assets.FirstOrDefault(a => a.Type == type);
        if (asset == null || string.IsNullOrEmpty(asset.RelativePath)) return null;

        var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, asset.RelativePath);
        if (!File.Exists(fullPath)) return null;

        try
        {
            // Performant laden (max 200px Breite reicht für Vorschau)
            using var stream = File.OpenRead(fullPath);
            return Bitmap.DecodeToWidth(stream, 200);
        }
        catch 
        { 
            return null; 
        }
    }
    
    private async Task ImportAssetAsync(AssetType type)
    {
        if (StorageProvider == null) return;

        var fileTypes = type == AssetType.Video 
            ? new[] { new FilePickerFileType("Videos") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi" } } }
            : new[] { FilePickerFileTypes.ImageAll };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {type}",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        if (result != null && result.Count > 0)
        {
            var sourcePath = result[0].Path.LocalPath;

            // 1. Clean Slate: Altes Asset dieses Typs entfernen (Single Slot Prinzip)
            await DeleteAssetAsync(type);

            // 2. Importieren via Service
            // Der Service fügt das neue Asset in _node.Assets ein
            await _fileService.ImportAssetAsync(sourcePath, _node, _nodePath, type);
            
            // 3. Vorschau aktualisieren
            LoadPreviews();
        }
    }

    private async Task DeleteAssetAsync(AssetType type)
    {
        var assetsToRemove = _node.Assets.Where(a => a.Type == type).ToList();
        
        foreach (var asset in assetsToRemove)
        {
            // Aus Liste entfernen
            _node.Assets.Remove(asset);

            // Physisch löschen
            if (!string.IsNullOrEmpty(asset.RelativePath))
            {
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, asset.RelativePath);
                if (File.Exists(fullPath))
                {
                    try 
                    { 
                        // Fire & Forget delete, falls Datei gelockt ist, ist es halt so (wird beim nächsten Cleanup bereinigt)
                        await Task.Run(() => File.Delete(fullPath)); 
                    } 
                    catch { /* Ignore */ }
                }
            }
        }
        
        LoadPreviews();
    }
    
    // Helpers (kopiert aus EditMediaViewModel)
    private string? MakeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, path);
    }

    private void InitializeData()
    {
        Name = _node.Name;
        Description = _node.Description;
        
        RandomizeCovers = _node.RandomizeCovers;
        RandomizeMusic = _node.RandomizeMusic;
    }

    private void InitializeEmulators()
    {
        // Add a "None / Inherited" option
        // Ideally, move "No Default" string to resources
        AvailableEmulators.Add(new EmulatorConfig { Name = "No Default (Inherit)", Id = null! });
        
        foreach (var emu in _settings.Emulators) 
        {
            AvailableEmulators.Add(emu);
        }

        // Restore selection
        if (!string.IsNullOrEmpty(_node.DefaultEmulatorId))
        {
            SelectedEmulator = AvailableEmulators.FirstOrDefault(e => e.Id == _node.DefaultEmulatorId);
        }
        
        // Fallback to "None" if nothing selected or ID not found
        if (SelectedEmulator == null && AvailableEmulators.Count > 0) 
        {
            SelectedEmulator = AvailableEmulators[0];
        }
    }

    private void Save()
    {
        // Validation could go here (e.g. check if Name is empty)
        if (string.IsNullOrWhiteSpace(Name)) return;

        // Apply changes to the node
        _node.Name = Name;
        _node.Description = Description;
        _node.RandomizeCovers = RandomizeCovers;
        _node.RandomizeMusic = RandomizeMusic;
        
        // NEU: Theme speichern (als Ordnername oder null)
        _node.ThemePath = string.IsNullOrWhiteSpace(SelectedTheme) ? null : SelectedTheme;
        
        // Emulator logic: If ID is null (our dummy item), set node property to null
        if (SelectedEmulator != null && SelectedEmulator.Id != null)
        {
            _node.DefaultEmulatorId = SelectedEmulator.Id;
        }
        else
        {
            _node.DefaultEmulatorId = null;
        }

        RequestClose?.Invoke(true);
    }
}