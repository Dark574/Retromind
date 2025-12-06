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
    
    // Avalonia DatePicker nutzt DateTimeOffset?, MediaItem nutzt DateTime?
    // Wir nutzen hier DateTimeOffset? für das Binding und konvertieren beim Speichern.
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

    // --- Image Properties ---
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
        _nodePath = nodePath; // Store path for correct folder structure (Media/Genre/...)
        _inheritedEmulator = inheritedEmulator;

        // 1. Copy values
        Title = item.Title;
        Developer = item.Developer;
        Genre = item.Genre;
        
        // Konvertierung DateTime -> DateTimeOffset für DatePicker
        ReleaseDate = item.ReleaseDate.HasValue ? new DateTimeOffset(item.ReleaseDate.Value) : null;
        
        Status = item.Status;
        Description = item.Description;
        MediaType = item.MediaType;
        LauncherPath = item.LauncherPath;
        MusicPath = item.MusicPath;
        OverrideWatchProcess = item.OverrideWatchProcess ?? string.Empty;
        
        // Bilder-Pfade kopieren
        CoverPath = item.CoverPath;
        WallpaperPath = item.WallpaperPath;
        LogoPath = item.LogoPath;

        if (string.IsNullOrWhiteSpace(item.LauncherArgs))
            LauncherArgs = "{file}";
        else
            LauncherArgs = item.LauncherArgs;

        // 2. Load Emulators
        AvailableEmulators.Add(new EmulatorConfig { Name = "Custom / Manual", Id = null! });
        foreach (var emu in settings.Emulators) AvailableEmulators.Add(emu);

        // 3. Initialize Profile Logic
        if (!string.IsNullOrEmpty(item.EmulatorId))
        {
            SelectedEmulatorProfile = settings.Emulators.FirstOrDefault(e => e.Id == item.EmulatorId);
        }
        else if (item.MediaType == MediaType.Native && inheritedEmulator != null)
        {
            MediaType = MediaType.Emulator;
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == inheritedEmulator.Id);
        }

        if (SelectedEmulatorProfile == null && MediaType == MediaType.Emulator)
            SelectedEmulatorProfile = AvailableEmulators.FirstOrDefault(e => e.Id == null!);

        // 4. Create Commands
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

        // 5. Load Gallery
        LoadGalleryImages();
        LoadMusicFiles();
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
                    : LauncherArgs.Replace("{file}", realFile);
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

    // --- Galerie Logik ---

    private void LoadGalleryImages()
    {
        GalleryImages.Clear();
        var distinctPaths = new HashSet<string>();

        // Helper-Funktion zum Hinzufügen (vermeidet Code-Duplizierung)
        void AddImage(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var fullPath = Path.GetFullPath(path); // Absolut machen für Vergleich
            
            if (!distinctPaths.Contains(fullPath.ToLower()) && File.Exists(fullPath))
            {
                var img = new GalleryImage { FilePath = fullPath };
                UpdateImageStatus(img);
                GalleryImages.Add(img);
                distinctPaths.Add(fullPath.ToLower());
            }
        }

        // 1. Aktuelle Bilder zuerst hinzufügen (damit sie sicher da sind)
        // Wir prüfen hier Pfade auf null und lösen sie auf
        if (!string.IsNullOrEmpty(CoverPath)) 
            AddImage(Path.IsPathRooted(CoverPath) ? CoverPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CoverPath));
        
        if (!string.IsNullOrEmpty(WallpaperPath)) 
            AddImage(Path.IsPathRooted(WallpaperPath) ? WallpaperPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WallpaperPath));
        
        if (!string.IsNullOrEmpty(LogoPath)) 
            AddImage(Path.IsPathRooted(LogoPath) ? LogoPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogoPath));

        // 2. Suche nach weiteren Bildern über den neuen Helper
        // Wir müssen ein temporäres Item bauen oder das Original nutzen, 
        // aber mit den neuen Pfaden, damit der Helper die richtigen Ordner scannt.
        var tempItem = new MediaItem 
        { 
            Title = Title, 
            FilePath = _originalItem.FilePath,
            CoverPath = CoverPath, // Damit auch Ordner von neuen Covers gescannt werden
            WallpaperPath = WallpaperPath
        };

        var foundImages = MediaSearchHelper.FindPotentialImages(tempItem);
        
        foreach (var imgPath in foundImages)
        {
            AddImage(imgPath);
        }
    }

    private void UpdateImageStatus(GalleryImage img)
    {
        var absImg = Path.GetFullPath(img.FilePath);

        var absCover = string.IsNullOrEmpty(CoverPath) ? null : Path.GetFullPath(CoverPath);
        var absWall = string.IsNullOrEmpty(WallpaperPath) ? null : Path.GetFullPath(WallpaperPath);
        var absLogo = string.IsNullOrEmpty(LogoPath) ? null : Path.GetFullPath(LogoPath);

        img.IsCover = absImg == absCover;
        img.IsWallpaper = absImg == absWall;
        img.IsLogo = absImg == absLogo;
    }

    private async Task AddImageAsync()
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Bild hinzufügen",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        if (result == null || result.Count == 0) return;

        // Use a safe temporary cache folder instead of "media" near the ROM.
        // This keeps the user's ROM folder clean.
        var tempDir = Path.Combine(Path.GetTempPath(), "RetromindCache", "Gallery");
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
        
        foreach (var file in result)
        {
            var sourcePath = file.Path.LocalPath;
            string finalPath = sourcePath;
            bool isDuplicate = false;

            // Hash-Berechnung in Task auslagern, um UI-Freeze zu verhindern
            var newHash = await Task.Run(() => FileHelper.CalculateMd5(sourcePath));
            
            // Wir müssen dies threadsicher machen oder snapshotten, da wir auf IO zugreifen
            var existingFiles = Directory.GetFiles(tempDir);

            // Hash-Vergleich auch im Task (optional, aber sauberer)
            var duplicateFound = await Task.Run(() => 
            {
                foreach (var existingFile in existingFiles)
                {
                    if (new FileInfo(existingFile).Length != new FileInfo(sourcePath).Length) continue;
                    if (FileHelper.CalculateMd5(existingFile) == newHash)
                    {
                        return existingFile;
                    }
                }
                return null;
            });

            if (duplicateFound != null)
            {
                finalPath = duplicateFound;
                isDuplicate = true;
            }

            if (!isDuplicate)
            {
                // Copy to temp if not exists
                // Sanitize simple:
                var rawName = Path.GetFileNameWithoutExtension(sourcePath);
                var cleanName = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars()));
                    
                var ext = Path.GetExtension(sourcePath);
                var uniqueName = $"{cleanName}_{Guid.NewGuid()}{ext}"; // Name_GUID.ext
        
                var targetPath = Path.Combine(tempDir, uniqueName);
    
                await Task.Run(() => File.Copy(sourcePath, targetPath, true));
                finalPath = targetPath;
            }

            if (!GalleryImages.Any(g => g.FilePath == finalPath))
            {
                var newImg = new GalleryImage { FilePath = finalPath };
                UpdateImageStatus(newImg);
                GalleryImages.Add(newImg);
            }
        }
    }

    // Importiert Bilder direkt in den richtigen Ordner (Cover/Wallpaper) ohne sie sofort aktiv zu setzen
    private async Task ImportImagesDirectlyAsync(MediaFileType type)
    {
        if (StorageProvider == null) return;

        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"{type} importieren",
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
        });

        if (result == null || result.Count == 0) return;

        foreach (var file in result)
        {
            // Direkt via FileService importieren (benennt um und verschiebt nach Medien/...)
            var relPath = _fileService.ImportAsset(file.Path.LocalPath, _originalItem, _nodePath, type);

            if (!string.IsNullOrEmpty(relPath))
            {
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relPath);
                
                // Zur Galerie hinzufügen, falls noch nicht da
                if (!GalleryImages.Any(g => g.FilePath == fullPath))
                {
                    var newImg = new GalleryImage { FilePath = fullPath };
                    UpdateImageStatus(newImg);
                    GalleryImages.Add(newImg);
                }

                // Wenn wir noch GAR KEIN Cover/Wallpaper haben, setzen wir das erste importierte direkt
                if (type == MediaFileType.Cover && string.IsNullOrEmpty(CoverPath)) CoverPath = fullPath;
                if (type == MediaFileType.Wallpaper && string.IsNullOrEmpty(WallpaperPath)) WallpaperPath = fullPath;
                if (type == MediaFileType.Logo && string.IsNullOrEmpty(LogoPath)) LogoPath = fullPath;
            }
        }
        
        // Status aller Bilder aktualisieren
        foreach(var img in GalleryImages) UpdateImageStatus(img);
    }
        
    private void SetImageType(MediaFileType type)
    {
        if (SelectedGalleryImage == null) return;
        
        // Here we use the FileManagementService to properly import the file 
        // into the correct structure (Medien/Genre/Cover/...).
        // This ensures clean organization.
        var newRelPath = _fileService.ImportAsset(SelectedGalleryImage.FilePath, _originalItem, _nodePath, type);

        if (string.IsNullOrEmpty(newRelPath)) return;

        // The ImportAsset returns a relative path. We might need the absolute path for the UI refresh immediately.
        var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, newRelPath);

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

        // Update the gallery item to point to the new, correct location
        // so the "IsCover" check works correctly against the new path.
        SelectedGalleryImage.FilePath = fullPath;
        
        foreach(var img in GalleryImages) UpdateImageStatus(img);
    }

    private void RemoveImage()
    {
        if (SelectedGalleryImage == null) return;

        // 1. Prüfen ob das Bild aktuell verwendet wird -> Referenz entfernen
        if (CoverPath == SelectedGalleryImage.FilePath) CoverPath = null;
        if (WallpaperPath == SelectedGalleryImage.FilePath) WallpaperPath = null;
        if (LogoPath == SelectedGalleryImage.FilePath) LogoPath = null;
        
        // 2. Versuchen die Datei physikalisch zu löschen
        // Der FileService prüft intern, ob es sicher ist (im App-Verzeichnis)
        _fileService.DeleteAsset(SelectedGalleryImage.FilePath);

        // 3. Aus der UI-Liste entfernen
        GalleryImages.Remove(SelectedGalleryImage);
    }

    partial void OnSelectedGalleryImageChanged(GalleryImage? value)
    {
        RemoveImageCommand.NotifyCanExecuteChanged();
        SetAsCoverCommand.NotifyCanExecuteChanged();
        SetAsWallpaperCommand.NotifyCanExecuteChanged();
        SetAsLogoCommand.NotifyCanExecuteChanged();
    }

    // --- Aktionen ---

    private async Task BrowseLauncherAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Launcher auswählen",
            AllowMultiple = false
        });

        if (result != null && result.Count > 0) LauncherPath = result[0].Path.LocalPath;
    }

    // load Music
    private void LoadMusicFiles()
    {
        AvailableMusic.Clear();
        var distinctPaths = new HashSet<string>();

        void AddAudio(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var fullPath = Path.GetFullPath(path);
            if (distinctPaths.Contains(fullPath.ToLower()) || !File.Exists(fullPath)) return;

            var isCurrent = fullPath == (string.IsNullOrEmpty(MusicPath) ? "" : Path.GetFullPath(MusicPath));
            AvailableMusic.Add(new AudioItem { FilePath = fullPath, IsActive = isCurrent });
            distinctPaths.Add(fullPath.ToLower());
        }

        if (!string.IsNullOrEmpty(MusicPath)) 
            AddAudio(Path.IsPathRooted(MusicPath) ? MusicPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, MusicPath));

        // Suchen
        var tempItem = new MediaItem { Title = Title, FilePath = _originalItem.FilePath, MusicPath = MusicPath };
        var found = MediaSearchHelper.FindPotentialAudio(tempItem);
        foreach (var f in found) AddAudio(f);
    }

    private async Task ImportMusicAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Musik importieren",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.ogg", "*.wav", "*.flac", "*.sid" } } }
        });

        if (result == null) return;
        
        // Wir nutzen direkt den FileService zum Importieren!
        // Kein Temp-Ordner mehr nötig.
            
        foreach (var file in result)
        {
            // ImportAsset kümmert sich um Ordnerstruktur und Namenskonvention (Originalname behalten)
            var relPath = _fileService.ImportAsset(file.Path.LocalPath, _originalItem, _nodePath, MediaFileType.Music);

            if (!string.IsNullOrEmpty(relPath))
            {
                var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relPath);
                    
                // Zur Liste hinzufügen, falls noch nicht da
                if (!AvailableMusic.Any(a => a.FilePath == fullPath))
                {
                    AvailableMusic.Add(new AudioItem { FilePath = fullPath, IsActive = false });
                }
                    
                // Optional: Wenn noch gar keine Musik gesetzt ist, setze die erste importierte automatisch als aktiv
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

        // Nicht nur Pfad setzen, sondern Importieren!
        // Das kopiert die Datei nach Medien/Gruppe/Music/[OriginalName].ext
        var relPath = _fileService.ImportAsset(SelectedAudioItem.FilePath, _originalItem, _nodePath, MediaFileType.Music);
        
        if (!string.IsNullOrEmpty(relPath))
        {
            // Wir müssen den Pfad absolut machen für die UI-Anzeige/Abspielen,
            // speichern tun wir später relative.
            var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relPath);
            MusicPath = fullPath; 

            // UI Update
            foreach(var a in AvailableMusic) 
                a.IsActive = (a.FilePath == MusicPath);
        }
    }

    private void RemoveMusic()
    {
        if (SelectedAudioItem == null) return;
        
        var fileToDelete = SelectedAudioItem.FilePath;
        
        // 1. Referenz entfernen
        if (MusicPath == fileToDelete) MusicPath = null;

        // 2. Physikalisch löschen
        _fileService.DeleteAsset(fileToDelete);

        // 3. Aus der Liste entfernen
        AvailableMusic.Remove(SelectedAudioItem);
    }

    partial void OnSelectedAudioItemChanged(AudioItem? value)
    {
        SetAsMusicCommand.NotifyCanExecuteChanged();
        RemoveMusicCommand.NotifyCanExecuteChanged();
    }
    
    private void Save()
    {
        _originalItem.Title = Title;
        _originalItem.Developer = Developer;
        _originalItem.Genre = Genre;
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.MediaType = MediaType;

        // MusicPath is already updated relative in SetAsMusic
        _originalItem.MusicPath = MusicPath;

        // Convert absolute paths back to relative for storage if they are inside BaseDirectory
        _originalItem.CoverPath = CoverPath;
        _originalItem.WallpaperPath = WallpaperPath;
        _originalItem.LogoPath = LogoPath;

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

    private string? MakeRelativeIfPossible(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (path.StartsWith(baseDir))
        {
            return Path.GetRelativePath(baseDir, path);
        }
        return path;
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