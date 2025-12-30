using System;
using LibVLCSharp.Shared;

namespace Retromind.Helpers.Video;

/// <summary>
/// IVideoPlayer-Implementierung auf Basis von LibVLC.
/// Nutzt Video-Callbacks, um Frames in eine LibVlcVideoSurface zu schreiben.
/// </summary>
public sealed class LibVlcVideoPlayer : IVideoPlayer
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly LibVlcVideoSurface _surface;
    private bool _disposed;

    public IVideoSurface Surface => _surface;

    public bool IsPlaying => _mediaPlayer.IsPlaying;

    public LibVlcVideoPlayer()
    {
        // Make sure Core.Initialize() was called in Program.Main before creating LibVLC instances.
        // Default: quiet logs for normal runtime. For troubleshooting you can temporarily enable
        // debug logs and/or increase verbosity (e.g. "--verbose=2").
        _libVlc = new LibVLC(
            enableDebugLogs: false,
            "--no-osd",
            "--quiet");

        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            // Ensure we start with a sane, audible volume
            Volume = 100
        };

        _surface = new LibVlcVideoSurface();

        // Configure video format + callbacks to render into the LibVlcVideoSurface
        _mediaPlayer.SetVideoFormatCallbacks(
            _surface.VideoFormat,
            _surface.VideoCleanup);

        _mediaPlayer.SetVideoCallbacks(
            _surface.VideoLock,
            _surface.VideoUnlock,
            _surface.VideoDisplay);
    }

    public void Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Video path must not be empty.", nameof(path));

        // Media NICHT sofort disposen – Player hält die Referenz.
        var media = new Media(_libVlc, path, FromType.FromPath);
        _mediaPlayer.Media = media;
    }

    public void Play()
    {
        _mediaPlayer.Play();
    }

    public void Pause()
    {
        _mediaPlayer.SetPause(true);
    }

    public void Stop()
    {
        _mediaPlayer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mediaPlayer.Stop();

        _mediaPlayer.Media?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        _surface.Dispose();
    }
}