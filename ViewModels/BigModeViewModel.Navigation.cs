using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        DisposeNavigation();
        ExpectVideoStop(0);
        ExpectVideoStop(1);
        ExpectVideoStop(2);

        ClearNodeFallbackOverrides(Items);

        _gamepadService.OnDirectionStateChanged -= OnGamepadDirectionStateChanged;
        _gamepadService.OnSelect -= OnGamepadSelect;
        _gamepadService.OnBack -= OnGamepadBack;
        _gamepadService.OnDetails -= OnGamepadDetails;
        _gamepadService.OnHome -= OnGamepadHome;
        _gamepadService.OnSystemMenu -= OnGamepadSystemMenu;
        _gamepadService.OnPreviousPage -= OnGamepadPreviousPage;
        _gamepadService.OnNextPage -= OnGamepadNextPage;
        DisposeHome();
        DisposeRetroAchievementsProgress();

        _videoSurfaceA.FrameReady -= OnMainVideoFrameReadyA;
        _videoSurfaceB.FrameReady -= OnMainVideoFrameReadyB;
        DisposeGamepadRepeatTimer();

        // Stop attract timer early so it can't fire while we tear down VLC
        try
        {
            if (_attractTimer != null)
            {
                _attractTimer.Tick -= OnAttractTimerTick;
                _attractTimer.Stop();
                _attractTimer.IsEnabled = false;
                _attractTimer = null;
            }
        }
        catch
        {
            // best effort
        }

        // Stop preview debounce timer (new UI-thread debounce, replaces the old CTS/Task.Run approach)
        try
        {
            DisposePreviewDebounceTimer();
        }
        catch
        {
            // ignore
        }

        try
        {
            DisposeActiveBezelDebounceTimer();
        }
        catch
        {
            // ignore
        }

        var mainPlayerA = _mediaPlayerA;
        var mainPlayerB = _mediaPlayerB;
        var secondaryPlayer = _secondaryPlayer;
        var vlc = _libVlc;
        var secondaryVlc = _secondaryLibVlc;

        MediaPlayer = null;
        _mediaPlayerA = null;
        _mediaPlayerB = null;
        _secondaryPlayer = null;

        SaveState();

        _ = Task.Run(() =>
        {
            try
            {
                // Stop & dispose secondary channel first
                var operation = "StopSecondaryOnDispose";
                try
                {
                    if (secondaryPlayer != null)
                    {
                        secondaryPlayer.EndReached -= OnSecondaryBackgroundEndReached;
                        // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                        secondaryPlayer.Stop();
                        operation = "DisposeSecondaryPlayer";
                        secondaryPlayer.Media = null;
                        secondaryPlayer.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync(operation == "StopSecondaryOnDispose" ? "Video.StopFailed" : "Video.ResourceCleanupFailed",
                        operation, 2, ex);
                }

                try
                {
                    _secondaryBackgroundMedia?.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeSecondaryMedia", 2, ex);
                }
                finally
                {
                    _secondaryBackgroundMedia = null;
                }

                // Stop & dispose main channel
                operation = "StopPreviewOnDispose";
                try
                {
                    if (mainPlayerA != null)
                    {
                        // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                        mainPlayerA.Stop();
                        operation = "DisposePreviewPlayer";
                        mainPlayerA.Media = null;
                        mainPlayerA.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync(operation == "StopPreviewOnDispose" ? "Video.StopFailed" : "Video.ResourceCleanupFailed",
                        operation, 0, ex);
                }

                operation = "StopPreviewOnDispose";
                try
                {
                    if (mainPlayerB != null)
                    {
                        // Stop regardless of IsPlaying to avoid stale audio on some VLC builds.
                        mainPlayerB.Stop();
                        operation = "DisposePreviewPlayer";
                        mainPlayerB.Media = null;
                        mainPlayerB.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync(operation == "StopPreviewOnDispose" ? "Video.StopFailed" : "Video.ResourceCleanupFailed",
                        operation, 1, ex);
                }

                try
                {
                    _currentPreviewMediaA?.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeCurrentPreviewMedia", 0, ex);
                }
                finally
                {
                    _currentPreviewMediaA = null;
                }

                try
                {
                    _currentPreviewMediaB?.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeCurrentPreviewMedia", 1, ex);
                }
                finally
                {
                    _currentPreviewMediaB = null;
                }

                try
                {
                    KeyValuePair<int, List<LibVLCSharp.Shared.Media>>[] pending;
                    lock (_previewMediaLock)
                    {
                        pending = _pendingDisposeMedia.ToArray();
                        _pendingDisposeMedia.Clear();
                    }
                    foreach (var pair in pending)
                    {
                        foreach (var media in pair.Value)
                        {
                            try
                            {
                                media.Dispose();
                            }
                            catch (Exception ex)
                            {
                                _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposePendingPreviewMedia", pair.Key, ex);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DrainPendingPreviewMedia", -1, ex);
                }

                // Dispose LibVLC last
                try
                {
                    vlc.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeLibVlc", 0, ex);
                }

                try
                {
                    secondaryVlc.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeSecondaryLibVlc", 2, ex);
                }

                try
                {
                    _videoSurfaceA.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeSurface", 0, ex);
                }

                try
                {
                    _videoSurfaceB.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeSurface", 1, ex);
                }

                try
                {
                    _secondaryVideoSurface?.Dispose();
                }
                catch (Exception ex)
                {
                    _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeSurface", 2, ex);
                }
            }
            catch (Exception ex)
            {
                // Best-effort cleanup; shutdown should not crash because VLC cleanup failed.
                _ = ReportVideoIssueAsync("Video.ResourceCleanupFailed", "DisposeVideoResources", -1, ex);
            }
        });
    }
}
