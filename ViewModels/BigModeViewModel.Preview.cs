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
    /// <summary>
    /// Called by the host once the theme view is fully loaded/layouted.
    /// Prevents LibVLC from creating external output windows during startup.
    /// </summary>
    public void NotifyViewReady()
    {
        if (_isViewReady) return;
        _isViewReady = true;

        if (IsGameListActive)
            PlayPreview(SelectedItem);
        else
            PlayCategoryPreview(SelectedCategory);
    }

    partial void OnSelectedItemChanged(MediaItem? value)
    {
        if (!_isViewReady) return;
        if (_isLaunching) return;
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
            try { await Task.Delay(150, token); }
            catch (TaskCanceledException) { return; }

            if (token.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && !_isLaunching && SelectedItem == value)
                    PlayPreview(value);
            });
        });
    }

    private void PlayPreview(MediaItem? item)
    {
        if (!_isViewReady) return;
        if (MediaPlayer == null || _isLaunching) return;

        IsVideoVisible = false;
        MediaPlayer.Stop();

        if (item == null) return;

        string? videoToPlay = null;

        var relativeVideoPath = item.GetPrimaryAssetPath(AssetType.Video);
        if (!string.IsNullOrEmpty(relativeVideoPath))
            videoToPlay = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeVideoPath);

        // Auto-discovery fallback (kept as-is; can be optimized later)
        if (!File.Exists(videoToPlay) && !string.IsNullOrEmpty(item.FilePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(item.FilePath);
                var name = Path.GetFileNameWithoutExtension(item.FilePath);
                if (dir != null)
                {
                    var candidate = Path.Combine(dir, name + ".mp4");
                    if (File.Exists(candidate)) videoToPlay = candidate;
                }
            }
            catch
            {
                // ignore discovery errors
            }
        }

        if (!string.IsNullOrEmpty(videoToPlay) && File.Exists(videoToPlay))
        {
            IsVideoVisible = true;
            using var media = new Media(_libVlc, new Uri(videoToPlay));
            MediaPlayer.Play(media);
        }
    }

    private void PlayCategoryPreview(MediaNode? node)
    {
        if (!_isViewReady) return;
        if (MediaPlayer == null || _isLaunching) return;

        IsVideoVisible = false;
        MediaPlayer.Stop();

        if (node == null) return;

        string? videoToPlay = null;

        var videoAsset = node.Assets.FirstOrDefault(a => a.Type == AssetType.Video);
        if (videoAsset != null && !string.IsNullOrEmpty(videoAsset.RelativePath))
            videoToPlay = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, videoAsset.RelativePath);

        if (!string.IsNullOrEmpty(videoToPlay) && File.Exists(videoToPlay))
        {
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(150, token); }
                catch { return; }

                if (token.IsCancellationRequested) return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested && !_isLaunching && SelectedCategory == node)
                    {
                        IsVideoVisible = true;
                        using var media = new Media(_libVlc, new Uri(videoToPlay));
                        MediaPlayer.Play(media);
                    }
                });
            });
        }
    }

    private void StopVideo()
    {
        IsVideoVisible = false;
        if (MediaPlayer != null && MediaPlayer.IsPlaying)
            MediaPlayer.Stop();
    }
}