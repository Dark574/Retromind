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
                if (!token.IsCancellationRequested)
                {
                    PlayPreview(value);
                }
            });
        });
    }

    private void PlayPreview(MediaItem? item)
    {
        if (MediaPlayer == null) return;
        MediaPlayer.Stop();

        if (item != null)
        {
            string? videoToPlay = null;

            // 1. Priorität: Explizit gesetzter Pfad im Item
            if (!string.IsNullOrEmpty(item.VideoPath))
            {
                // Prüfen ob relativer Pfad (im App-Ordner) oder absoluter Pfad
                videoToPlay = System.IO.Path.IsPathRooted(item.VideoPath)
                    ? item.VideoPath
                    : System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, item.VideoPath);
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
        StopVideo(); // Video Stop beim Spielstart
        if (SelectedItem == null) return;

        // Logic to launch the game/movie will go here.
        // For now, we just log or placeholders.
        System.Diagnostics.Debug.WriteLine($"Launching: {SelectedItem.Title}");
        
        // Wir delegieren das Starten nach "oben"
        RequestPlay?.Invoke(SelectedItem);
    }
    
    // Navigation helper commands (useful for controller mapping later)
    [RelayCommand]
    private void SelectNext()
    {
        if (Items.Count == 0 || SelectedItem == null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index < Items.Count - 1)
            SelectedItem = Items[index + 1];
    }

    [RelayCommand]
    private void SelectPrevious()
    {
        if (Items.Count == 0 || SelectedItem == null) return;
        var index = Items.IndexOf(SelectedItem);
        if (index > 0)
            SelectedItem = Items[index - 1];
    }
    
    private void StopVideo()
    {
        MediaPlayer?.Stop();
    }

    public void Dispose()
    {
        MediaPlayer?.Dispose();
        _libVlc?.Dispose();
    }
}