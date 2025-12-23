using System;

namespace Retromind.Helpers.Video;

/// <summary>
/// Abstrakter Videoplayer, der eine IVideoSurface füttert.
/// Konkrete Implementierungen können LibVLC, mpv oder etwas Eigenes sein.
/// </summary>
public interface IVideoPlayer : IDisposable
{
    IVideoSurface Surface { get; }

    void Load(string path);
    void Play();
    void Pause();
    void Stop();

    bool IsPlaying { get; }
}
