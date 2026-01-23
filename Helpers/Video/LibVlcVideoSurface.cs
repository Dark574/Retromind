using System;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Retromind.Helpers.Video;

/// <summary>
/// IVideoSurface implementation that is fed by LibVLC video callbacks.
/// Expected format: BGRA32.
/// </summary>
public sealed class LibVlcVideoSurface : IVideoSurface
{
    private readonly object _sync = new();
    private IntPtr _buffer;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public event Action? FrameReady;

    /// <summary>
    /// VideoFormat callback for LibVLC.
    /// Signature must match LibVLCVideoFormatCb.
    /// </summary>
    internal uint VideoFormat(
        ref IntPtr opaque,
        IntPtr chroma,
        ref uint width,
        ref uint height,
        ref uint pitches,
        ref uint lines)
    {
        // BGRA32 as FourCC "RV32"
        var rv32 = BitConverter.ToUInt32(new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0);

        unsafe
        {
            // chroma points to 4 bytes that we overwrite
            var p = (uint*)chroma;
            *p = rv32;
        }

        Width = (int)width;
        Height = (int)height;

        pitches = (uint)(Width * 4); // 4 Bytes pro Pixel (BGRA)
        lines = (uint)Height;

        lock (_sync)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            var size = (int)(pitches * lines);
            _buffer = Marshal.AllocHGlobal(size);
        }

        return 1; // Number of video planes
    }

    /// <summary>
    /// VideoCleanup callback for LibVLC.
    /// </summary>
    internal void VideoCleanup(ref IntPtr opaque)
    {
        Dispose();
    }

    /// <summary>
    /// VideoLock callback for LibVLC.
    /// Signature must match LibVLCVideoLockCb(IntPtr opaque, IntPtr planes).
    /// planes is a pointer to a void*[] allocated by LibVLC.
    /// </summary>
    internal IntPtr VideoLock(IntPtr opaque, IntPtr planes)
    {
        lock (_sync)
        {
            // planes points to an array of void* entries (one per plane).
            // We only have one plane, so set the first entry to our buffer.
            unsafe
            {
                var pPlanes = (IntPtr*)planes;
                pPlanes[0] = _buffer;
            }

            // The return value is a "picture handle" passed to Unlock/Display.
            // We do not need it, so IntPtr.Zero is fine.
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// VideoUnlock callback for LibVLC.
    /// </summary>
    internal void VideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // Nothing to do for this simple setup.
    }

    /// <summary>
    /// VideoDisplay callback for LibVLC.
    /// Called when a frame is ready.
    /// </summary>
    internal void VideoDisplay(IntPtr opaque, IntPtr picture)
    {
        FrameReady?.Invoke();
    }

    public IntPtr GetCurrentFrame()
    {
        lock (_sync)
        {
            return _buffer;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_sync)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }
    }
}
