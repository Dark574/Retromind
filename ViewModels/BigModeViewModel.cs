using System;
using System.Collections.ObjectModel;
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
    
    // Flag to prevent inputs while launching
    private bool _isLaunching;
    
    // Das ist das Objekt, an das die View bindet!
    [ObservableProperty]
    private MediaPlayer? _mediaPlayer;
    
    // Collection of media items to display in the theme
    [ObservableProperty]
    private ObservableCollection<MediaItem> _items;

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
    
    // Token um alte Video-Start-Versuche abzubrechen
    private CancellationTokenSource? _previewCts;

    public BigModeViewModel(ObservableCollection<MediaItem> items, string title)
    {
        Items = items;
        CategoryTitle = title;
        
        // X11 erzwingen (da App jetzt im X11 Modus läuft)
        string[] vlcOptions = { "--no-xlib", "--vout=x11" };

        _libVlc = new LibVLC(enableDebugLogs: true, vlcOptions); 
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.Volume = 100;
    }

    // Wird aufgerufen, wenn sich SelectedItem ändert (dank ObservableProperty Magic)
    partial void OnSelectedItemChanged(MediaItem? value)
    {
        // Wenn wir gerade ein Spiel starten, ignorieren wir jegliche Auswahländerung für die Vorschau
        if (_isLaunching) return;
        
        // 1. Laufendes Video sofort stoppen (fühlt sich schneller an)
        // Aber Vorsicht: Zu viele Stops können auch blockieren. 
        // Wir lassen es hier mal weg und stoppen erst, wenn das neue wirklich startet.
            
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
                if (!token.IsCancellationRequested && !_isLaunching)
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