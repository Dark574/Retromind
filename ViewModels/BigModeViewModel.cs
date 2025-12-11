using System;
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
    // VLC Objekte
    private readonly LibVLC _libVlc;
    
    // Navigation State
    private readonly ObservableCollection<MediaNode> _rootNodes;
    private int _currentCategoryIndex;
    
    // Flag to prevent inputs while launching
    private bool _isLaunching;
    
    // Merken, welches Theme gerade aktiv ist, um unnötiges Neuladen zu verhindern
    private string _currentThemePath = string.Empty;
    
    // Zugriff für das Theme auf den aktuellen Node (z.B. für Titel, Node-spezifische Assets)
    [ObservableProperty]
    private MediaNode? _currentNode;

    // Zugriff für das Theme auf sein eigenes Verzeichnis (für lokale Assets wie background.png)
    [ObservableProperty]
    private string _currentThemeDirectory = string.Empty;
    
    // Das ist das Objekt, an das die View bindet!
    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;
    
    // Collection of media items to display in the theme
    [ObservableProperty]
    private ObservableCollection<MediaItem> _items = new();

    // The currently selected item (for navigation and details)
    [ObservableProperty]
    private MediaItem? _selectedItem;
    
    // Title of the current context (e.g., "SNES" or "Favorites")
    [ObservableProperty]
    private string _categoryTitle = "Library";

    /// <summary>
    /// Event triggered when the Big Mode should be closed (returning to desktop mode).
    /// </summary>
    public event Action? RequestClose;
    
    /// <summary>
    /// Event triggered when the user wants to launch a game.
    /// The main view model will handle the actual launching logic.
    /// </summary>
    public event Action<MediaItem>? RequestPlay;
    
    // Signalisiert dem Hauptfenster: Bitte lade dieses .axaml File!
    public event Action<string>? RequestThemeChange;
    
    // Token um alte Video-Start-Versuche abzubrechen
    private CancellationTokenSource? _previewCts;

    public BigModeViewModel(ObservableCollection<MediaNode> rootNodes, MediaNode? startNode = null)
    {
        _rootNodes = rootNodes;
        
        // X11 erzwingen (da App jetzt im X11 Modus läuft)
        string[] vlcOptions = { "--no-xlib", "--vout=x11" };
        _libVlc = new LibVLC(enableDebugLogs: true, vlcOptions); 
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Volume = 100;
        
        // Start-Kategorie setzen
        if (startNode != null && _rootNodes.Contains(startNode))
        {
            _currentCategoryIndex = _rootNodes.IndexOf(startNode);
            SwitchToCategory(startNode);
        }
        else if (_rootNodes.Count > 0)
        {
            _currentCategoryIndex = 0;
            SwitchToCategory(_rootNodes[0]);
        }
    }

    // Neue Navigation-Commands
    [RelayCommand]
    private void NextCategory()
    {
        if (_isLaunching || _rootNodes.Count <= 1) return;

        _currentCategoryIndex++;
        if (_currentCategoryIndex >= _rootNodes.Count) _currentCategoryIndex = 0; // Wrap around

        SwitchToCategory(_rootNodes[_currentCategoryIndex]);
    }

    [RelayCommand]
    private void PreviousCategory()
    {
        if (_isLaunching || _rootNodes.Count <= 1) return;

        _currentCategoryIndex--;
        if (_currentCategoryIndex < 0) _currentCategoryIndex = _rootNodes.Count - 1; // Wrap around

        SwitchToCategory(_rootNodes[_currentCategoryIndex]);
    }

    private void SwitchToCategory(MediaNode node)
    {
        CurrentNode = node;
        CategoryTitle = node.Name;
            
        // Items laden (alle Items aus diesem Node und evtl. Unterordnern?)
        // Fürs erste nehmen wir nur die direkten Items, oder man müsste rekursiv sammeln.
        // Wenn deine Nodes flach sind (nur Areas), reicht Items.
        // Wenn du "Alle Spiele" unter einer Area willst, brauchst du eine Helper-Methode.
            
        // Simpelste Variante:
        Items = node.Items; 

        // Video stoppen beim Kategoriewechsel
        StopVideo();

        // Erstes Item selektieren
        if (Items.Count > 0) SelectedItem = Items[0];
        else SelectedItem = null;
        
        // --- THEME AUTO-DISCOVERY ---
            
        string themesBaseDir = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Themes");
        string themeToLoad = System.IO.Path.Combine(themesBaseDir, "Default.axaml"); // Fallback

        // 1. Manuelles Override am Node?
        if (!string.IsNullOrEmpty(node.ThemePath))
        {
            // Check ob absoluter Pfad oder relativ zu Themes
            if (System.IO.File.Exists(node.ThemePath)) 
                themeToLoad = node.ThemePath;
            else
            {
                var combined = System.IO.Path.Combine(themesBaseDir, node.ThemePath);
                if (System.IO.File.Exists(combined)) themeToLoad = combined;
            }
        }
        // 2. Convention: Gibt es einen Ordner mit dem Namen des Nodes?
        else 
        {
            // Sicherstellen, dass der Name valide für Ordner ist
            var safeName = string.Join("_", node.Name.Split(System.IO.Path.GetInvalidFileNameChars()));
            var magicThemePath = System.IO.Path.Combine(themesBaseDir, safeName, "theme.axaml");
                
            if (System.IO.File.Exists(magicThemePath))
            {
                themeToLoad = magicThemePath;
            }
        }

        // --- THEME LOAD ---
            
        // Nur laden, wenn sich die Datei wirklich geändert hat!
        if (!string.Equals(_currentThemePath, themeToLoad, StringComparison.OrdinalIgnoreCase))
        {
            _currentThemePath = themeToLoad;
                
            // Wir setzen das Verzeichnis, damit das Theme seine Bilder findet
            CurrentThemeDirectory = System.IO.Path.GetDirectoryName(themeToLoad) ?? string.Empty;
                
            // Feuern!
            RequestThemeChange?.Invoke(themeToLoad);
        }
    }
    
    // Wird aufgerufen, wenn sich SelectedItem ändert (dank ObservableProperty Magic)
    partial void OnSelectedItemChanged(MediaItem? value)
    {
        // Wenn wir gerade ein Spiel starten, ignorieren wir jegliche Auswahländerung für die Vorschau
        if (_isLaunching) return;
        
        // 1. Laufendes Video SOFORT stoppen beim Navigieren!
        // Das verhindert, dass der Ton vom vorherigen Spiel noch läuft, während man schon weiter ist.
        StopVideo();
            
        // 2. Alten Start-Vorgang abbrechen
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        // 3. Verzögert starten (Debounce)
        Task.Run(async () => 
        {
            // Warte 400ms. Wenn der User in der Zeit weiter scrollt, wird dieser Task abgebrochen.
            try 
            { 
                await Task.Delay(400, token); 
            } 
            catch (TaskCanceledException) 
            { 
                return; 
            }

            if (token.IsCancellationRequested) return;

            // Zurück auf den UI Thread um VLC anzusprechen
            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                // WICHTIG: Prüfen ob wir immer noch auf dem gleichen Item sind!
                // Wenn der User sehr schnell scrollt, könnte das Event hier ankommen, obwohl SelectedItem schon woanders ist.
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

            // Nutzung des Asset-Systems statt starrer Property
            var relativeVideoPath = item.GetPrimaryAssetPath(AssetType.Video);

            if (!string.IsNullOrEmpty(relativeVideoPath))
            {
                // Pfad auflösen (relativ zu absolut)
                videoToPlay = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, relativeVideoPath);
            }

            // 2. Priorität: Auto-Discovery (Falls kein Video gesetzt ist)
            // Wir schauen, ob eine .mp4 Datei neben der Spiel-Datei liegt
            if (!System.IO.File.Exists(videoToPlay) && !string.IsNullOrEmpty(item.FilePath))
            {
                try 
                {
                    var gameDir = System.IO.Path.GetDirectoryName(item.FilePath);
                    var gameName = System.IO.Path.GetFileNameWithoutExtension(item.FilePath);
                        
                    if (gameDir != null)
                    {
                        var potentialVideo = System.IO.Path.Combine(gameDir, gameName + ".mp4");
                        if (System.IO.File.Exists(potentialVideo))
                        {
                            videoToPlay = potentialVideo;
                        }
                    }
                }
                catch { /* Ignorieren bei Pfad-Fehlern */ }
            }

            // 3. Abspielen wenn gefunden
            if (!string.IsNullOrEmpty(videoToPlay) && System.IO.File.Exists(videoToPlay))
            {
                using var media = new Media(_libVlc, new Uri(videoToPlay));
                MediaPlayer.Play(media);
            }
        }
    }
    
    /// <summary>
    /// Command to exit the Big Mode.
    /// </summary>
    [RelayCommand]
    private void ExitBigMode()
    {
        RequestClose?.Invoke();
    }
    
    /// <summary>
    /// Command to launch the currently selected media.
    /// </summary>
    [RelayCommand]
    private void PlayCurrent()
    {
        if (_isLaunching) return; // Doppelte Starts verhindern
        _isLaunching = true;
        
        StopVideo(); // Video Stop beim Spielstart
        
        // Ab jetzt auch Vorschau-Starts blockieren (passiert durch _isLaunching = true oben)
        _previewCts?.Cancel(); // Laufende Delays abbrechen

        if (SelectedItem == null) 
        {
            _isLaunching = false;
            return;
        }

        // Logic to launch the game/movie will go here.
        // For now, we just log or placeholders.
        System.Diagnostics.Debug.WriteLine($"Launching: {SelectedItem.Title}");
        
        // Wir delegieren das Starten nach "oben"
        RequestPlay?.Invoke(SelectedItem);
        
        // Hinweis: Wir setzen _isLaunching hier NICHT auf false zurück.
        // Das ViewModel wird vermutlich eh bald zerstört oder wir verlassen den Screen.
        // Falls wir zurückkommen, wird das ViewModel neu erstellt oder wir müssten es resetten.
        // Da BigModeViewModel oft neu erstellt wird beim Öffnen, ist das okay.
        // Falls es langlebig ist, müsste der Aufrufer Bescheid geben, wenn das Spiel beendet ist.
            
        // Fallback für den Fall, dass der Start fehlschlägt oder sehr schnell geht:
        // Nach 5 Sekunden wieder freigeben, falls die UI noch offen ist.
        Task.Delay(10000).ContinueWith(_ => _isLaunching = false);
    }
    
    // Navigation helper commands (useful for controller mapping later)
    [RelayCommand]
    private void SelectNext()
    {
        if (_isLaunching) return; // Block input during launch
        
        if (Items.Count == 0 || SelectedItem == null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index < Items.Count - 1)
            SelectedItem = Items[index + 1];
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (_isLaunching) return; // Block input during launch
        
        if (Items.Count == 0 || SelectedItem == null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index > 0)
            SelectedItem = Items[index - 1];
    }
    
    private void StopVideo()
    {
        // Thread-sicherer Stop
        if (MediaPlayer != null && MediaPlayer.IsPlaying)
        {
            MediaPlayer.Stop();
        }
    }

    public void Dispose()
    {
        // Aufräumen im Hintergrund, damit die UI nicht blockiert
        // Wir kopieren die Referenzen, um sie im Task zu nutzen
        var player = MediaPlayer;
        var vlc = _libVlc;

        MediaPlayer = null; // UI-Bindung sofort lösen
            
        Task.Run(() => 
        {
            try 
            {
                player?.Stop();
                player?.Dispose();
                vlc?.Dispose();
            }
            catch 
            { 
                // Ignore dispose errors
            }
        });
    }
}