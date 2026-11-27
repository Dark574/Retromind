using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    private Task? _videoTeardown;
    internal Task VideoTeardown => _videoTeardown ?? Task.CompletedTask;

    internal async Task DisposeAsync()
    {
        try
        {
            await UiThreadHelper.InvokeAsync(Dispose).ConfigureAwait(false);
        }
        finally
        {
            await VideoTeardown.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (!UiThreadHelper.CheckAccess())
        {
            UiThreadHelper.Post(Dispose);
            return;
        }

        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Invalidate UI work before transferring native ownership to the worker.
        _isViewReady = false;
        Interlocked.Increment(ref _previewPlayGeneration);
        Interlocked.Increment(ref _overlayFadeGeneration);
        ClearPendingPreviewStart();
        ExpectVideoStop(0);
        ExpectVideoStop(1);
        ExpectVideoStop(2);

        try
        {
            SaveState();
            EndNavigationScroll(resumePreview: false);
            CloseSearch(resumePreview: false);
            CloseAlphabetNavigation(resumePreview: false);
            DisposeNavigation();
            ClearNodeFallbackOverrides(Items);

            _gamepadService.OnDirectionStateChanged -= OnGamepadDirectionStateChanged;
            _gamepadService.OnSelect -= OnGamepadSelect;
            _gamepadService.OnBack -= OnGamepadBack;
            _gamepadService.OnDetails -= OnGamepadDetails;
            _gamepadService.OnHome -= OnGamepadHome;
            _gamepadService.OnSystemMenu -= OnGamepadSystemMenu;
            _gamepadService.OnPageStateChanged -= OnGamepadPageStateChanged;
            _gamepadService.OnAlphabet -= OnGamepadAlphabet;
            _gamepadService.OnSearch -= OnGamepadSearch;

            DisposeHome();
            DisposeRetroAchievementsProgress();
            DisposeGamepadRepeatTimer();
            StopAttractModeTimer();
            DisposePreviewDebounceTimer();
            DisposeActiveBezelDebounceTimer();
        }
        finally
        {
            StartVideoTeardown();
        }
    }

    private void StartVideoTeardown()
    {
        _videoSurfaceA.FrameReady -= OnMainVideoFrameReadyA;
        _videoSurfaceB.FrameReady -= OnMainVideoFrameReadyB;

        var mainPlayerA = _mediaPlayerA;
        var mainPlayerB = _mediaPlayerB;
        var secondaryPlayer = _secondaryPlayer;
        var mainMediaA = TakePreviewMedia(0);
        var mainMediaB = TakePreviewMedia(1);
        var secondaryMedia = _secondaryBackgroundMedia;
        _mediaPlayerA = null;
        _mediaPlayerB = null;
        _secondaryPlayer = null;
        _secondaryBackgroundMedia = null;

        _videoTeardown = Task.Run(() =>
        {
            var secondaryStopped = ReleaseVideoPlayer(2, secondaryPlayer, secondaryMedia, "SecondaryOnDispose", disposePlayer: true);
            var mainAStopped = ReleaseVideoPlayer(0, mainPlayerA, mainMediaA, "PreviewOnDispose", disposePlayer: true);
            var mainBStopped = ReleaseVideoPlayer(1, mainPlayerB, mainMediaB, "PreviewOnDispose", disposePlayer: true);

            // If every native operation on a player failed, its callbacks may
            // still use the frame buffer. Leave that buffer to VLC's cleanup.
            if (mainAStopped)
                _videoSurfaceA.Dispose();
            if (mainBStopped)
                _videoSurfaceB.Dispose();
            if (secondaryStopped)
                _secondaryVideoSurface?.Dispose();

            DisposeVlcInstance(_libVlc, 0);
            DisposeVlcInstance(_secondaryLibVlc, 2);
        });
        MediaPlayer = null;
    }

    private void DisposeVlcInstance(IDisposable? instance, int index)
    {
        try
        {
            instance?.Dispose();
        }
        catch (Exception ex)
        {
            _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeLibVlc", index, ex);
        }
    }
}
