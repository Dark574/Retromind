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
    private static readonly string[] VideoExtensionOrder =
    {
        ".mp4",
        ".mkv",
        ".avi",
        ".mov",
        ".wmv",
        ".webm",
        ".m4v",
        ".mpg",
        ".mpeg"
    };

    private static readonly System.Collections.Generic.HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",
            ".mkv",
            ".avi",
            ".mov",
            ".wmv",
            ".webm",
            ".m4v",
            ".mpg",
            ".mpeg"
        };

    // --- Timing knobs ---
    private static readonly TimeSpan PreviewDebounceSmall = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PreviewDebounceMedium = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PreviewDebounceLarge = TimeSpan.FromMilliseconds(275);
    private static readonly TimeSpan PreviewDebounceHuge = TimeSpan.FromMilliseconds(350);

    private static readonly TimeSpan VideoFadeOutDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan VideoStartSettleDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromMilliseconds(750);
    
    // Cache limits (soft caps) to avoid unbounded growth in very large libraries.
    private const int MaxItemVideoCacheEntries = 10_000;
    private const int MaxNodeVideoCacheEntries = 2_000;

    // --- Preview state ---
    private DispatcherTimer? _previewDebounceTimer;
    
    // One-time estimate of overall library size to tune debounce for huge collections.
    private int? _estimatedTotalItems;
    
    // Prevent old "play after render" tasks from starting late.
    private int _previewPlayGeneration;
    
    // Prevent old fade tasks from hiding the overlay after a new playback started.
    private int _overlayFadeGeneration;

    // Avoid duplicate restarts while a start is already pending for the same video.
    private string? _pendingPreviewVideoPath;
    private int _pendingPreviewGeneration;

    // IMPORTANT: Keep Media alive while VLC is using it, otherwise playback can freeze.
    private Media? _currentPreviewMedia;

    // Tracks the expected frame generation so we can hide stale frames until the first new frame arrives.
    private int _expectedPreviewFrameGeneration;
    private int _mainVideoFrameReadyGeneration;

    private bool _suspendPreviewDuringScroll;
    
    /// <summary>
    /// Forces the main MediaPlayer instance to be recreated on the next playback start.
    /// This is used as a defensive workaround for platform-specific LibVLC state glitches
    /// (e.g. when running inside an AppImage bundle).
    /// </summary>
    private bool _forceRecreateMediaPlayerNextTime;

    // ----------------------------
    // Entry points (host API)
    // ----------------------------
    
    /// <summary>
    /// Must be called by the host once the theme view is fully loaded/layouted.
    /// If LibVLC starts playback too early, it may create its own output window (platform-dependent).
    /// </summary>
    public void NotifyViewReady()
    {
        // The first time, we note: View is ready.
        var wasReady = _isViewReady;
        _isViewReady = true;

        // In any case: Try starting (or restarting) the preview
        // This way we catch timing issues where the first call is lost
        TriggerPreviewPlaybackWithDebounce();

        // play background video eventually
        EnsureSecondaryBackgroundPlayingIfReady();
        
        // Optional: an additional, slightly delayed repetition,
        // to ensure that layout/slot bounds are stable.
        if (!wasReady)
        {
            UiThreadHelper.Post(
                () =>
                {
                    TriggerPreviewPlaybackWithDebounce();
                    EnsureSecondaryBackgroundPlayingIfReady();
                },
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
        CancelPreviewDebounce();
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

    // ----------------------------
    // Event handlers / callbacks
    // ----------------------------
    
    private void OnPreviewEndReached(object? sender, EventArgs e)
    {
        // Called on a VLC thread; marshal back to UI thread.
        if (string.IsNullOrEmpty(_currentPreviewVideoPath))
            return;

        UiThreadHelper.Post(() =>
        {
            if (!CanShowVideo || !_isViewReady || _isLaunching || MediaPlayer == null)
                return;

            // Replay the current preview video from the beginning.
            try
            {
                MediaPlayer.Stop();
                MediaPlayer.Play();
            }
            catch
            {
                // best-effort only
            }

            // Defensive: ensure the background channel keeps running.
            EnsureSecondaryBackgroundPlayingIfReady();
        }, DispatcherPriority.Background);
    }

    private void OnMainVideoFrameReady()
    {
        var expected = Volatile.Read(ref _expectedPreviewFrameGeneration);
        if (expected == 0)
            return;

        if (expected != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (Interlocked.CompareExchange(ref _mainVideoFrameReadyGeneration, expected, 0) != 0)
            return;

        UiThreadHelper.Post(() =>
        {
            if (expected != Volatile.Read(ref _previewPlayGeneration))
                return;

            MainVideoHasFrame = true;
        }, DispatcherPriority.Background);
    }

    partial void OnThemeContextNodeChanged(MediaNode? value)
    {
        CancelPreviewDebounce();
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

        // Selection changed -> update counters before triggering preview logic.
        UpdateGameCounters();
        
        // Notify dependent computed properties used by themes.
        OnPropertyChanged(nameof(SelectedYear));
        OnPropertyChanged(nameof(SelectedDeveloper));
        
        TriggerPreviewPlaybackWithDebounce();
    }

    partial void OnSelectedCategoryChanged(MediaNode? value)
    {
        if (value == null)
        {
            SelectedCategoryIndex = -1;
        }
        else
        {
            // O(n) only for external selection changes (mouse/touch).
            SelectedCategoryIndex = CurrentCategories.IndexOf(value);
        }

        TriggerPreviewPlaybackWithDebounce();
    }
    
    partial void OnIsGameListActiveChanged(bool value)
    {
        CancelPreviewDebounce();

        StopVideo();
        
        // View mode changed (categories vs. games) -> counters may need to reset.
        UpdateGameCounters();
        
        TriggerPreviewPlaybackWithDebounce();
    }
    
    // ----------------------------
    // Debounce / scheduling
    // ----------------------------
    
    /// <summary>
    /// Debounced entry point. Safe to call frequently (scrolling / key repeat).
    /// Schedules the work on the UI thread without allocating extra thread-pool tasks.
    /// </summary>
    private void TriggerPreviewPlaybackWithDebounce()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(TriggerPreviewPlaybackWithDebounce, DispatcherPriority.Background);
            return;
        }

        if (!_isViewReady || _isLaunching)
            return;

        if (_suspendPreviewDuringScroll)
        {
            StopVideo();
            return;
        }

        // Theme capability: if video is not allowed, ensure preview is stopped.
        if (!CanShowVideo)
        {
            StopVideo();
            return;
        }

        EnsurePreviewDebounceTimer();

        var delay = GetAdaptivePreviewDebounceDelay();

        // One-shot timer: reset interval and enable
        _previewDebounceTimer!.Interval = delay;
        _previewDebounceTimer.IsEnabled = true;
    }
    
    private void EnsurePreviewDebounceTimer()
    {
        if (_previewDebounceTimer != null)
            return;

        _previewDebounceTimer = new DispatcherTimer
        {
            IsEnabled = false
        };

        _previewDebounceTimer.Tick += OnPreviewDebounceTimerTick;
    }

    private void OnPreviewDebounceTimerTick(object? sender, EventArgs e)
    {
        // One-shot timer behavior
        if (_previewDebounceTimer != null)
            _previewDebounceTimer.IsEnabled = false;

        TriggerPreviewPlayback();
    }

    private void CancelPreviewDebounce()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(CancelPreviewDebounce, DispatcherPriority.Background);
            return;
        }

        if (_previewDebounceTimer != null)
            _previewDebounceTimer.IsEnabled = false;
    }

    private void DisposePreviewDebounceTimer()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(DisposePreviewDebounceTimer, DispatcherPriority.Background);
            return;
        }

        if (_previewDebounceTimer != null)
        {
            _previewDebounceTimer.Tick -= OnPreviewDebounceTimerTick;
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.IsEnabled = false;
            _previewDebounceTimer = null;
        }
    }

    private TimeSpan GetAdaptivePreviewDebounceDelay()
    {
        // Primary heuristic: current visible list size (this is what the user is scrolling).
        var currentListCount = IsGameListActive ? Items.Count : CurrentCategories.Count;

        // Secondary heuristic: overall library size (one-time estimated; avoids too short debounce on huge collections).
        _estimatedTotalItems ??= EstimateTotalItemCountSoftCap(_rootNodes, softCap: 100_000);

        // Use the larger signal.
        var size = Math.Max(currentListCount, _estimatedTotalItems.Value);

        return size switch
        {
            <= 2_000 => PreviewDebounceSmall,
            <= 10_000 => PreviewDebounceMedium,
            <= 30_000 => PreviewDebounceLarge,
            _ => PreviewDebounceHuge
        };
    }

    private static int EstimateTotalItemCountSoftCap(System.Collections.Generic.IEnumerable<MediaNode> nodes, int softCap)
    {
        var total = 0;

        void Walk(System.Collections.Generic.IEnumerable<MediaNode> level)
        {
            foreach (var n in level)
            {
                if (n.Items is { Count: > 0 })
                {
                    total += n.Items.Count;
                    if (total >= softCap)
                        return;
                }

                if (n.Children is { Count: > 0 })
                {
                    Walk(n.Children);
                    if (total >= softCap)
                        return;
                }
            }
        }

        Walk(nodes);
        return Math.Min(total, softCap);
    }

    // ----------------------------
    // Playback
    // ----------------------------

    /// <summary>
    /// Ensures that the main MediaPlayer instance is in a clean, usable state.
    /// If a previous Stop/Dispose cycle left the player in an undefined state,
    /// this recreates it and reattaches the video callbacks.
    /// </summary>
    private MediaPlayer EnsureMediaPlayer(bool forceRecreate = false)
    {
        // Some LibVLC builds (especially when bundled in portable runtimes / AppImages)
        // can end up in a broken audio state when the same MediaPlayer is reused
        // across many rapid Stop/Play cycles. In that case we allow the caller to
        // request a hard recreate of the player.
        if (forceRecreate && MediaPlayer is not null)
        {
            try
            {
                if (MediaPlayer.IsPlaying)
                    MediaPlayer.Stop();

                MediaPlayer.Media = null;
                MediaPlayer.Dispose();
            }
            catch
            {
                // Best-effort cleanup; fallback is simply to drop the reference.
            }

            MediaPlayer = null;
        }
        
        // If the current player is null or already disposed (LibVLC 3.x can get
        // into odd states after Stop/Media = null), create a fresh one.
        if (MediaPlayer is null)
        {
            var newPlayer = new MediaPlayer(_libVlc)
            {
                Volume = 100,
                Scale = 0f // 0 = scale to fill the control
            };

            newPlayer.SetVideoFormatCallbacks(
                _videoSurface.VideoFormat,
                _videoSurface.VideoCleanup);

            newPlayer.SetVideoCallbacks(
                _videoSurface.VideoLock,
                _videoSurface.VideoUnlock,
                _videoSurface.VideoDisplay);

            newPlayer.EndReached += OnPreviewEndReached;

            MediaPlayer = newPlayer;
        }
        else
        {
            // Defensive: ensure we always start from a known volume and unmuted state.
            if (MediaPlayer.Volume <= 0)
                MediaPlayer.Volume = 100;

            MediaPlayer.Mute = false;
        }

        return MediaPlayer;
    }

    /// <summary>
    /// Starts preview playback for the current selection. Must run on the UI thread.
    /// </summary>
    private void TriggerPreviewPlayback()
    {
        string? videoPath = IsGameListActive
            ? ResolveItemVideoPath(SelectedItem)
            : ResolveNodeVideoPath(SelectedCategory);
        
        // Main channel: Derive content flag from path and mode
        MainVideoHasContent = CanShowVideo && !string.IsNullOrEmpty(videoPath);

        PlayPreviewForPath(videoPath);
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

        // Fallback: use the launch file if it is already a video; otherwise try
        // a same-name video next to the launch file (local file path only).
        if (videoPath == null)
        {
            var primary = item.GetPrimaryLaunchPath();

            if (!string.IsNullOrWhiteSpace(primary) &&
                Path.IsPathRooted(primary) &&
                File.Exists(primary))
            {
                try
                {
                    var primaryExt = Path.GetExtension(primary);
                    if (!string.IsNullOrEmpty(primaryExt) && VideoExtensions.Contains(primaryExt))
                    {
                        videoPath = primary;
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(primary);
                        var name = Path.GetFileNameWithoutExtension(primary);
                        if (dir != null)
                        {
                            foreach (var ext in VideoExtensionOrder)
                            {
                                var candidate = Path.Combine(dir, name + ext);
                                if (File.Exists(candidate))
                                {
                                    videoPath = candidate;
                                    break;
                                }
                            }
                        }
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
            // No video available for the current selection. We mark the current
            // player instance as "suspect" so that the next playback start will
            // recreate the MediaPlayer from scratch. This is a defensive
            // workaround for platform-specific LibVLC issues in bundled runtimes
            // (e.g. AppImage), where reusing the same player across "no-media"
            // transitions can sometimes leave the audio output in a bad state
            _forceRecreateMediaPlayerNextTime = true;
            
            StopVideo();
            return;
        }

        if (string.Equals(_currentPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
        {
            if (MediaPlayer.IsPlaying ||
                string.Equals(_pendingPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _currentPreviewVideoPath = videoPath;

        // Ensure the overlay exists before fading in.
        IsVideoOverlayVisible = true;
        IsVideoVisible = false;

        var gen = Interlocked.Increment(ref _previewPlayGeneration);
        MainVideoHasFrame = false;
        Volatile.Write(ref _mainVideoFrameReadyGeneration, 0);
        Volatile.Write(ref _expectedPreviewFrameGeneration, gen);
        _pendingPreviewVideoPath = videoPath;
        Volatile.Write(ref _pendingPreviewGeneration, gen);
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
        {
            ClearPendingPreviewStart(generation);
            return;
        }

        if (!_isViewReady || _isLaunching)
        {
            ClearPendingPreviewStart(generation);
            return;
        }

        if (!string.Equals(_currentPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingPreviewStart(generation);
            return;
        }

        try
        {
            // (Re)create MediaPlayer if needed and ensure a clean state.
            var forceRecreate = _forceRecreateMediaPlayerNextTime;
            _forceRecreateMediaPlayerNextTime = false;

            var player = EnsureMediaPlayer(forceRecreate);
            
            _currentPreviewMedia?.Dispose();
            _currentPreviewMedia = null;

            var media = new Media(_libVlc, new Uri(videoPath));
            
            _currentPreviewMedia = media;

            player.Media = media;
            player.Mute = false;
            if (player.Volume <= 0)
                player.Volume = 100;

            player.Play();

            MainVideoIsPlaying = true;

            // Defensive: keep the background channel alive after preview changes.
            EnsureSecondaryBackgroundPlayingIfReady();
        }
        catch
        {
            // Best-effort: preview must never crash the UI.
            MainVideoIsPlaying = false;
        }
        finally
        {
            ClearPendingPreviewStart(generation);
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

        CancelPreviewDebounce();

        ClearPendingPreviewStart();
        IsVideoVisible = false;

        // Main channel: Reset status
        MainVideoIsPlaying = false;
        MainVideoHasContent = false;
        MainVideoHasFrame = false;
        Volatile.Write(ref _expectedPreviewFrameGeneration, 0);
        Volatile.Write(ref _mainVideoFrameReadyGeneration, 0);
        
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

        // Main stop should not impact the background channel.
        EnsureSecondaryBackgroundPlayingIfReady();
    }

    private void ClearPendingPreviewStart(int generation)
    {
        if (generation != Volatile.Read(ref _pendingPreviewGeneration))
            return;

        _pendingPreviewVideoPath = null;
    }

    private void ClearPendingPreviewStart()
    {
        _pendingPreviewVideoPath = null;
        Volatile.Write(ref _pendingPreviewGeneration, 0);
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
