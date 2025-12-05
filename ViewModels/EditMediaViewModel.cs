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

namespace Retromind.ViewModels;

public partial class EditMediaViewModel : ViewModelBase
{
    private readonly EmulatorConfig? _inheritedEmulator;
    private readonly MediaItem _originalItem;

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

    // --- Image Properties (Temporär für Bearbeitung) ---
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

    // --- Listen für UI ---
    public ObservableCollection<EmulatorConfig> AvailableEmulators { get; } = new();
    public List<MediaType> MediaTypeOptions { get; } = new() { MediaType.Native, MediaType.Emulator };
    public List<PlayStatus> StatusOptions { get; } = Enum.GetValues<PlayStatus>().ToList();

    // --- Konstruktor ---
    public EditMediaViewModel(MediaItem item, AppSettings settings, EmulatorConfig? inheritedEmulator = null)
    {
        _originalItem = item;
        _inheritedEmulator = inheritedEmulator;

        // 1. Werte kopieren (vom Original ins ViewModel)
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
        
        // Bilder-Pfade kopieren
        CoverPath = item.CoverPath;
        WallpaperPath = item.WallpaperPath;
        LogoPath = item.LogoPath;

        if (string.IsNullOrWhiteSpace(item.LauncherArgs))
            LauncherArgs = "{file}";
        else
            LauncherArgs = item.LauncherArgs;

        // 2. Emulatoren laden
        AvailableEmulators.Add(new EmulatorConfig { Name = "Custom / Manual", Id = null! });
        foreach (var emu in settings.Emulators) AvailableEmulators.Add(emu);

        // 3. Profil Logik initialisieren
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

        // 4. Commands erstellen
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        BrowseLauncherCommand = new AsyncRelayCommand(BrowseLauncherAsync);

        AddImageCommand = new AsyncRelayCommand(AddImageAsync);
        RemoveImageCommand = new RelayCommand(RemoveImage, () => SelectedGalleryImage != null);
        
        SetAsCoverCommand = new RelayCommand(() => SetImageType("Cover"), () => SelectedGalleryImage != null);
        SetAsWallpaperCommand = new RelayCommand(() => SetImageType("Wallpaper"), () => SelectedGalleryImage != null);
        SetAsLogoCommand = new RelayCommand(() => SetImageType("Logo"), () => SelectedGalleryImage != null);
        
        AddMusicCommand = new AsyncRelayCommand(AddMusicAsync);
        SetAsMusicCommand = new RelayCommand(SetAsMusic, () => SelectedAudioItem != null);
        RemoveMusicCommand = new RelayCommand(RemoveMusic, () => SelectedAudioItem != null);

        // 5. Galerie laden
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

    private void AddIfValid(string? path, HashSet<string> distinctPaths)
    {
        if (string.IsNullOrEmpty(path)) return;
            
        // Pfad auflösen (Relativ -> Absolut)
        var fullPath = Path.IsPathRooted(path) 
            ? path 
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            
        fullPath = Path.GetFullPath(fullPath);
            
        if (File.Exists(fullPath) && !distinctPaths.Contains(fullPath.ToLower()))
        {
            var img = new GalleryImage { FilePath = fullPath };
            UpdateImageStatus(img);
            GalleryImages.Add(img);
            distinctPaths.Add(fullPath.ToLower());
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

        string targetDir;
        if (!string.IsNullOrEmpty(_originalItem.FilePath))
             targetDir = Path.Combine(Path.GetDirectoryName(_originalItem.FilePath)!, "media");
        else
             targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Library", "Unknown");

        if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

        foreach (var file in result)
        {
            var sourcePath = file.Path.LocalPath;
            string finalPath = sourcePath;
            bool isDuplicate = false;

            var newHash = FileHelper.CalculateMd5(sourcePath);

            foreach (var existingFile in Directory.GetFiles(targetDir))
            {
                if (new FileInfo(existingFile).Length != new FileInfo(sourcePath).Length) continue;

                if (FileHelper.CalculateMd5(existingFile) == newHash)
                {
                    finalPath = existingFile;
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                var fileName = Path.GetFileName(sourcePath);
                var targetPath = Path.Combine(targetDir, fileName);
                
                int c = 1;
                while (File.Exists(targetPath))
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    targetPath = Path.Combine(targetDir, $"{name}_{c}{ext}");
                    c++;
                }
                
                File.Copy(sourcePath, targetPath);
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

    private void SetImageType(string type)
    {
        if (SelectedGalleryImage == null) return;
        
        switch (type)
        {
            case "Cover":
                CoverPath = SelectedGalleryImage.FilePath;
                break;
            case "Wallpaper":
                WallpaperPath = SelectedGalleryImage.FilePath;
                break;
            case "Logo":
                LogoPath = SelectedGalleryImage.FilePath;
                break;
        }

        foreach(var img in GalleryImages) UpdateImageStatus(img);
    }

    private void RemoveImage()
    {
        if (SelectedGalleryImage == null) return;

        try 
        {
            if (CoverPath == SelectedGalleryImage.FilePath) CoverPath = null;
            if (WallpaperPath == SelectedGalleryImage.FilePath) WallpaperPath = null;
            if (LogoPath == SelectedGalleryImage.FilePath) LogoPath = null;

            if (File.Exists(SelectedGalleryImage.FilePath))
                File.Delete(SelectedGalleryImage.FilePath);
            
            GalleryImages.Remove(SelectedGalleryImage);
        }
        catch
        {
            // Ignorieren
        }
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

    // NEU: Musik laden
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

    private async Task AddMusicAsync()
    {
        if (StorageProvider == null) return;
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Musik hinzufügen",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.ogg", "*.wav" } } }
        });

        // Kopier-Logik analog zu Bildern (gekürzt):
        if (result == null) return;
        // ... Zielordner bestimmen, Kopieren, Checksumme ...
        // (Hier exakt denselben Code wie bei AddImage nutzen, nur für Audio)
        // Am Ende: AddAudio(finalPath);
    }

    private void SetAsMusic()
    {
        if (SelectedAudioItem == null) return;
        MusicPath = SelectedAudioItem.FilePath;
        
        // UI Update
        foreach(var a in AvailableMusic) 
            a.IsActive = (a.FilePath == MusicPath);
    }

    private void RemoveMusic()
    {
        // Analog zu RemoveImage...
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
        
        // Konvertierung zurück zu DateTime?
        _originalItem.ReleaseDate = ReleaseDate?.DateTime;
        
        _originalItem.Status = Status;
        _originalItem.Description = Description;
        _originalItem.MediaType = MediaType;
        _originalItem.MusicPath = MusicPath;

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
        }

        RequestClose?.Invoke(true);
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