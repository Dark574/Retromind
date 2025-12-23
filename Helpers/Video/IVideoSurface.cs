using System;

namespace Retromind.Helpers.Video;

/// <summary>
/// Stellt einen Rohbild-Puffer für Videoframes bereit.
/// Wird später von LibVLC (oder mpv, ffmpeg, …) beschrieben.
/// </summary>
public interface IVideoSurface : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>
    /// Wird ausgelöst, wenn ein neuer Frame verfügbar ist.
    /// Das UI kann sich dann den Puffer holen und neu zeichnen.
    /// </summary>
    event Action? FrameReady;

    /// <summary>
    /// Liefert einen Pointer auf die aktuellen Pixel (z.B. BGRA32).
    /// Lebensdauer: gültig bis zum nächsten FrameReady.
    /// </summary>
    IntPtr GetCurrentFrame();
}