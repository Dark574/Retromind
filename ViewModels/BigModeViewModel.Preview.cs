using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private const int PreviewDebounceMs = 150;
    
    // verhindert, dass alte "Play nach Render"-Tasks später doch noch loslaufen
    private int _previewPlayGeneration;

    // IMPORTANT: keep Media alive while VLC is playing it, otherwise you'll get freezes/standbilder
    private Media? _currentPreviewMedia;
    
    // verhindert, dass ein alter Fade-Task später das Overlay „wegschaltet“, obwohl wieder Video läuft
    private int _overlayFadeGeneration;
    
    private const int VideoFadeOutMs = 250;
    
    /// <summary>
    /// Must be called by the host once the theme view is fully loaded/layouted.
    /// If LibVLC starts playback too early, it may create its own output window (platform-dependent).
    /// </summary>
    public void NotifyViewReady()
    {
        if (_isViewReady) return;
        _isViewReady = true;

        // Nach dem Ready-Signal die Wiedergabe für die aktuelle Auswahl anstoßen
        TriggerPreviewPlayback();
    }

    /// <summary>
    /// Muss vor einem Theme/View-Swap aufgerufen werden, damit LibVLC nicht in ein externes Fenster fällt,
    /// während die alte VideoView aus dem VisualTree entfernt wird.
    /// </summary>
    public async Task PrepareForThemeSwapAsync()
    {
        _isViewReady = false;

        // Kill pending debounce/play tasks
        _previewCts?.Cancel();
        _previewCts = null;

        // Stop + invalidate delayed play tasks
        StopVideo();

        // WICHTIG: StopVideo() ist nicht zwingend "sofort fertig" auf LibVLC-Seite.
        // Wir warten defensiv kurz, bis VLC wirklich nicht mehr spielt.
        var mp = MediaPlayer;
        if (mp != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 750)
            {
                if (!(mp.IsPlaying))
                    break;

                await Task.Delay(25);
            }
        }

        // Zusätzlich 1 Render-Tick, damit Avalonia das "Detach/Dispose" sauber durcharbeitet,
        // bevor wir die Root-View austauschen.
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }
    
    // Theme-Kontext-Wechsel bedeutet: Die Host-View wird gleich neu gebaut.
    // Deshalb darf VLC bis zum nächsten NotifyViewReady() NICHT starten.
    partial void OnThemeContextNodeChanged(MediaNode? value)
    {
        // laufende / geplante Preview sofort killen
        _previewCts?.Cancel();
        _previewCts = null;

        // Preview für die aktuelle Auswahl neu anstoßen
        TriggerPreviewPlaybackWithDebounce();
    }
    
    partial void OnSelectedItemChanged(MediaItem? value)
    {
        // Dieser Handler aktualisiert nur noch den Index für externe Änderungen.
        // Er löst KEINE Wiedergabe mehr aus. Das verhindert doppelte Aufrufe.
        if (value == null)
        {
            SelectedItemIndex = -1;
        }
        else
        {
            // O(n) nur für Mausklicks etc.
            var idx = Items.IndexOf(value);
            if (idx >= 0) SelectedItemIndex = idx;
        }
        
        // Wiedergabe wird durch den Aufrufer (z.B. SelectNext/Previous) gesteuert
        // oder durch den Debounce-Mechanismus für externe Änderungen.
        TriggerPreviewPlaybackWithDebounce();
    }

    partial void OnSelectedCategoryChanged(MediaNode? value)
    {
        if (value == null)
        {
            _selectedCategoryIndex = -1;
        }
        else
        {
            // This is O(n), but it only runs when the selection changes externally.
            // Navigation via controller will be O(1).
            _selectedCategoryIndex = CurrentCategories.IndexOf(value);
        }
        
        TriggerPreviewPlaybackWithDebounce();
    }
    
    /// <summary>
    /// Kapselt die Debounce-Logik. Kann von überall sicher aufgerufen werden.
    /// </summary>
    private void TriggerPreviewPlaybackWithDebounce()
    {
        if (!_isViewReady || _isLaunching) return;

        // Theme hat keinen VideoSlot -> Video sicher aus
        if (!CanShowVideo)
        {
            StopVideo();
            return;
        }
        
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(PreviewDebounceMs, token); }
            catch { return; }

            if (token.IsCancellationRequested) return;

            // Die eigentliche Wiedergabe wird sicher über den Dispatcher aufgerufen
            await Dispatcher.UIThread.InvokeAsync(TriggerPreviewPlayback, DispatcherPriority.Background);
        });
    }
    
    /// <summary>
    /// Löst die Wiedergabe für die aktuelle Auswahl aus. Muss auf dem UI-Thread aufgerufen werden.
    /// </summary>
    private void TriggerPreviewPlayback()
    {
        if (IsGameListActive)
        {
            PlayPreview(SelectedItem);
        }
        else
        {
            PlayCategoryPreview(SelectedCategory);
        }
    }
    
    private string? ResolveItemVideoPath(MediaItem item)
    {
        if (_itemVideoPathCache.TryGetValue(item.Id, out var cached))
            return cached;

        string? videoPath = null;

        var relativeVideoPath = item.GetPrimaryAssetPath(AssetType.Video);
        if (!string.IsNullOrEmpty(relativeVideoPath))
        {
            var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeVideoPath);
            if (File.Exists(candidate))
                videoPath = candidate;
        }

        // Fallback: "<romname>.mp4" next to the ROM file
        if (videoPath == null && !string.IsNullOrEmpty(item.FilePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(item.FilePath);
                var name = Path.GetFileNameWithoutExtension(item.FilePath);
                if (dir != null)
                {
                    var candidate = Path.Combine(dir, name + ".mp4");
                    if (File.Exists(candidate))
                        videoPath = candidate;
                }
            }
            catch
            {
                // Best-effort only
            }
        }

        _itemVideoPathCache[item.Id] = videoPath;
        return videoPath;
    }

    private string? ResolveNodeVideoPath(MediaNode node)
    {
        if (_nodeVideoPathCache.TryGetValue(node.Id, out var cached))
            return cached;

        string? videoPath = null;

        var videoAsset = node.Assets.FirstOrDefault(a => a.Type == AssetType.Video);
        if (videoAsset != null && !string.IsNullOrEmpty(videoAsset.RelativePath))
        {
            var candidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, videoAsset.RelativePath);
            if (File.Exists(candidate))
                videoPath = candidate;
        }

        _nodeVideoPathCache[node.Id] = videoPath;
        return videoPath;
    }
    
    private void PlayPreview(MediaItem? item)
    {
        if (!CanShowVideo)
        {
            StopVideo();
            return;
        }
        
        // Die Logik hier ist jetzt sicher, da sie immer vom UI-Thread aufgerufen wird.
        if (!_isViewReady || _isLaunching || MediaPlayer == null || item == null)
        {
            StopVideo();
            return;
        }

        var videoToPlay = ResolveItemVideoPath(item);
        if (string.IsNullOrEmpty(videoToPlay))
        {
            StopVideo();
            return;
        }

        if (string.Equals(_currentPreviewVideoPath, videoToPlay, StringComparison.OrdinalIgnoreCase) && MediaPlayer.IsPlaying)
            return;

        _currentPreviewVideoPath = videoToPlay;
        
        // Overlay muss existieren, bevor wir sichtbar werden (für sauberes Fade-In)
        IsVideoOverlayVisible = true;
        
        // Start at 0 opacity, then fade in on next render tick (prevents 1-frame flashes)
        IsVideoVisible = false;

        var gen = Interlocked.Increment(ref _previewPlayGeneration);
        _ = StartPlaybackAfterRenderAsync(videoToPlay, gen);

        // Trigger the fade-in asynchronously (UI thread)
        Dispatcher.UIThread.Post(() =>
        {
            // Only fade in if this playback is still the current one
            if (gen == Volatile.Read(ref _previewPlayGeneration) && IsVideoOverlayVisible)
                IsVideoVisible = true;
        }, DispatcherPriority.Render);
    }

    private void PlayCategoryPreview(MediaNode? node)
    {
        if (!CanShowVideo)
        {
            StopVideo();
            return;
        }
        
        // Diese Methode ist jetzt ebenfalls sicher.
        if (!_isViewReady || _isLaunching || MediaPlayer == null || node == null)
        {
            StopVideo();
            return;
        }
        
        var videoToPlay = ResolveNodeVideoPath(node);
        if (string.IsNullOrEmpty(videoToPlay))
        {
            StopVideo();
            return;
        }

        if (string.Equals(_currentPreviewVideoPath, videoToPlay, StringComparison.OrdinalIgnoreCase) && MediaPlayer.IsPlaying) return;

        _currentPreviewVideoPath = videoToPlay;
        
        // Overlay muss existieren, bevor wir sichtbar werden (für sauberes Fade-In)
        IsVideoOverlayVisible = true;
        IsVideoVisible = false;

        var gen = Interlocked.Increment(ref _previewPlayGeneration);
        _ = StartPlaybackAfterRenderAsync(videoToPlay, gen);

        Dispatcher.UIThread.Post(() =>
        {
            if (gen == Volatile.Read(ref _previewPlayGeneration) && IsVideoOverlayVisible)
                IsVideoVisible = true;
        }, DispatcherPriority.Render);
    }

    /// <summary>
    /// If the view mode changes (categories <-> games), we reset preview deterministically.
    /// This avoids "old media in new mode" edge cases.
    /// </summary>
    partial void OnIsGameListActiveChanged(bool value)
    {
        // Any pending debounced plays are no longer relevant
        _previewCts?.Cancel();
        _previewCts = null;

        StopVideo();

        // If the view is ready, start the correct preview for the new mode
        TriggerPreviewPlaybackWithDebounce();
    }
    
    private async Task StartPlaybackAfterRenderAsync(string videoPath, int generation)
    {
        // 1) Ein Render-Tick, damit VideoView sicher im Visual Tree hängt.
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);

        // 2) Extra Delay: Wayland/XWayland + VLC Embedding braucht oft "settle time"
        await Task.Delay(75);
        
        // 3) Falls inzwischen was anderes selektiert wurde: abbrechen
        if (generation != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (!_isViewReady || _isLaunching || MediaPlayer == null)
            return;

        // Wenn inzwischen "kein Video" aktiv ist, abbrechen
        if (!string.Equals(_currentPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            // Dispose previous media deterministically
            _currentPreviewMedia?.Dispose();
            _currentPreviewMedia = null;

            var media = new Media(_libVlc, new Uri(videoPath));
            _currentPreviewMedia = media;

            // Prefer setting MediaPlayer.Media so VLC holds a stable reference
            MediaPlayer.Media = media;
            MediaPlayer.Play();
        }
        catch
        {
            // Best-effort: Preview darf nie crashen
        }
    }
    
    private void StopVideo()
    {
        // Invalidate pending "play after render" tasks
        Interlocked.Increment(ref _previewPlayGeneration);

        // Cancel pending debounces first so we don't restart immediately
        _previewCts?.Cancel();
        _previewCts = null;

        // 1) Fade out (Opacity -> 0)
        IsVideoVisible = false;

        // 2) Schedule hard hide after fade-out
        var fadeGen = Interlocked.Increment(ref _overlayFadeGeneration);
        _ = HideOverlayAfterFadeAsync(fadeGen);

        try
        {
            if (MediaPlayer != null)
            {
                if (MediaPlayer.IsPlaying)
                    MediaPlayer.Stop();

                // Clear VLC-side media reference
                MediaPlayer.Media = null;
            }
        }
        catch
        {
            // Best-effort only
        }

        // Now dispose the managed Media instance we created
        try
        {
            _currentPreviewMedia?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _currentPreviewMedia = null;
        }

        _currentPreviewVideoPath = null;
    }
    
    private async Task HideOverlayAfterFadeAsync(int generation)
    {
        try
        {
            await Task.Delay(VideoFadeOutMs);
        }
        catch
        {
            return;
        }

        // Wenn inzwischen wieder Video läuft oder ein neuer Fade gestartet wurde -> nicht verstecken
        if (generation != Volatile.Read(ref _overlayFadeGeneration))
            return;

        if (IsVideoVisible)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Doppelt absichern: wirklich nur ausblenden, wenn weiterhin unsichtbar
            if (!IsVideoVisible)
                IsVideoOverlayVisible = false;
        }, DispatcherPriority.Background);
    }
}