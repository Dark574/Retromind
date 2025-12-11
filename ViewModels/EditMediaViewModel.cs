using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Retromind.Models;
using Retromind.Services;

namespace Retromind.ViewModels;

public partial class EditMediaViewModel : ViewModelBase
{
    private readonly EmulatorConfig? _inheritedEmulator;
    private readonly MediaItem _originalItem;
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;

    // --- Metadata Properties (Temporary Buffer) ---
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _genre;
    [ObservableProperty] private DateTimeOffset? _releaseDate; 
    [ObservableProperty] private PlayStatus _status;

    // --- Launch Config Properties ---
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsEmulatorMode))]
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))]
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private MediaType _mediaType;

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsManualEmulator))] 
    [NotifyPropertyChangedFor(nameof(PreviewText))]
    private EmulatorConfig? _selectedEmulatorProfile;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherPath;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PreviewText))]
    private string? _launcherArgs;
    
    [ObservableProperty] private string _overrideWatchProcess = string.Empty;

    // --- Asset Management ---
    
    // Wir binden direkt an die Collection des Items. 
    // Da FileService die Liste live aktualisiert, sieht die UI sofort Änderungen.
    public ObservableCollection<MediaAsset> Assets => _originalItem.Assets;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(DeleteAssetCommand))]
    private MediaAsset? _selectedAsset;

    // --- Commands ---
    public IAsyncRelayCommand<AssetType> ImportAssetCommand { get; }
    public IRelayCommand DeleteAssetCommand { get; }
    
    public IAsyncRelayCommand BrowseLauncherCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public IStorageProvider? StorageProvider { get; set; }
    
    // Event to signal the view to close (true = saved, false = cancelled)
    public event Action<bool>? RequestClose;

    // --- UI Lists ---
    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();
    public List<MediaType> MediaTypeOptions { get; } = new() { MediaType.Native, MediaType.Emulator };
    public List<PlayStatus> StatusOptions { get; } = Enum.GetValues<PlayStatus>().ToList();

    public EditMediaViewModel(
        MediaItem item, 
        AppSettings settings, 
        FileManagementService fileService, 
        List<string> nodePath, 
        EmulatorConfig? inheritedEmulator = null)
    {
        _originalItem = item;
        _fileService = fileService;
        _nodePath = nodePath; 
        _inheritedEmulator = inheritedEmulator;

        // Commands initialisieren
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);
        
        // Generische Asset-Commands
        ImportAssetCommand = new AsyncRelayCommand<AssetType>(ImportAssetAsync);
        DeleteAssetCommand = new RelayCommand(DeleteSelectedAsset, () => SelectedAsset != null);

        LoadItemData();
        InitializeEmulators(settings);
    }

    private void LoadItemData()
    {
        // Metadaten laden (Puffer)
        Title = _originalItem.Title;
        Developer = _originalItem.Developer;
        Genre = _originalItem.Genre;
        ReleaseDate = _originalItem.ReleaseDate.HasValue ? new DateTimeOffset(_originalItem.ReleaseDate.Value) : null;
        Status = _originalItem.Status;
        Description = _originalItem.Description;
        MediaType = _originalItem.MediaType;
        
        // Launch Config laden
        LauncherPath = _originalItem.LauncherPath;
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        LauncherArgs = string.IsNullOrWhiteSpace(_originalItem.LauncherArgs) ? "{file}" : _originalItem.LauncherArgs;
        
        // Assets müssen nicht geladen werden, da wir direkt auf _originalItem.Assets zugreifen
        // Der FileService sollte idealerweise vor dem Öffnen dieses Dialogs sicherstellen,
        // dass die Assets-Liste aktuell ist (via RefreshItemAssets).
    }

    private void InitializeEmulators(AppSettings settings)
    {
        AvailableEmulators.Clear();
        AvailableEmulators.Add(new EmulatorConfig { Name = "Custom / Manual", Id = null! });
        
        foreach (var emu in settings.Emulators) 
            AvailableEmulators.Add(emu);

        if (!string.IsNullOrEmpty(_originalItem.EmulatorId))
        {
            SelectedEmulatorProfile = settings.Emulators.FirstOrDefault(e => e.Id == _originalItem.EmulatorId);
        }
        else if (_originalItem.MediaType == MediaType.Native && _inheritedEmulator != null)
        {
            MediaType = MediaType.Emulator;
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == _inheritedEmulator.Id);
        }

        if (SelectedEmulatorProfile == null && MediaType == MediaType.Emulator)
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == null!);
    }

    // --- Asset Actions ---

    private async Task ImportAssetAsync(AssetType type)
    {
        if (StorageProvider == null) return;

        // Filter basierend auf AssetType erstellen
        var fileTypes = type switch
        {
            AssetType.Music => new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.ogg", "*.wav", "*.flac" } } },
            AssetType.Video => new[] { new FilePickerFileType("Video") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.webm" } } },
            _ => new[] { FilePickerFileTypes.ImageAll } // Default für Cover, Logo, Wallpaper, Marquee
        };

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {type}",
            AllowMultiple = true,
            FileTypeFilter = fileTypes
        });

        if (result == null || result.Count == 0) return;

        foreach (var file in result)
        {
            // Der Service übernimmt das Kopieren, Umbenennen und Hinzufügen zur Liste
            _fileService.ImportAsset(file.Path.LocalPath, _originalItem, _nodePath, type);
        }
    }

    private void DeleteSelectedAsset()
    {
        if (SelectedAsset == null) return;

        // Der Service löscht die Datei physisch und entfernt sie aus der Liste
        _fileService.DeleteAsset(_originalItem, SelectedAsset);
        SelectedAsset = null;
    }

    // --- Computed Properties & Helpers ---

    public bool IsManualEmulator => IsEmulatorMode &&
                                    (SelectedEmulatorProfile == null || SelectedEmulatorProfile.Id == null);

    public bool IsEmulatorMode => MediaType == MediaType.Emulator;

    public string PreviewText
    {
        get
        {
            var realFile = !string.IsNullOrEmpty(_originalItem.FilePath)
                ? $"\"{_originalItem.FilePath}\""
                : "\"/Games/SuperMario.smc\"";

            if (SelectedEmulatorProfile != null && SelectedEmulatorProfile.Id != null)
            {
                // FIX: Logik analog zu LauncherService (Smart Injection)
                
                var baseArgs = SelectedEmulatorProfile.Arguments ?? "";
                var itemArgs = LauncherArgs ?? "";
                
                string combinedArgs;

                if (!string.IsNullOrWhiteSpace(itemArgs))
                {
                    if (baseArgs.Contains("{file}") && itemArgs.Contains("{file}"))
                    {
                        // Injection
                        combinedArgs = baseArgs.Replace("{file}", itemArgs);
                    }
                    else
                    {
                        // Append
                        combinedArgs = $"{baseArgs} {itemArgs}".Trim();
                    }
                }
                else
                {
                    combinedArgs = baseArgs;
                }

                // Finales Ersetzen des Pfades (falls {file} noch irgendwo übrig ist)
                if (string.IsNullOrWhiteSpace(combinedArgs))
                {
                    return $"> {SelectedEmulatorProfile.Path} {realFile}";
                }
                
                if (combinedArgs.Contains("{file}"))
                {
                    return $"> {SelectedEmulatorProfile.Path} {combinedArgs.Replace("{file}", realFile)}";
                }
                else
                {
                    // Fallback: Pfad anhängen, falls {file} nirgends mehr steht
                    return $"> {SelectedEmulatorProfile.Path} {combinedArgs} {realFile}";
                }
            }

            if (MediaType == MediaType.Emulator && IsManualEmulator)
            {
                var args = string.IsNullOrWhiteSpace(LauncherArgs)
                    ? realFile
                    : LauncherArgs?.Replace("{file}", realFile) ?? realFile;
                return $"> {LauncherPath} {args}";
            }

            if (MediaType == MediaType.Native)
            {
                if (_inheritedEmulator != null)
                {
                    var args = string.IsNullOrWhiteSpace(_inheritedEmulator.Arguments)
                        ? realFile
                        : _inheritedEmulator.Arguments.Replace("{file}", realFile);
                    return $"(Inherited)\n> {_inheritedEmulator.Path} {args}";
                }
                return $"> {realFile}";
            }
            return "";
        }
    }

    private async Task BrowseLauncherAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Launcher Executable",
            AllowMultiple = false
        });

        if (result != null && result.Count > 0) LauncherPath = result[0].Path.LocalPath;
    }

    private void Save()
    {
        // 1. Metadaten zurückschreiben
        _originalItem.Title = Title;
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.MediaType = MediaType;

        // 2. Launch Config zurückschreiben
        if (SelectedEmulatorProfile != null && SelectedEmulatorProfile.Id != null)
        {
            _originalItem.EmulatorId = SelectedEmulatorProfile.Id;
        }
        else
        {
            _originalItem.EmulatorId = null;
            _originalItem.LauncherPath = LauncherPath;
            _originalItem.LauncherArgs = LauncherArgs;
            _originalItem.OverrideWatchProcess = OverrideWatchProcess;
        }

        // Hinweis: Assets müssen nicht gespeichert werden, 
        // da sie bereits "live" via FileService im _originalItem.Assets gelandet sind.
        
        RequestClose?.Invoke(true);
    }

    private void Cancel()
    {
        // Änderungen an Metadaten verwerfen
        RequestClose?.Invoke(false);
    }
}