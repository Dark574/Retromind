using System;

namespace Retromind.Helpers.Video;

/// <summary>
/// Provides a raw image buffer for video frames.
/// Filled by LibVLC (or mpv, ffmpeg, etc.).
/// </summary>
public interface IVideoSurface : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Raised when a new frame is available.
    /// The UI can fetch the buffer and redraw.
    /// </summary>
    event Action? FrameReady;

    /// <summary>
    /// Returns a pointer to the current pixels (e.g. BGRA32).
    /// Lifetime: valid until the next FrameReady.
    /// </summary>
    IntPtr GetCurrentFrame();
}
