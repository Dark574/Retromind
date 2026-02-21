using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Retromind.Helpers;
using Retromind.Helpers.Video;
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
    private static readonly TimeSpan VideoStartTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AudioStartFallbackDelay = TimeSpan.FromMilliseconds(100);
    private const int MaxAudioFadeDurationMs = 600;
    
    // AppImage-specific: some bundled LibVLC builds can lose audio after Stop/Play cycles.
    private static readonly bool IsAppImage =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE"));
    
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
    private int _pendingPreviewSurfaceIndex = -1;

    // IMPORTANT: Keep Media alive while VLC is using it, otherwise playback can freeze.
    private Media? _currentPreviewMediaA;
    private Media? _currentPreviewMediaB;
    
    // Delay disposal of Media until VLC confirms Stop to avoid native crashes.
    private readonly object _previewMediaLock = new();
    private readonly System.Collections.Generic.Dictionary<int, Media> _pendingDisposeMedia = new();

    // Tracks the expected frame generation so we can hide stale frames until the first new frame arrives.
    private int _expectedPreviewFrameGeneration;
    private int _expectedPreviewSurfaceIndex = -1;
    private int _mainVideoFrameReadyGeneration;
    private int _audioFadeGeneration;
    private int _audioCrossfadeStartedGeneration;

    private int _activePreviewIndex = -1;

    private bool _mainVideoHasFrameA;
    private bool _mainVideoHasFrameB;
    private bool _mainVideoIsPlayingA;
    private bool _mainVideoIsPlayingB;

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
        var mpA = _mediaPlayerA;
        var mpB = _mediaPlayerB;
        if (mpA != null || mpB != null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < StopWaitTimeout)
            {
                var aPlaying = mpA != null && mpA.IsPlaying;
                var bPlaying = mpB != null && mpB.IsPlaying;
                if (!aPlaying && !bPlaying)
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
    
    private void OnPreviewEndReached(int index)
    {
        // Called on a VLC thread; marshal back to UI thread.
        if (string.IsNullOrEmpty(_currentPreviewVideoPath))
            return;

        UiThreadHelper.Post(() =>
        {
            if (!CanShowVideo || !_isViewReady || _isLaunching)
                return;

            if (index != _activePreviewIndex)
                return;

            var player = GetMediaPlayer(index);
            if (player == null)
                return;

            // Replay the current preview video from the beginning.
            try
            {
                player.Stop();
                player.Play();
            }
            catch
            {
                // best-effort only
            }

            // Defensive: ensure the background channel keeps running.
            EnsureSecondaryBackgroundPlayingIfReady();
        }, DispatcherPriority.Background);
    }

    private void OnMainVideoFrameReadyA() => OnMainVideoFrameReady(0);

    private void OnMainVideoFrameReadyB() => OnMainVideoFrameReady(1);

    private void OnMainVideoFrameReady(int index)
    {
        if (index == 0)
            _mainVideoHasFrameA = true;
        else
            _mainVideoHasFrameB = true;

        var expected = Volatile.Read(ref _expectedPreviewFrameGeneration);
        if (expected == 0)
            return;

        if (expected != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (index != Volatile.Read(ref _expectedPreviewSurfaceIndex))
            return;

        if (Interlocked.CompareExchange(ref _mainVideoFrameReadyGeneration, expected, 0) != 0)
            return;

        UiThreadHelper.Post(() =>
        {
            if (expected != Volatile.Read(ref _previewPlayGeneration))
                return;

            UpdateMainVideoHasFrame();

            var oldIndex = _activePreviewIndex;
            _activePreviewIndex = index;
            MainVideoActiveSurfaceIndex = index;
            MediaPlayer = GetMediaPlayer(index);

            var videoFadeMs = Math.Max(0, VideoFadeDurationMs);
            var audioFadeMs = ResolveAudioFadeDurationMs(videoFadeMs);

            if (audioFadeMs == 0)
            {
                var newPlayer = GetMediaPlayer(index);
                if (newPlayer != null)
                    newPlayer.Volume = 100;

                Interlocked.CompareExchange(ref _audioCrossfadeStartedGeneration, expected, 0);
            }
            else
            {
                TryStartAudioCrossfadeOnce(oldIndex, index, expected, audioFadeMs);
            }

            if (videoFadeMs == 0)
            {
                StopPreviewPlayer(oldIndex);
                return;
            }

            _ = StopPreviewPlayerAfterDelayAsync(oldIndex, expected, videoFadeMs);
        }, DispatcherPriority.Background);
    }

    partial void OnThemeContextNodeChanged(MediaNode? value)
    {
        CancelPreviewDebounce();
        OnPropertyChanged(nameof(ActiveLogoPath));
        OnPropertyChanged(nameof(ActiveWallpaperPath));
        OnPropertyChanged(nameof(ActiveVideoPath));
        OnPropertyChanged(nameof(ActiveMarqueePath));
        OnPropertyChanged(nameof(ActiveBezelPath));
        OnPropertyChanged(nameof(ActiveControlPanelPath));
        if (IsGameListActive)
            ApplyNodeFallbackOverrides();
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

        // Stop preview only when the target video actually changes.
        var targetVideoPath = ResolvePreviewVideoPath();
        StopVideoIfPreviewPathChanged(targetVideoPath);

        // Selection changed -> update counters before triggering preview logic.
        UpdateGameCounters();
        UpdateCircularItems();
        
        // Notify dependent computed properties used by themes.
        OnPropertyChanged(nameof(SelectedYear));
        OnPropertyChanged(nameof(SelectedDeveloper));
        OnPropertyChanged(nameof(ActiveLogoPath));
        OnPropertyChanged(nameof(HasDisplayLogo));
        OnPropertyChanged(nameof(ActiveWallpaperPath));
        OnPropertyChanged(nameof(ActiveVideoPath));
        OnPropertyChanged(nameof(ActiveMarqueePath));
        OnPropertyChanged(nameof(ActiveBezelPath));
        OnPropertyChanged(nameof(ActiveControlPanelPath));
        
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

        // Root menu: keep theme context synced with the selected root node.
        if (!IsGameListActive && _navigationPath.Count == 0)
            ThemeContextNode = value;

        // Stop preview only when the target video actually changes.
        var targetVideoPath = ResolvePreviewVideoPath();
        StopVideoIfPreviewPathChanged(targetVideoPath);

        TriggerPreviewPlaybackWithDebounce();
    }
    
    partial void OnIsGameListActiveChanged(bool value)
    {
        CancelPreviewDebounce();

        StopVideo();
        
        // View mode changed (categories vs. games) -> counters may need to reset.
        UpdateGameCounters();
        UpdateCircularItems();

        OnPropertyChanged(nameof(ActiveLogoPath));
        OnPropertyChanged(nameof(HasDisplayLogo));
        OnPropertyChanged(nameof(ActiveWallpaperPath));
        OnPropertyChanged(nameof(ActiveVideoPath));
        OnPropertyChanged(nameof(ActiveMarqueePath));
        OnPropertyChanged(nameof(ActiveBezelPath));
        OnPropertyChanged(nameof(ActiveControlPanelPath));
        
        TriggerPreviewPlaybackWithDebounce();
    }

    private void StopVideoIfPreviewPathChanged(string? targetVideoPath)
    {
        if (string.Equals(_currentPreviewVideoPath, targetVideoPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrEmpty(targetVideoPath))
            StopVideo();
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

    private MediaPlayer? GetMediaPlayer(int index) =>
        index == 0 ? _mediaPlayerA : _mediaPlayerB;

    private void SetMediaPlayer(int index, MediaPlayer? player)
    {
        if (index == 0)
            _mediaPlayerA = player;
        else
            _mediaPlayerB = player;
    }

    private LibVlcVideoSurface GetSurface(int index) =>
        index == 0 ? _videoSurfaceA : _videoSurfaceB;

    private Media? GetPreviewMedia(int index) =>
        index == 0 ? _currentPreviewMediaA : _currentPreviewMediaB;

    private void SetPreviewMedia(int index, Media? media)
    {
        if (index == 0)
            _currentPreviewMediaA = media;
        else
            _currentPreviewMediaB = media;
    }

    private void DisposePreviewMedia(int index)
    {
        try
        {
            GetPreviewMedia(index)?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            SetPreviewMedia(index, null);
        }
    }

    private void MarkPreviewMediaForDispose(int index)
    {
        lock (_previewMediaLock)
        {
            var media = GetPreviewMedia(index);
            if (media == null)
                return;

            _pendingDisposeMedia[index] = media;
            SetPreviewMedia(index, null);
        }
    }

    private void ForceDisposePendingMedia(int index)
    {
        Media? media;
        lock (_previewMediaLock)
        {
            if (!_pendingDisposeMedia.TryGetValue(index, out media))
                return;

            _pendingDisposeMedia.Remove(index);
        }

        var player = GetMediaPlayer(index);
        if (player != null)
        {
            try
            {
                if (ReferenceEquals(player.Media, media))
                    player.Media = null;
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            media?.Dispose();
        }
        catch
        {
            // best-effort only
        }
    }

    private void DisposePendingMediaIfIdle(int index)
    {
        var player = GetMediaPlayer(index);
        if (player != null && player.IsPlaying)
            return;

        ForceDisposePendingMedia(index);
    }

    private void OnPreviewStopped(int index)
    {
        Media? media;
        lock (_previewMediaLock)
        {
            if (!_pendingDisposeMedia.TryGetValue(index, out media))
                return;

            _pendingDisposeMedia.Remove(index);
        }

        var player = GetMediaPlayer(index);
        if (player != null)
        {
            try
            {
                if (ReferenceEquals(player.Media, media))
                    player.Media = null;
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            media?.Dispose();
        }
        catch
        {
            // best-effort only
        }
    }

    private int GetInactivePreviewIndex()
    {
        if (_activePreviewIndex is 0 or 1)
            return _activePreviewIndex == 0 ? 1 : 0;

        return 0;
    }

    private void UpdateMainVideoHasFrame()
    {
        MainVideoHasFrame = _mainVideoHasFrameA || _mainVideoHasFrameB;
    }

    private void UpdateMainVideoIsPlaying()
    {
        MainVideoIsPlaying = _mainVideoIsPlayingA || _mainVideoIsPlayingB;
    }

    /// <summary>
    /// Ensures that the MediaPlayer for the given index is in a clean, usable state.
    /// </summary>
    private MediaPlayer EnsureMediaPlayerForIndex(int index, bool forceRecreate = false)
    {
        var player = GetMediaPlayer(index);

        if (forceRecreate && player is not null)
        {
            try
            {
                // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                player.Stop();
                player.Media = null;
                player.Dispose();
            }
            catch
            {
                // Best-effort cleanup; fallback is simply to drop the reference.
            }

            player = null;
            SetMediaPlayer(index, null);
        }

        if (player is null)
        {
            player = CreateMediaPlayerForSurface(GetSurface(index), index);
            SetMediaPlayer(index, player);
        }
        else
        {
            if (player.Volume <= 0)
                player.Volume = 100;

            player.Mute = false;
        }

        return player;
    }

    /// <summary>
    /// Starts preview playback for the current selection. Must run on the UI thread.
    /// </summary>
    private void TriggerPreviewPlayback()
    {
        var videoPath = ResolvePreviewVideoPath();
        
        // Main channel: Derive content flag from path and mode
        MainVideoHasContent = CanShowVideo && !string.IsNullOrEmpty(videoPath);

        PlayPreviewForPath(videoPath);
    }

    private string? ResolvePreviewVideoPath()
    {
        var node = ThemeContextNode ?? CurrentNode;

        return IsGameListActive
            ? ResolveItemVideoPath(SelectedItem, node)
            : ResolveNodeVideoPath(SelectedCategory);
    }

    private string? ResolveItemVideoPath(MediaItem? item, MediaNode? node)
    {
        if (item == null) return null;

        if (_itemVideoPathCache.TryGetValue(item.Id, out var cached))
            return cached;

        if (_itemVideoPathCache.Count > MaxItemVideoCacheEntries)
            _itemVideoPathCache.Clear();
        
        string? videoPath = null;
        var hasItemVideo = false;

        var relativeVideoPath = item.GetPrimaryAssetPath(AssetType.Video);
        if (!string.IsNullOrEmpty(relativeVideoPath))
        {
            var candidate = AppPaths.ResolveDataPath(relativeVideoPath);
            if (File.Exists(candidate))
            {
                videoPath = candidate;
                hasItemVideo = true;
            }
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
                                    hasItemVideo = true;
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

        if (videoPath == null && node != null && node.IsFallbackEnabled(AssetType.Video))
            videoPath = ResolveNodeVideoPath(node);

        if (hasItemVideo)
        {
            _itemVideoPathCache[item.Id] = videoPath;
        }
        else
        {
            _itemVideoPathCache.Remove(item.Id);
        }
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
        if (!CanShowVideo || !_isViewReady || _isLaunching)
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
            if ((_mainVideoIsPlayingA || _mainVideoIsPlayingB) ||
                string.Equals(_pendingPreviewVideoPath, videoPath, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _currentPreviewVideoPath = videoPath;

        // Ensure the overlay exists before fading in.
        IsVideoOverlayVisible = true;
        IsVideoVisible = false;

        var gen = Interlocked.Increment(ref _previewPlayGeneration);
        Volatile.Write(ref _audioCrossfadeStartedGeneration, 0);
        Volatile.Write(ref _mainVideoFrameReadyGeneration, 0);
        Volatile.Write(ref _expectedPreviewFrameGeneration, gen);

        var oldIndex = _activePreviewIndex;
        var targetIndex = GetInactivePreviewIndex();
        Volatile.Write(ref _expectedPreviewSurfaceIndex, targetIndex);

        _pendingPreviewVideoPath = videoPath;
        _pendingPreviewSurfaceIndex = targetIndex;
        Volatile.Write(ref _pendingPreviewGeneration, gen);

        _ = StartPlaybackAfterRenderAsync(videoPath, gen, targetIndex, oldIndex);
        _ = FallbackStopOldAfterTimeoutAsync(oldIndex, targetIndex, gen);

        // Fade in after the next render tick to avoid one-frame flashes.
        UiThreadHelper.Post(() =>
        {
            if (gen == Volatile.Read(ref _previewPlayGeneration) && IsVideoOverlayVisible)
                IsVideoVisible = true;
        }, DispatcherPriority.Render);
    }

    private async Task StartPlaybackAfterRenderAsync(string videoPath, int generation, int targetIndex, int oldIndex)
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

        if (targetIndex != Volatile.Read(ref _pendingPreviewSurfaceIndex))
        {
            ClearPendingPreviewStart(generation);
            return;
        }

        try
        {
            // (Re)create MediaPlayer if needed and ensure a clean state.
            var forceRecreate = _forceRecreateMediaPlayerNextTime;
            _forceRecreateMediaPlayerNextTime = false;

            var player = EnsureMediaPlayerForIndex(targetIndex, forceRecreate);

            // Ensure the target surface is idle before reusing it.
            var targetIsPlaying = targetIndex == 0 ? _mainVideoIsPlayingA : _mainVideoIsPlayingB;
            if (targetIsPlaying)
                StopPreviewPlayer(targetIndex);
            else
                MarkPreviewMediaForDispose(targetIndex);

            DisposePendingMediaIfIdle(targetIndex);

            var media = new Media(_libVlc, new Uri(videoPath));
            SetPreviewMedia(targetIndex, media);

            player.Media = media;
            player.Mute = false;
            player.Volume = 0;
            player.Play();

            // Start audio immediately on playback start to avoid long delays
            // when the first video frame takes time to arrive.
            var audioFadeMs = ResolveAudioFadeDurationMs(VideoFadeDurationMs);
            var fromPlayer = oldIndex is 0 or 1 ? GetMediaPlayer(oldIndex) : null;
            var hasFromPlaying = oldIndex == 0 ? _mainVideoIsPlayingA : oldIndex == 1 && _mainVideoIsPlayingB;

            if (audioFadeMs == 0 || fromPlayer == null || !hasFromPlaying)
            {
                player.Volume = 100;
                Interlocked.CompareExchange(ref _audioCrossfadeStartedGeneration, generation, 0);
            }
            else
            {
                TryStartAudioCrossfadeOnce(oldIndex, targetIndex, generation, audioFadeMs);
            }

            _ = StartAudioFallbackAfterDelayAsync(oldIndex, targetIndex, generation);

            if (targetIndex == 0)
                _mainVideoIsPlayingA = true;
            else
                _mainVideoIsPlayingB = true;

            UpdateMainVideoIsPlaying();

            // Defensive: keep the background channel alive after preview changes.
            EnsureSecondaryBackgroundPlayingIfReady();
        }
        catch
        {
            // Best-effort: preview must never crash the UI.
            if (targetIndex == 0)
                _mainVideoIsPlayingA = false;
            else
                _mainVideoIsPlayingB = false;

            UpdateMainVideoIsPlaying();
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
        MainVideoHasContent = false;
        Volatile.Write(ref _expectedPreviewFrameGeneration, 0);
        Volatile.Write(ref _expectedPreviewSurfaceIndex, -1);
        Volatile.Write(ref _mainVideoFrameReadyGeneration, 0);
        Volatile.Write(ref _audioCrossfadeStartedGeneration, 0);

        // AppImage workaround: force a fresh MediaPlayer on the next preview start
        // to avoid LibVLC losing audio after frequent Stop/Play cycles.
        if (IsAppImage)
            _forceRecreateMediaPlayerNextTime = true;
        
        var fadeGen = Interlocked.Increment(ref _overlayFadeGeneration);
        _ = HideOverlayAfterFadeAsync(fadeGen);

        MainVideoActiveSurfaceIndex = -1;
        _activePreviewIndex = -1;

        StopPreviewPlayer(0);
        StopPreviewPlayer(1);

        _currentPreviewVideoPath = null;

        // Main stop should not impact the background channel.
        EnsureSecondaryBackgroundPlayingIfReady();
    }

    private void ClearPendingPreviewStart(int generation)
    {
        if (generation != Volatile.Read(ref _pendingPreviewGeneration))
            return;

        _pendingPreviewVideoPath = null;
        _pendingPreviewSurfaceIndex = -1;
    }

    private void ClearPendingPreviewStart()
    {
        _pendingPreviewVideoPath = null;
        _pendingPreviewSurfaceIndex = -1;
        Volatile.Write(ref _pendingPreviewGeneration, 0);
    }

    private void StopPreviewPlayer(int index)
    {
        var player = GetMediaPlayer(index);
        if (player != null)
        {
            try
            {
                // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                player.Stop();
            }
            catch
            {
                // best-effort only
            }
        }

        MarkPreviewMediaForDispose(index);

        if (index == 0)
        {
            _mainVideoIsPlayingA = false;
            _mainVideoHasFrameA = false;
        }
        else
        {
            _mainVideoIsPlayingB = false;
            _mainVideoHasFrameB = false;
        }

        UpdateMainVideoIsPlaying();
        UpdateMainVideoHasFrame();

        if (index == _activePreviewIndex && !_mainVideoHasFrameA && !_mainVideoHasFrameB)
        {
            _activePreviewIndex = -1;
            MainVideoActiveSurfaceIndex = -1;
        }
    }

    private void StartAudioCrossfade(int fromIndex, int toIndex, int generation, int durationMs)
    {
        var fromPlayer = fromIndex is 0 or 1 ? GetMediaPlayer(fromIndex) : null;
        var toPlayer = toIndex is 0 or 1 ? GetMediaPlayer(toIndex) : null;

        if (toPlayer != null && toPlayer.Volume < 0)
            toPlayer.Volume = 0;

        var fadeGen = Interlocked.Increment(ref _audioFadeGeneration);
        _ = CrossfadeAudioAsync(fromPlayer, toPlayer, durationMs, fadeGen, generation);
    }

    private bool TryStartAudioCrossfadeOnce(int fromIndex, int toIndex, int generation, int durationMs)
    {
        if (Interlocked.CompareExchange(ref _audioCrossfadeStartedGeneration, generation, 0) != 0)
            return false;

        StartAudioCrossfade(fromIndex, toIndex, generation, durationMs);
        return true;
    }

    private async Task StartAudioFallbackAfterDelayAsync(int fromIndex, int toIndex, int generation)
    {
        if (!IsAppImage)
            return;

        var delayMs = (int)Math.Max(0, AudioStartFallbackDelay.TotalMilliseconds);
        if (delayMs == 0)
            return;

        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (generation != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (Volatile.Read(ref _mainVideoFrameReadyGeneration) == generation)
            return;

        var audioFadeMs = ResolveAudioFadeDurationMs(VideoFadeDurationMs);
        if (audioFadeMs == 0)
        {
            var player = GetMediaPlayer(toIndex);
            if (player != null)
                player.Volume = 100;

            Interlocked.CompareExchange(ref _audioCrossfadeStartedGeneration, generation, 0);
            return;
        }

        TryStartAudioCrossfadeOnce(fromIndex, toIndex, generation, audioFadeMs);
    }

    private static int ResolveAudioFadeDurationMs(int videoFadeMs)
    {
        // Audio fade has caused noticeable delays on some LibVLC builds.
        // Keep it immediate for consistent UX.
        return 0;
    }

    private async Task CrossfadeAudioAsync(MediaPlayer? fromPlayer, MediaPlayer? toPlayer, int durationMs, int fadeGen, int previewGen)
    {
        if (durationMs <= 0)
        {
            if (toPlayer != null)
                toPlayer.Volume = 100;
            if (fromPlayer != null)
                fromPlayer.Volume = 0;
            return;
        }

        var steps = Math.Clamp(durationMs / 30, 4, 60);
        var stepDelay = Math.Max(1, durationMs / steps);

        for (var i = 0; i <= steps; i++)
        {
            if (fadeGen != Volatile.Read(ref _audioFadeGeneration))
                return;

            if (previewGen != Volatile.Read(ref _previewPlayGeneration))
                return;

            var t = (double)i / steps;
            if (fromPlayer != null)
                fromPlayer.Volume = (int)Math.Round(100 * (1.0 - t));
            if (toPlayer != null)
                toPlayer.Volume = (int)Math.Round(100 * t);

            try
            {
                await Task.Delay(stepDelay).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }
    }

    private async Task StopPreviewPlayerAfterDelayAsync(int index, int generation, int delayMs)
    {
        if (index is not (0 or 1))
            return;

        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (generation != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (index == _activePreviewIndex)
            return;

        UiThreadHelper.Post(() => StopPreviewPlayer(index), DispatcherPriority.Background);
    }

    private async Task FallbackStopOldAfterTimeoutAsync(int oldIndex, int targetIndex, int generation)
    {
        if (oldIndex is not (0 or 1) || targetIndex is not (0 or 1))
            return;

        try
        {
            await Task.Delay(VideoStartTimeout).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (generation != Volatile.Read(ref _previewPlayGeneration))
            return;

        if (_activePreviewIndex == targetIndex)
            return;

        UiThreadHelper.Post(() => StopPreviewPlayer(oldIndex), DispatcherPriority.Background);
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
