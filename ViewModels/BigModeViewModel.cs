using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Retromind.Models;

namespace Retromind.ViewModels;

/// <summary>
/// ViewModel acting as the DataContext for the Big Picture / Themed mode.
/// Theme creators will bind to properties exposed here.
/// </summary>
public partial class BigModeViewModel : ViewModelBase
{
    private readonly LibVLC _libVlc;
    
    // Wir merken uns den Pfad, den wir gegangen sind (für den "Zurück"-Button)
    private readonly Stack<ObservableCollection<MediaNode>> _navigationStack = new();
    private readonly Stack<string> _titleStack = new();

    // Die "Wurzel"-Kategorien (Bibliothek, Settings, etc.)
    private readonly ObservableCollection<MediaNode> _rootNodes;

    private bool _isLaunching;
    private string _currentThemePath = string.Empty;
    private CancellationTokenSource? _previewCts;

    // --- VIEW STATUS ---

    // True = Wir zeigen eine Liste von Spiele an.
    // False = Wir zeigen eine Liste von Ordnern (Kategorien) an.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategorySelectionActive))] 
    private bool _isGameListActive = false;

    public bool IsCategorySelectionActive => !IsGameListActive;

    // Die Liste der KATEGORIEN / ORDNER, die aktuell angezeigt wird
    [ObservableProperty]
    private ObservableCollection<MediaNode> _currentCategories;

    // Der aktuell ausgewählte ORDNER
    [ObservableProperty] 
    private MediaNode? _selectedCategory;

    // -------------------

    [ObservableProperty] private MediaNode? _currentNode;
    [ObservableProperty] private string _currentThemeDirectory = string.Empty;
    [ObservableProperty] private MediaPlayer? _mediaPlayer;
    
    // Die Liste der SPIELE (nur relevant wenn IsGameListActive = true)
    [ObservableProperty] private ObservableCollection<MediaItem> _items = new();
    [ObservableProperty] private MediaItem? _selectedItem;
    
    [ObservableProperty] private string _categoryTitle = "Main Menu";

    public event Action? RequestClose;
    public event Action<MediaItem>? RequestPlay;
    public event Action<string>? RequestThemeChange;

    public BigModeViewModel(ObservableCollection<MediaNode> rootNodes, MediaNode? startNode = null)
    {
        _rootNodes = rootNodes;
        _currentCategories = _rootNodes; // Start: Zeige Root Nodes

        string[] vlcOptions = { "--no-xlib", "--vout=x11" };
        _libVlc = new LibVLC(enableDebugLogs: false, vlcOptions); 
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Volume = 100;
        
        // Initiale Auswahl
        if (_currentCategories.Count > 0)
        {
            SelectedCategory = _currentCategories[0];
            UpdateThemeForNode(SelectedCategory);
            // NEU: Sofort Vorschau für die erste Kategorie starten
            PlayCategoryPreview(SelectedCategory); 
        }
    }

    // UP / LEFT
    [RelayCommand]
    private void SelectPrevious()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            // Navigation in SPIELEN
            if (Items.Count == 0) return;
            if (SelectedItem == null) { SelectedItem = Items.Last(); return; }
            var index = Items.IndexOf(SelectedItem);
            if (index > 0) SelectedItem = Items[index - 1];
        }
        else
        {
            // Navigation in ORDNERN
            if (CurrentCategories.Count == 0) return;
            if (SelectedCategory == null) { SelectedCategory = CurrentCategories.Last(); return; }
            var index = CurrentCategories.IndexOf(SelectedCategory);
            if (index > 0) SelectedCategory = CurrentCategories[index - 1];
            else SelectedCategory = CurrentCategories.Last();
            
            UpdateThemeForNode(SelectedCategory);
            // NEU: Vorschau für Kategorie starten
            PlayCategoryPreview(SelectedCategory);
        }
    }

    // DOWN / RIGHT
    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return;

        if (IsGameListActive)
        {
            // Navigation in SPIELEN
            if (Items.Count == 0) return;
            if (SelectedItem == null) { SelectedItem = Items.First(); return; }
            var index = Items.IndexOf(SelectedItem);
            if (index < Items.Count - 1) SelectedItem = Items[index + 1];
        }
        else
        {
            // Navigation in ORDNERN
            if (CurrentCategories.Count == 0) return;
            if (SelectedCategory == null) { SelectedCategory = CurrentCategories.First(); return; }
            var index = CurrentCategories.IndexOf(SelectedCategory);
            if (index < CurrentCategories.Count - 1) SelectedCategory = CurrentCategories[index + 1];
            else SelectedCategory = CurrentCategories.First();
            
            UpdateThemeForNode(SelectedCategory);
            // NEU: Vorschau für Kategorie starten
            PlayCategoryPreview(SelectedCategory);
        }
    }

    // ENTER
    [RelayCommand]
    private void PlayCurrent()
    {
        if (_isLaunching) return; 

        // FALL A: Wir sind in der Ordner-Ansicht
        if (!IsGameListActive)
        {
            if (SelectedCategory == null) return;

            var node = SelectedCategory;
            
            // Logik: Hat der Node Kinder (Unterordner)? -> Dann tauchen wir tiefer (Drill Down)
            if (node.Children != null && node.Children.Count > 0)
            {
                // Merken wo wir waren
                _navigationStack.Push(_currentCategories);
                _titleStack.Push(CategoryTitle);

                // Neue Ansicht setzen
                CategoryTitle = node.Name;
                CurrentCategories = node.Children;
                
                // Auswahl resetten auf erstes Element
                SelectedCategory = CurrentCategories.FirstOrDefault();
                UpdateThemeForNode(SelectedCategory);
            }
            // Hat er KEINE Kinder, aber ITEMS (Spiele)? -> Dann wechseln wir zur Spiele-Ansicht
            else if (node.Items != null && node.Items.Count > 0)
            {
                // Hier merken wir uns NICHTS im Stack für Categories, weil wir nur den Modus wechseln
                // Aber wir merken uns, dass wir aus einer Ordner-Ebene kommen
                
                CurrentNode = node;
                Items = node.Items;
                IsGameListActive = true; // Umschalten auf Spiele-Liste
                
                if (Items.Count > 0)
                {
                    SelectedItem = Items[0];
                    PlayPreview(SelectedItem);
                }
            }
            // Leerer Ordner?
            else
            {
                // Nichts tun oder User Feedback ("Leer")
            }
            return;
        }

        // FALL B: Wir sind in der Spiele-Ansicht -> Spiel starten
        _isLaunching = true;
        StopVideo(); 
        _previewCts?.Cancel(); 

        if (SelectedItem != null)
        {
            RequestPlay?.Invoke(SelectedItem);
        }
        
        Task.Delay(5000).ContinueWith(_ => _isLaunching = false);
    }

    private void PlayCategoryPreview(MediaNode? node)
    {
        if (MediaPlayer == null || _isLaunching || node == null) return;
        
        // Stoppe aktuelles Video (egal ob Spiel oder andere Kategorie)
        MediaPlayer.Stop();

        // 1. Video Pfad ermitteln
        // Annahme: Dein MediaNode hat ähnliche Asset-Methoden wie MediaItem, 
        // oder du speicherst Pfade in Properties wie "VideoPath".
        // Falls du das Asset-System nutzt: node.GetAssetPath(AssetType.Video)
        
        string? videoToPlay = null;
        
        // BEISPIEL: Wir prüfen, ob der Node eine Methode/Property für das Video hat.
        // Falls nicht vorhanden, musst du das in MediaNode ergänzen oder hier improvisieren.
        // Ich nehme an, du hast sowas wie 'node.VideoPath' oder nutzt das AssetSystem.
        
        // Da ich MediaNode Code nicht sehe, rate ich mal basierend auf MediaItem:
        // var relativeVideoPath = node.GetAssetPath(AssetType.Video); 
        
        // Fallback: Wenn du noch keine Videos an Nodes hast, lassen wir es leer.
        // Wenn du sie hast, füge hier die Logik ein:
        
        // if (!string.IsNullOrEmpty(relativeVideoPath)) 
        //    videoToPlay = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeVideoPath);

        // Falls wir ein Video haben, spielen wir es ab
        if (!string.IsNullOrEmpty(videoToPlay) && System.IO.File.Exists(videoToPlay))
        {
            // Kleiner Delay, damit man beim schnellen Scrollen nicht Wahnsinnig wird
            // (Ähnlich wie bei OnSelectedItemChanged)
            
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;

            Task.Run(async () => 
            {
                try { await Task.Delay(400, token); } catch { return; }
                if (token.IsCancellationRequested) return;

                await Dispatcher.UIThread.InvokeAsync(() => 
                {
                    if (!token.IsCancellationRequested && !_isLaunching && SelectedCategory == node)
                    {
                        using var media = new Media(_libVlc, new Uri(videoToPlay));
                        MediaPlayer.Play(media);
                    }
                });
            });
        }
    }
    
    // ESC / BACK
    [RelayCommand]
    private void ExitBigMode()
    {
        // 1. Wenn wir in der Spiele-Liste sind -> Zurück zur Ordner-Ansicht dieses Knotens
        if (IsGameListActive)
        {
            IsGameListActive = false;
            StopVideo();
            return;
        }

        // 2. Wenn wir in Unterordnern sind -> Zurück zum Eltern-Ordner (Stack Pop)
        if (_navigationStack.Count > 0)
        {
            var previousList = _navigationStack.Pop();
            var previousTitle = _titleStack.Count > 0 ? _titleStack.Pop() : "Main Menu";

            CategoryTitle = previousTitle;
            CurrentCategories = previousList;
            
            // Auswahl auf das erste Element setzen (oder besser: das was vorher gewählt war merken?)
            SelectedCategory = CurrentCategories.FirstOrDefault();
            UpdateThemeForNode(SelectedCategory);
            return;
        }

        // 3. Wir sind ganz oben -> App schließen
        RequestClose?.Invoke();
    }
    
    // --- HELPER ---

    private void UpdateThemeForNode(MediaNode? node)
    {
        if (node == null) return;
        
        // Hintergrund für UI aktualisieren (wir nutzen das Property CurrentNode auch für das Bild im Hintergrund)
        CurrentNode = node; 
        
        // Theme Datei laden logic
        string themesBaseDir = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Themes");
        string themeToLoad = System.IO.Path.Combine(themesBaseDir, "Default.axaml");

        if (!string.IsNullOrEmpty(node.ThemePath))
        {
             // ... (Pfad Logik wie vorher) ...
             if (System.IO.File.Exists(node.ThemePath)) themeToLoad = node.ThemePath;
             else {
                 var c = System.IO.Path.Combine(themesBaseDir, node.ThemePath);
                 if(System.IO.File.Exists(c)) themeToLoad = c;
             }
        }
        else 
        {
            var safeName = string.Join("_", node.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
            var magic = System.IO.Path.Combine(themesBaseDir, safeName, "theme.axaml");
            if (System.IO.File.Exists(magic)) themeToLoad = magic;
        }

        if (!string.Equals(_currentThemePath, themeToLoad, StringComparison.OrdinalIgnoreCase))
        {
            _currentThemePath = themeToLoad;
            CurrentThemeDirectory = System.IO.Path.GetDirectoryName(themeToLoad) ?? string.Empty;
            RequestThemeChange?.Invoke(themeToLoad);
        }
    }

    // ... PlayPreview, OnSelectedItemChanged, StopVideo, Dispose bleiben gleich ...
    
    // Kopiere hier deine PlayPreview, OnSelectedItemChanged, StopVideo und Dispose Methoden hin.
    // Achte darauf, dass OnSelectedItemChanged StopVideo() aufruft.
    
    partial void OnSelectedItemChanged(MediaItem? value)
    {
        if (_isLaunching) return;
        if (!IsGameListActive) { StopVideo(); return; }

        StopVideo();
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        Task.Run(async () => 
        {
            try { await Task.Delay(400, token); } catch (TaskCanceledException) { return; }
            if (token.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                if (!token.IsCancellationRequested && !_isLaunching && SelectedItem == value)
                {
                    PlayPreview(value);
                }
            });
        });
    }

    private void PlayPreview(MediaItem? item)
    {
        if (MediaPlayer == null || _isLaunching) return;
        MediaPlayer.Stop();

        if (item != null)
        {
            string? videoToPlay = null;
            var relativeVideoPath = item.GetPrimaryAssetPath(AssetType.Video);
            if (!string.IsNullOrEmpty(relativeVideoPath))
                videoToPlay = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, relativeVideoPath);

            if (!System.IO.File.Exists(videoToPlay) && !string.IsNullOrEmpty(item.FilePath))
            {
                 // Auto-Discovery fallback...
                 try {
                    var d = System.IO.Path.GetDirectoryName(item.FilePath);
                    var n = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                    if(d!=null) {
                        var p = System.IO.Path.Combine(d, n + ".mp4");
                        if(System.IO.File.Exists(p)) videoToPlay = p;
                    }
                 } catch {}
            }

            if (!string.IsNullOrEmpty(videoToPlay) && System.IO.File.Exists(videoToPlay))
            {
                using var media = new Media(_libVlc, new Uri(videoToPlay));
                MediaPlayer.Play(media);
            }
        }
    }
    
    private void StopVideo()
    {
        if (MediaPlayer != null && MediaPlayer.IsPlaying) MediaPlayer.Stop();
    }

    public void Dispose()
    {
        var player = MediaPlayer;
        var vlc = _libVlc;
        MediaPlayer = null; 
        Task.Run(() => { try { player?.Stop(); player?.Dispose(); vlc?.Dispose(); } catch {} });
    }
}