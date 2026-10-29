using System;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Retromind.Helpers.Video;
using Retromind.Models;

namespace Retromind.ViewModels;

public partial class BigModeViewModel
{
    // Diagnostic state only. Existing preview generations still control all playback/fades.
    // Index 2 identifies the independent secondary/background channel.
    private readonly int[] _videoPlaybackExpected = new int[3];
    private readonly int[] _videoPlaybackGenerations = new int[3];
    private readonly int[] _videoPlaybackFailureReported = new int[3];
    private readonly Lock _videoDiagnosticsLock = new();
    private int _secondaryPlaybackGeneration;

    private LibVlcVideoSurface? GetDiagnosticSurface(int index) => index switch
    {
        0 => _videoSurfaceA,
        1 => _videoSurfaceB,
        2 => _secondaryVideoSurface,
        _ => null
    };

    private void BeginVideoPlayback(int index, string? path, int generation, bool playbackExpected = true)
    {
        lock (_videoDiagnosticsLock)
        {
            if (GetDiagnosticSurface(index) is { } surface)
                surface.VideoPath = path;
            Volatile.Write(ref _videoPlaybackGenerations[index], generation);
            _videoPlaybackFailureReported[index] = 0;
            _videoPlaybackExpected[index] = playbackExpected ? 1 : 0;
        }
    }

    private void ExpectVideoStop(int index)
    {
        if (index is >= 0 and < 3)
        {
            lock (_videoDiagnosticsLock)
                _videoPlaybackExpected[index] = 0;
        }
    }

    private void OnVideoEncounteredError(int index, MediaPlayer player)
    {
        _ = ReportVideoPlaybackFailureAsync(index, "EncounteredError", expectedPlayer: player,
            generation: Volatile.Read(ref _videoPlaybackGenerations[index]));
    }

    private void OnVideoSurfaceError(int index, ErrorReport report)
    {
        if (report.Code == "Video.PlaybackFailed")
            _ = ReportVideoPlaybackFailureAsync(index, report.Context!["Operation"], report.Exception,
                sourceReport: report);
        else
            _ = VideoDiagnostics.ReportAsync(_errorService, report);
    }

    private async Task ReportVideoPlaybackFailureAsync(
        int index, string operation, Exception? exception = null, int? generation = null,
        MediaPlayer? expectedPlayer = null, bool requirePlaybackExpected = true, ErrorReport? sourceReport = null)
    {
        try
        {
            if (exception is OperationCanceledException)
                return;
            ErrorReport report;
            // Snapshot an entire diagnostic attempt atomically. An old VLC callback
            // must not consume the new attempt's duplicate-suppression flag or path.
            lock (_videoDiagnosticsLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || !_isViewReady || _isLaunching)
                    return;
                if (requirePlaybackExpected && _videoPlaybackExpected[index] == 0)
                    return;

                var attempt = _videoPlaybackGenerations[index];
                if (generation.HasValue && generation.Value != attempt)
                    return;
                if (index < 2 && attempt != Volatile.Read(ref _previewPlayGeneration))
                    return;
                if (expectedPlayer != null && !ReferenceEquals(expectedPlayer,
                        index == 2 ? _secondaryPlayer : GetMediaPlayer(index)))
                    return;
                if (_videoPlaybackFailureReported[index] != 0)
                    return;
                _videoPlaybackFailureReported[index] = 1;

                report = sourceReport ?? VideoDiagnostics.CreateReport(nameof(BigModeViewModel),
                    "Video.PlaybackFailed", operation, index, GetDiagnosticSurface(index)?.VideoPath, exception);
            }
            await VideoDiagnostics.ReportAsync(_errorService, report).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostic output must never escape into VLC or the preview state machine.
        }
    }

    private async Task ReportVideoIssueAsync(string code, string operation, int index, Exception exception)
    {
        try
        {
            var report = VideoDiagnostics.CreateReport(nameof(BigModeViewModel), code, operation,
                index, GetDiagnosticSurface(index)?.VideoPath, exception);
            await VideoDiagnostics.ReportAsync(_errorService, report).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort output only, including during shutdown.
        }
    }
}
