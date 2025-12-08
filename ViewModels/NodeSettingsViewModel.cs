using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    // Assets are usually handled by separate managers/dialogs, but let's expose them here or via commands.
    // For now, simple string properties to bind to textboxes or file pickers.
    // In a full implementation, you might want "Browse..." commands.
    
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string? _wallpaperPath;
    [ObservableProperty] private string? _logoPath;
    [ObservableProperty] private string? _videoPath;
    
    [ObservableProperty] 
    private bool? _randomizeCovers;
    
    [ObservableProperty] 
    private bool? _randomizeMusic;

    [ObservableProperty] 
    private EmulatorConfig? _selectedEmulator;

    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    
    // Import Commands
    public IAsyncRelayCommand<string> ImportAssetCommand { get; }

    // Storage Provider Property (wird von View gesetzt oder übergeben)
    public IStorageProvider? StorageProvider { get; set; }
    
    // Event to signal the view to close (true = saved, false = cancelled)
    public event Action<bool>? RequestClose;

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

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => RequestClose?.Invoke(false));
        
        // Generischer Import-Command (Parameter: "Cover", "Logo" etc.)
        ImportAssetCommand = new AsyncRelayCommand<string>(ImportAssetAsync);
    }

    private async Task ImportAssetAsync(string? typeStr)
    {
        if (StorageProvider == null || string.IsNullOrEmpty(typeStr)) return;

        if (!Enum.TryParse<MediaFileType>(typeStr, out var type)) return;

        var fileTypes = type == MediaFileType.Video 
            ? new[] { new FilePickerFileType("Videos") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi" } } }
            : new[] { FilePickerFileTypes.ImageAll };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {typeStr}",
            AllowMultiple = false,
            FileTypeFilter = fileTypes
        });

        if (result != null && result.Count > 0)
        {
            // Nutzt die NEUE Methode im Service
            var relPath = _fileService.ImportNodeAsset(result[0].Path.LocalPath, _node, _nodePath, type);
            
            if (!string.IsNullOrEmpty(relPath))
            {
                var fullPath = ToAbsolutePath(relPath);
                
                // Property aktualisieren
                switch (type)
                {
                    case MediaFileType.Cover: CoverPath = fullPath; break;
                    case MediaFileType.Wallpaper: WallpaperPath = fullPath; break;
                    case MediaFileType.Logo: LogoPath = fullPath; break;
                    case MediaFileType.Video: VideoPath = fullPath; break;
                }
            }
        }
    }
    
    // Helpers (kopiert aus EditMediaViewModel)
    private string? MakeRelative(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, path);
    }

    private string? ToAbsolutePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
    }
    
    private void InitializeData()
    {
        Name = _node.Name;
        Description = _node.Description;
        CoverPath = ToAbsolutePath(_node.CoverPath);
        WallpaperPath = ToAbsolutePath(_node.WallpaperPath);
        LogoPath = ToAbsolutePath(_node.LogoPath);
        VideoPath = ToAbsolutePath(_node.VideoPath);
        
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
        _node.CoverPath = MakeRelative(CoverPath);
        _node.WallpaperPath = MakeRelative(WallpaperPath);
        _node.LogoPath = MakeRelative(LogoPath);
        _node.VideoPath = MakeRelative(VideoPath);
        
        _node.RandomizeCovers = RandomizeCovers;
        _node.RandomizeMusic = RandomizeMusic;
        
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