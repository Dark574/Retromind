using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private static readonly TimeSpan PreviewDebounceDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan VideoFadeOutDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan VideoStartSettleDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromMilliseconds(750);
    
    // Cache limits (soft caps) to avoid unbounded growth in very large libraries.
    private const int MaxItemVideoCacheEntries = 10_000;
    private const int MaxNodeVideoCacheEntries = 2_000;

    // Prevent old "play after render" tasks from starting late.
    private int _previewPlayGeneration;

    // IMPORTANT: Keep Media alive while VLC is using it, otherwise playback can freeze.
    private Media? _currentPreviewMedia;

    // Prevent old fade tasks from hiding the overlay after a new playback started.
    private int _overlayFadeGeneration;

    private CancellationTokenSource? _previewDebounceCts;

    /// <summary>
    /// Must be called by the host once the theme view is fully loaded/layouted.
    /// If LibVLC starts playback too early, it may create its own output window (platform-dependent).
    /// </summary>
    public void NotifyViewReady()
    {
        // The first time, we note: View is ready.
        var wasReady = _isViewReady;
        _isViewReady = true;

        // In any case: Try starting (or restarting) the preview.
        // This way we catch timing issues where the first call is lost.
        TriggerPreviewPlaybackWithDebounce();

        // Optional: an additional, slightly delayed repetition,
        // to ensure that layout/slot bounds are stable.
        if (!wasReady)
        {
            UiThreadHelper.Post(
                TriggerPreviewPlaybackWithDebounce,
                DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Must be called before swapping the theme view, otherwise LibVLC may detach and spawn its own output window.
    /// </summary>
    public async Task PrepareForThemeSwapAsync()
    {
        _isViewReady = false;

        // Cancel any pending debounce/play tasks.
        _previewDebounceCts?.Cancel();
        _previewDebounceCts = null;

        StopVideo();

        // StopVideo() may not be fully "settled" in LibVLC immediately.
        // Wait defensively for a short time until playback is confirmed stopped.
        var mp = MediaPlayer;
        if (mp != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < StopWaitTimeout)
            {
                if (!mp.IsPlaying)
                    break;

                await Task.Delay(25).ConfigureAwait(false);
            }
        }

        // One render tick to let Avalonia process detach/layout before the root view swap.
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);
    }

    partial void OnThemeContextNodeChanged(MediaNode? value)
    {
        _previewDebounceCts?.Cancel();
        _previewDebounceCts = null;

        TriggerPreviewPlaybackWithDebounce();
    }

    partial void OnSelectedItemChanged(MediaItem? value)
    {
        if (value == null)
        {
            SelectedItemIndex = -1;
        }
        else
        {
            // O(n) only for external selection changes (mouse/touch).
            var idx = Items.IndexOf(value);
            if (idx >= 0) SelectedItemIndex = idx;
        }

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
            // O(n) only for external selection changes (mouse/touch).
            _selectedCategoryIndex = CurrentCategories.IndexOf(value);
        }

        TriggerPreviewPlaybackWithDebounce();
    }

    partial void OnIsGameListActiveChanged(bool value)
    {
        _previewDebounceCts?.Cancel();
        _previewDebounceCts = null;

        StopVideo();
        TriggerPreviewPlaybackWithDebounce();
    }

    /// <summary>
    /// Debounced entry point. Safe to call frequently (scrolling / key repeat).
    /// Schedules the work on the UI thread without allocating extra thread-pool tasks.
    /// </summary>
    private void TriggerPreviewPlaybackWithDebounce()
    {
        if (!_isViewReady || _isLaunching)
            return;

        // Theme capability: if video is not allowed, ensure preview is stopped.
        if (!CanShowVideo)
        {
            StopVideo();
            return;
        }

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = new CancellationTokenSource();
        var token = _previewDebounceCts.Token;

        // Debounce OFF the UI thread, then marshal the actual work back to the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PreviewDebounceDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            UiThreadHelper.Post(TriggerPreviewPlayback, DispatcherPriority.Background);
        }, token);
    }

    /// <summary>
    /// Starts preview playback for the current selection. Must run on the UI thread.
    /// </summary>
    private void TriggerPreviewPlayback()
    {
        if (IsGameListActive)
        {
            var videoPath = ResolveItemVideoPath(SelectedItem);
            PlayPreviewForPath(videoPath);
        }
        else
        {
            var videoPath = ResolveNodeVideoPath(SelectedCategory);
            PlayPreviewForPath(videoPath);
        }
    }

    private string? ResolveItemVideoPath(MediaItem? item)
    {
        if (item == null) return null;

        if (_itemVideoPathCache.TryGetValue(item.Id, out var cached))
            return cached;

        if (_itemVideoPathCache.Count > MaxItemVideoCacheEntries)
            _itemVideoPathCache.Clear();
        
        string? videoPath = null;

        var relativeVideoPath = item.GetPrimaryAssetPath(AssetType.Video);
        if (!string.IsNullOrEmpty(relativeVideoPath))
        {
            var candidate = AppPaths.ResolveDataPath(relativeVideoPath);
            if (File.Exists(candidate))
                videoPath = candidate;
        }

        // Fallback: "<romname>.mp4" next to the primary launch file (only if it is a real local file path).
        if (videoPath == null)
        {
            var primary = item.GetPrimaryLaunchPath();

            if (!string.IsNullOrWhiteSpace(primary) &&
                Path.IsPathRooted(primary) &&
                File.Exists(primary))
            {
                try
                {
                    var dir = Path.GetDirectoryName(primary);
                    var name = Path.GetFileNameWithoutExtension(primary);
                    if (dir != null)
                    {
                        var candidate = Path.Combine(dir, name + ".mp4");
                        if (File.Exists(candidate))
                            videoPath = candidate;
                    }
                }
                catch
                {
                    // best-effort only
                }
            }
        }

        _itemVideoPathCache[item.Id] = videoPath;
        return videoPath;
    }

    private string? ResolveNodeVideoPath(MediaNode? node)
    {
        if (node == null) return null;

        if (_nodeVideoPathCache.TryGetValue(node.Id, out var cached))
            return cached;

        if (_nodeVideoPathCache.Count > MaxNodeVideoCacheEntries)
            _nodeVideoPathCache.Clear();
        
        string? videoPath = null;

        var videoAsset = node.Assets.FirstOrDefault(a => a.Type == AssetType.Video);
        if (!string.IsNullOrEmpty(videoAsset?.RelativePath))
        {
            var candidate = AppPaths.ResolveDataPath(videoAsset.RelativePath);
            if (File.Exists(candidate))
                videoPath = candidate;
        }

        _nodeVideoPathCache[node.Id] = videoPath;
        return videoPath;
    }

    private void PlayPreviewForPath(string? videoPath)
    {
        if (!CanShowVideo || !_isViewReady || _isLaunching || MediaPlayer == null)
        {
            StopVideo();
            return;
        }

        if (string.IsNullOrEmpty(videoPath))
        {
            StopVideo();
            return;
        }

        if (string.Equals(_currentPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase) && MediaPlayer.IsPlaying)
            return;

        _currentPreviewVideoPath = videoPath;

        // Ensure the overlay exists before fading in.
        IsVideoOverlayVisible = true;
        IsVideoVisible = false;

        var gen = Interlocked.Increment(ref _previewPlayGeneration);
        _ = StartPlaybackAfterRenderAsync(videoPath, gen);

        // Fade in after the next render tick to avoid one-frame flashes.
        UiThreadHelper.Post(() =>
        {
            if (gen == Volatile.Read(ref _previewPlayGeneration) && IsVideoOverlayVisible)
                IsVideoVisible = true;
        }, DispatcherPriority.Render);
    }

    private async Task StartPlaybackAfterRenderAsync(string videoPath, int generation)
    {
        await UiThreadHelper.InvokeAsync(static () => { }, DispatcherPriority.Render);

        await Task.Delay(VideoStartSettleDelay).ConfigureAwait(false);

        if (generation != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (!_isViewReady || _isLaunching || MediaPlayer == null)
            return;

        if (!string.Equals(_currentPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            _currentPreviewMedia?.Dispose();
            _currentPreviewMedia = null;

            var media = new Media(_libVlc, new Uri(videoPath));
            _currentPreviewMedia = media;

            MediaPlayer.Media = media;
            MediaPlayer.Play();
        }
        catch
        {
            // Best-effort: preview must never crash the UI.
        }
    }

    private void StopVideo()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(StopVideo, DispatcherPriority.Background);
            return;
        }

        Interlocked.Increment(ref _previewPlayGeneration);

        _previewDebounceCts?.Cancel();
        _previewDebounceCts = null;

        IsVideoVisible = false;

        var fadeGen = Interlocked.Increment(ref _overlayFadeGeneration);
        _ = HideOverlayAfterFadeAsync(fadeGen);

        try
        {
            if (MediaPlayer != null)
            {
                if (MediaPlayer.IsPlaying)
                    MediaPlayer.Stop();

                MediaPlayer.Media = null;
            }
        }
        catch
        {
            // best-effort only
        }

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
            await Task.Delay(VideoFadeOutDelay).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (generation != Volatile.Read(ref _overlayFadeGeneration))
            return;

        if (IsVideoVisible)
            return;

        await UiThreadHelper.InvokeAsync(() =>
        {
            if (!IsVideoVisible)
                IsVideoOverlayVisible = false;
        }, DispatcherPriority.Background);
    }
}