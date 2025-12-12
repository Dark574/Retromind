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

        if (IsGameListActive)
        {
            PlayPreview(SelectedItem);
        }
        else
        {
            PlayCategoryPreview(SelectedCategory);
        }
    }

    partial void OnSelectedItemChanged(MediaItem? value)
    {
        if (value == null)
        {
            _selectedItemIndex = -1;
        }
        else
        {
            // O(n) only if selection changes externally; controller navigation will keep this in sync.
            var idx = Items.IndexOf(value);
            if (idx >= 0) _selectedItemIndex = idx;
        }
        
        if (!_isViewReady) return;
        if (_isLaunching) return;

        // Only show previews for game selection changes.
        if (!IsGameListActive)
        {
            StopVideo();
            return;
        }

        StopVideo();

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PreviewDebounceMs, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

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

    partial void OnSelectedCategoryChanged(MediaNode? value)
    {
        // Keep index in sync when selection changes through UI binding or restore logic.
        if (value == null)
        {
            _selectedCategoryIndex = -1;
            return;
        }

        // This is O(n), but it only runs when the selection changes externally.
        // Navigation via controller will be O(1).
        _selectedCategoryIndex = CurrentCategories.IndexOf(value);
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
        if (!_isViewReady) return;
        if (_isLaunching) return;
        if (MediaPlayer == null) return;

        if (item == null)
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

        // If the same preview is already active, do nothing (prevents VLC flicker).
        if (string.Equals(_currentPreviewVideoPath, videoToPlay, StringComparison.OrdinalIgnoreCase) && MediaPlayer.IsPlaying)
        {
            IsVideoVisible = true;
            return;
        }
        
        _currentPreviewVideoPath = videoToPlay;
        
        IsVideoVisible = true;
        using var media = new Media(_libVlc, new Uri(videoToPlay));
        MediaPlayer.Play(media);
    }

    private void PlayCategoryPreview(MediaNode? node)
    {
        if (!_isViewReady) return;
        if (_isLaunching) return;
        if (MediaPlayer == null) return;

        if (node == null)
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

        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PreviewDebounceMs, token);
            }
            catch
            {
                return;
            }

            if (token.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || _isLaunching || SelectedCategory != node) return;

                // If the same preview is already active, do nothing (prevents VLC flicker).
                if (string.Equals(_currentPreviewVideoPath, videoToPlay, StringComparison.OrdinalIgnoreCase) && MediaPlayer.IsPlaying)
                {
                    IsVideoVisible = true;
                    return;
                }

                _currentPreviewVideoPath = videoToPlay;

                IsVideoVisible = false;
                MediaPlayer.Stop();

                IsVideoVisible = true;
                using var media = new Media(_libVlc, new Uri(videoToPlay));
                MediaPlayer.Play(media);
            });
        });
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