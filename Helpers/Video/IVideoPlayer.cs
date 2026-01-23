using System;

namespace Retromind.Helpers.Video;

/// <summary>
/// Abstract video player that feeds an IVideoSurface.
/// Implementations can use LibVLC, mpv, or a custom backend.
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
