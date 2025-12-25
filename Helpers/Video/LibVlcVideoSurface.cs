using System;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Retromind.Helpers.Video;

/// <summary>
/// IVideoSurface-Implementierung, die von LibVLC-Video-Callbacks befüllt wird.
/// Erwartetes Format: BGRA32.
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
    /// VideoFormat-Callback für LibVLC.
    /// Signatur muss LibVLCVideoFormatCb entsprechen.
    /// </summary>
    internal uint VideoFormat(
        ref IntPtr opaque,
        IntPtr chroma,
        ref uint width,
        ref uint height,
        ref uint pitches,
        ref uint lines)
    {
        // BGRA32 als FourCC "RV32"
        var rv32 = BitConverter.ToUInt32(new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0);

        unsafe
        {
            // chroma zeigt auf 4 Bytes, die wir überschreiben
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

        return 1; // Anzahl der Video-Planes
    }

    /// <summary>
    /// VideoCleanup-Callback für LibVLC.
    /// </summary>
    internal void VideoCleanup(ref IntPtr opaque)
    {
        Dispose();
    }

    /// <summary>
    /// VideoLock-Callback für LibVLC.
    /// Signatur muss LibVLCVideoLockCb(IntPtr opaque, IntPtr planes) entsprechen.
    /// planes ist ein Pointer auf ein von LibVLC allokiertes void*[].
    /// </summary>
    internal IntPtr VideoLock(IntPtr opaque, IntPtr planes)
    {
        lock (_sync)
        {
            // planes zeigt auf ein Array von void*-Einträgen (für jede Plane).
            // Wir haben nur eine Plane -> ersten Eintrag auf unseren Buffer setzen.
            unsafe
            {
                var pPlanes = (IntPtr*)planes;
                pPlanes[0] = _buffer;
            }

            // Rückgabewert ist ein "picture handle", den Unlock/Display wieder bekommen.
            // Wir brauchen ihn nicht -> IntPtr.Zero reicht.
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// VideoUnlock-Callback für LibVLC.
    /// </summary>
    internal void VideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // Für unser einfaches Setup nichts zu tun.
    }

    /// <summary>
    /// VideoDisplay-Callback für LibVLC.
    /// Wird aufgerufen, wenn ein Frame fertig ist.
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