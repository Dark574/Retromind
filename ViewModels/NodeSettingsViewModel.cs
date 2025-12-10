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

    // Keine festen Pfade mehr, wir schauen in die Assets Liste
    public ObservableCollection<MediaAsset> Assets => _node.Assets;
    
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
    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }

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
        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
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
            // Achtung: Wir müssen ImportNodeAsset im Service ergänzen oder eine generische Methode nutzen.
            // Da wir ImportAsset für MediaItem haben, nutzen wir eine Überladung für MediaNode.
            // Siehe FileService Ergänzung unten.
            _fileService.ImportNodeAsset(result[0].Path.LocalPath, _node, _nodePath, type);
        }
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