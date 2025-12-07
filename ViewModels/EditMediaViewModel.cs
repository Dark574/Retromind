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
using Retromind.Helpers;
using Retromind.Services;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel for editing metadata, media files, and emulator configurations of a MediaItem.
/// </summary>
public partial class EditMediaViewModel : ViewModelBase
{
    private readonly EmulatorConfig? _inheritedEmulator;
    private readonly MediaItem _originalItem;
    
    // Dependencies for proper file handling
    private readonly FileManagementService _fileService;
    private readonly List<string> _nodePath;

    // --- Metadata Properties ---
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _genre;
    
    // Avalonia DatePicker uses DateTimeOffset?, MediaItem uses DateTime?
    // We bind to this DateTimeOffset? and convert back on Save.
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

    // --- Image Properties (Absolute Paths for UI) ---
    [ObservableProperty] private string? _coverPath;
    [ObservableProperty] private string? _wallpaperPath;
    [ObservableProperty] private string? _logoPath;
    
    // --- Audio Properties ---
    [ObservableProperty] private string? _musicPath;
    public ObservableCollection<AudioItem> AvailableMusic { get; } = new();
    [ObservableProperty] private AudioItem? _selectedAudioItem;

    // --- Gallery ---
    public ObservableCollection<GalleryImage> GalleryImages { get; } = new();
    [ObservableProperty] private GalleryImage? _selectedGalleryImage;

    // --- Commands ---
    public IRelayCommand AddImageCommand { get; }
    public IAsyncRelayCommand ImportCoverCommand { get; }
    public IAsyncRelayCommand ImportLogoCommand { get; }
    public IAsyncRelayCommand ImportWallpaperCommand { get; }
    public IRelayCommand RemoveImageCommand { get; }
    public IRelayCommand SetAsCoverCommand { get; }
    public IRelayCommand SetAsWallpaperCommand { get; }
    public IRelayCommand SetAsLogoCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand BrowseLauncherCommand { get; }
    public IRelayCommand AddMusicCommand { get; }
    public IRelayCommand SetAsMusicCommand { get; }
    public IRelayCommand RemoveMusicCommand { get; }

    public IStorageProvider? StorageProvider { get; set; }
    
    // Event to signal the view to close (true = saved, false = cancelled)
    public event Action<bool>? RequestClose;

    // --- Lists for UI ---
    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();
    public List<MediaType> MediaTypeOptions { get; } = new() { MediaType.Native, MediaType.Emulator };
    public List<PlayStatus> StatusOptions { get; } = Enum.GetValues<PlayStatus>().ToList();

    // --- Constructor ---
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

        // Initialize Commands
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);

        AddImageCommand = new AsyncRelayCommand(AddImageAsync);
        ImportCoverCommand = new AsyncRelayCommand(() => ImportImagesDirectlyAsync(MediaFileType.Cover));
        ImportLogoCommand = new AsyncRelayCommand(() => ImportImagesDirectlyAsync(MediaFileType.Logo));
        ImportWallpaperCommand = new AsyncRelayCommand(() => ImportImagesDirectlyAsync(MediaFileType.Wallpaper));
        RemoveImageCommand = new RelayCommand(RemoveImage, () => SelectedGalleryImage != null);
        
        SetAsCoverCommand = new RelayCommand(() => SetImageType(MediaFileType.Cover), () => SelectedGalleryImage != null);
        SetAsWallpaperCommand = new RelayCommand(() => SetImageType(MediaFileType.Wallpaper), () => SelectedGalleryImage != null);
        SetAsLogoCommand = new RelayCommand(() => SetImageType(MediaFileType.Logo), () => SelectedGalleryImage != null);
        
        AddMusicCommand = new AsyncRelayCommand(ImportMusicAsync);
        SetAsMusicCommand = new RelayCommand(SetAsMusic, () => SelectedAudioItem != null);
        RemoveMusicCommand = new RelayCommand(RemoveMusic, () => SelectedAudioItem != null);

        // Initialization Logic
        LoadItemData();
        InitializeEmulators(settings);
        
        // Load external assets
        LoadGalleryImages();
        LoadMusicFiles();
    }

    private void LoadItemData()
    {
        Title = _originalItem.Title;
        Developer = _originalItem.Developer;
        Genre = _originalItem.Genre;
        ReleaseDate = _originalItem.ReleaseDate.HasValue ? new DateTimeOffset(_originalItem.ReleaseDate.Value) : null;
        Status = _originalItem.Status;
        Description = _originalItem.Description;
        MediaType = _originalItem.MediaType;
        LauncherPath = _originalItem.LauncherPath;
        MusicPath = ToAbsolutePath(_originalItem.MusicPath);
        OverrideWatchProcess = _originalItem.OverrideWatchProcess ?? string.Empty;
        
        CoverPath = ToAbsolutePath(_originalItem.CoverPath);
        WallpaperPath = ToAbsolutePath(_originalItem.WallpaperPath);
        LogoPath = ToAbsolutePath(_originalItem.LogoPath);

        LauncherArgs = string.IsNullOrWhiteSpace(_originalItem.LauncherArgs) ? "{file}" : _originalItem.LauncherArgs;
    }

    private void InitializeEmulators(AppSettings settings)
    {
        AvailableEmulators.Clear();
        AvailableEmulators.Add(new EmulatorConfig { Name = "Custom / Manual", Id = null! });
        
        foreach (var emu in settings.Emulators) 
            AvailableEmulators.Add(emu);

        // Pre-select correct emulator
        if (!string.IsNullOrEmpty(_originalItem.EmulatorId))
        {
            SelectedEmulatorProfile = settings.Emulators.FirstOrDefault(e => e.Id == _originalItem.EmulatorId);
        }
        else if (_originalItem.MediaType == MediaType.Native && _inheritedEmulator != null)
        {
            // If it was native but runs via inherited emulator, suggest that one
            MediaType = MediaType.Emulator;
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == _inheritedEmulator.Id);
        }

        // Default fallback
        if (SelectedEmulatorProfile == null && MediaType == MediaType.Emulator)
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == null!);
    }

    // --- Computed Properties ---
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
                var args = string.IsNullOrWhiteSpace(SelectedEmulatorProfile.Arguments)
                    ? realFile
                    : SelectedEmulatorProfile.Arguments.Replace("{file}", realFile);
                return $"> {SelectedEmulatorProfile.Path} {args}";
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

    // --- Gallery Logic ---

    private void LoadGalleryImages()
    {
        GalleryImages.Clear();
        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Helper to add images avoiding duplicates
        void AddImage(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var fullPath = ToAbsolutePath(path);
            
            if (fullPath != null && !distinctPaths.Contains(fullPath) && File.Exists(fullPath))
            {
                var img = new GalleryImage { FilePath = fullPath };
                // Image status is updated later in batch
                GalleryImages.Add(img);
                distinctPaths.Add(fullPath);
            }
        }

        // 1. Add currently assigned images
        AddImage(CoverPath);
        AddImage(WallpaperPath);
        AddImage(LogoPath);

        // 2. Search for other images in related folders
        var tempItem = new MediaItem 
        { 
            Title = Title, 
            FilePath = _originalItem.FilePath,
            CoverPath = CoverPath, 
            WallpaperPath = WallpaperPath
        };

        var foundImages = MediaSearchHelper.FindPotentialImages(tempItem);
        foreach (var imgPath in foundImages)
        {
            AddImage(imgPath);
        }
        
        UpdateAllImageStatuses();
    }

    /// <summary>
    /// Updates the IsCover/IsWallpaper/IsLogo flags for all gallery images.
    /// Optimized to calculate absolute paths only once.
    /// </summary>
    private void UpdateAllImageStatuses()
    {
        var absCover = string.IsNullOrEmpty(CoverPath) ? null : Path.GetFullPath(CoverPath);
        var absWall = string.IsNullOrEmpty(WallpaperPath) ? null : Path.GetFullPath(WallpaperPath);
        var absLogo = string.IsNullOrEmpty(LogoPath) ? null : Path.GetFullPath(LogoPath);

        foreach (var img in GalleryImages)
        {
            // GalleryImage.FilePath is already absolute
            img.IsCover = string.Equals(img.FilePath, absCover, StringComparison.OrdinalIgnoreCase);
            img.IsWallpaper = string.Equals(img.FilePath, absWall, StringComparison.OrdinalIgnoreCase);
            img.IsLogo = string.Equals(img.FilePath, absLogo, StringComparison.OrdinalIgnoreCase);
        }
    }
    
    // Updates a single image (fallback)
    private void UpdateImageStatus(GalleryImage img)
    {
         UpdateAllImageStatuses(); 
    }

    private async Task AddImageAsync()
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Image",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        if (result == null || result.Count == 0) return;

        // Use a safe temporary cache folder instead of "media" near the ROM.
        var tempDir = Path.Combine(Path.GetTempPath(), "RetromindCache", "Gallery");
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
        
        // Run I/O heavy tasks on background thread
        await Task.Run(() => 
        {
            foreach (var file in result)
            {
                try 
                {
                    var sourcePath = file.Path.LocalPath;
                    string finalPath = sourcePath;

                    // Simple copy to cache logic
                    var rawName = Path.GetFileNameWithoutExtension(sourcePath);
                    var cleanName = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars()));
                    var ext = Path.GetExtension(sourcePath);
                    var uniqueName = $"{cleanName}_{Guid.NewGuid()}{ext}";
            
                    var targetPath = Path.Combine(tempDir, uniqueName);
                    File.Copy(sourcePath, targetPath, true);
                    finalPath = targetPath;

                    // Update UI on UI Thread
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                    {
                         if (!GalleryImages.Any(g => g.FilePath == finalPath))
                         {
                             var newImg = new GalleryImage { FilePath = finalPath };
                             GalleryImages.Add(newImg);
                             UpdateImageStatus(newImg);
                         }
                    });
                }
                catch (Exception ex)
                {
                    // Log error?
                    System.Diagnostics.Debug.WriteLine($"Error adding image: {ex.Message}");
                }
            }
        });
    }

    private async Task ImportImagesDirectlyAsync(MediaFileType type)
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Import {type}",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        if (result == null || result.Count == 0) return;

        foreach (var file in result)
        {
            var relPath = _fileService.ImportAsset(file.Path.LocalPath, _originalItem, _nodePath, type);

            if (!string.IsNullOrEmpty(relPath))
            {
                var fullPath = ToAbsolutePath(relPath);
                
                if (fullPath != null && !GalleryImages.Any(g => g.FilePath == fullPath))
                {
                    var newImg = new GalleryImage { FilePath = fullPath };
                    GalleryImages.Add(newImg);
                }

                // If no image is set for this type, set the first imported one
                if (type == MediaFileType.Cover && string.IsNullOrEmpty(CoverPath)) CoverPath = fullPath;
                if (type == MediaFileType.Wallpaper && string.IsNullOrEmpty(WallpaperPath)) WallpaperPath = fullPath;
                if (type == MediaFileType.Logo && string.IsNullOrEmpty(LogoPath)) LogoPath = fullPath;
            }
        }
        UpdateAllImageStatuses();
    }
        
    private void SetImageType(MediaFileType type)
    {
        if (SelectedGalleryImage == null) return;
        
        var newRelPath = _fileService.ImportAsset(SelectedGalleryImage.FilePath, _originalItem, _nodePath, type);

        if (string.IsNullOrEmpty(newRelPath)) return;

        var fullPath = ToAbsolutePath(newRelPath);

        switch (type)
        {
            case MediaFileType.Cover:
                CoverPath = fullPath; 
                break;
            case MediaFileType.Wallpaper:
                WallpaperPath = fullPath;
                break;
            case MediaFileType.Logo:
                LogoPath = fullPath;
                break;
        }

        // Update the gallery item to point to the new location
        if (fullPath != null) SelectedGalleryImage.FilePath = fullPath;
        
        UpdateAllImageStatuses();
    }

    private void RemoveImage()
    {
        if (SelectedGalleryImage == null) return;

        // 1. Remove references
        if (CoverPath == SelectedGalleryImage.FilePath) CoverPath = null;
        if (WallpaperPath == SelectedGalleryImage.FilePath) WallpaperPath = null;
        if (LogoPath == SelectedGalleryImage.FilePath) LogoPath = null;
        
        // 2. Try to physically delete (service checks safety)
        _fileService.DeleteAsset(SelectedGalleryImage.FilePath);

        // 3. Remove from UI
        GalleryImages.Remove(SelectedGalleryImage);
    }

    partial void OnSelectedGalleryImageChanged(GalleryImage? value)
    {
        RemoveImageCommand.NotifyCanExecuteChanged();
        SetAsCoverCommand.NotifyCanExecuteChanged();
        SetAsWallpaperCommand.NotifyCanExecuteChanged();
        SetAsLogoCommand.NotifyCanExecuteChanged();
    }

    // --- Actions ---

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

    private void LoadMusicFiles()
    {
        AvailableMusic.Clear();
        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAudio(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var fullPath = ToAbsolutePath(path);
            
            if (fullPath != null && !distinctPaths.Contains(fullPath) && File.Exists(fullPath))
            {
                var isCurrent = string.Equals(fullPath, ToAbsolutePath(MusicPath), StringComparison.OrdinalIgnoreCase);
                AvailableMusic.Add(new AudioItem { FilePath = fullPath, IsActive = isCurrent });
                distinctPaths.Add(fullPath);
            }
        }

        AddAudio(MusicPath);

        // Search for potential music files
        var tempItem = new MediaItem { Title = Title, FilePath = _originalItem.FilePath, MusicPath = MusicPath };
        var found = MediaSearchHelper.FindPotentialAudio(tempItem);
        foreach (var f in found) AddAudio(f);
    }

    private async Task ImportMusicAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Music",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.sid" } } }
        });

        if (result == null) return;
        
        foreach (var file in result)
        {
            var relPath = _fileService.ImportAsset(file.Path.LocalPath, _originalItem, _nodePath, MediaFileType.Music);

            if (!string.IsNullOrEmpty(relPath))
            {
                var fullPath = ToAbsolutePath(relPath);
                    
                if (fullPath != null && !AvailableMusic.Any(a => a.FilePath == fullPath))
                {
                    AvailableMusic.Add(new AudioItem { FilePath = fullPath, IsActive = false });
                }
                    
                if (string.IsNullOrEmpty(MusicPath))
                {
                    MusicPath = fullPath;
                    foreach(var a in AvailableMusic) a.IsActive = (a.FilePath == fullPath);
                }
            }
        }
    }

    private void SetAsMusic()
    {
        if (SelectedAudioItem == null) return;

        var relPath = _fileService.ImportAsset(SelectedAudioItem.FilePath, _originalItem, _nodePath, MediaFileType.Music);
        
        if (!string.IsNullOrEmpty(relPath))
        {
            var fullPath = ToAbsolutePath(relPath);
            MusicPath = fullPath; 

            foreach(var a in AvailableMusic) 
                a.IsActive = (a.FilePath == MusicPath);
        }
    }

    private void RemoveMusic()
    {
        if (SelectedAudioItem == null) return;
        
        var fileToDelete = SelectedAudioItem.FilePath;
        
        if (MusicPath == fileToDelete) MusicPath = null;

        _fileService.DeleteAsset(fileToDelete);
        AvailableMusic.Remove(SelectedAudioItem);
    }

    partial void OnSelectedAudioItemChanged(AudioItem? value)
    {
        SetAsMusicCommand.NotifyCanExecuteChanged();
        RemoveMusicCommand.NotifyCanExecuteChanged();
    }
    
    private void Save()
    {
        // Copy metadata
        _originalItem.Title = Title;
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.MediaType = MediaType;

        // Save paths as relative to project root if possible to ensure portability
        _originalItem.MusicPath = MakeRelative(MusicPath);
        _originalItem.CoverPath = MakeRelative(CoverPath);
        _originalItem.WallpaperPath = MakeRelative(WallpaperPath);
        _originalItem.LogoPath = MakeRelative(LogoPath);

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

        RequestClose?.Invoke(true);
    }

    // --- Helpers ---

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
    
    private void Cancel()
    {
        RequestClose?.Invoke(false);
    }
}

public partial class GalleryImage : ObservableObject
{
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private bool _isCover;
    [ObservableProperty] private bool _isWallpaper;
    [ObservableProperty] private bool _isLogo;
    
    public string FileName => System.IO.Path.GetFileName(FilePath);
}

public partial class AudioItem : ObservableObject
{
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private bool _isActive;
    public string FileName => System.IO.Path.GetFileName(FilePath);
}