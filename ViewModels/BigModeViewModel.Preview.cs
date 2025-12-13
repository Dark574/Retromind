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

    partial void OnSelectedItemChanged(MediaItem? value)
    {
        // Dieser Handler aktualisiert nur noch den Index für externe Änderungen.
        // Er löst KEINE Wiedergabe mehr aus. Das verhindert doppelte Aufrufe.
        if (value == null)
        {
            _selectedItemIndex = -1;
        }
        else
        {
            // O(n) nur für Mausklicks etc.
            var idx = Items.IndexOf(value);
            if (idx >= 0) _selectedItemIndex = idx;
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
        // Die Logik hier ist jetzt sicher, da sie immer vom UI-Thread aufgerufen wird.
        if (_isLaunching || MediaPlayer == null || item == null)
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

        if (string.Equals(_currentPreviewVideoPath, videoToPlay, StringComparison.OrdinalIgnoreCase) && MediaPlayer.IsPlaying) return;

        _currentPreviewVideoPath = videoToPlay;
        IsVideoVisible = true;
        using var media = new Media(_libVlc, new Uri(videoToPlay));
        MediaPlayer.Play(media);
    }

    private void PlayCategoryPreview(MediaNode? node)
    {
        // Diese Methode ist jetzt ebenfalls sicher.
        if (_isLaunching || MediaPlayer == null || node == null)
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
        IsVideoVisible = true;
        using var media = new Media(_libVlc, new Uri(videoToPlay));
        MediaPlayer.Play(media);
    }

    private void StopVideo()
    {
        IsVideoVisible = false;
        
        // Cancel any pending delayed preview starts
        _previewCts?.Cancel();
        _previewCts = null;

        // Reset current preview marker so the next Play() is not skipped.
        _currentPreviewVideoPath = null;

        // LibVLC sometimes keeps the last frame even if IsPlaying is already false.
        // Therefore, always attempt a Stop().
        try
        {
            MediaPlayer?.Stop();
        }
        catch
        {
            // Best-effort: never crash on shutdown/stop.
        }
    }
}