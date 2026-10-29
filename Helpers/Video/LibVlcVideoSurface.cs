using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Retromind.Models;

namespace Retromind.Helpers.Video;

/// <summary>
/// IVideoSurface implementation that is fed by LibVLC video callbacks.
/// Expected format: BGRA32.
/// </summary>
public sealed class LibVlcVideoSurface : IVideoSurface, IFrameCopySource
{
    private readonly object _sync = new();
    private readonly Action<ErrorReport>? _reportError;
    private readonly int _surfaceIndex;
    private readonly Func<int, IntPtr> _allocate;
    private readonly Action<IntPtr> _free;
    private IntPtr _allocation;
    private IntPtr _buffer;
    private bool _disposed;

    internal const uint MaximumFrameDimension = 16_384;
    internal const int MaximumFrameBytes = 256 * 1024 * 1024;

    public LibVlcVideoSurface() : this(null, 0)
    {
    }

    internal LibVlcVideoSurface(Action<ErrorReport>? reportError, int surfaceIndex,
        Func<int, IntPtr>? allocate = null, Action<IntPtr>? free = null)
    {
        _reportError = reportError;
        _surfaceIndex = surfaceIndex;
        _allocate = allocate ?? Marshal.AllocHGlobal;
        _free = free ?? Marshal.FreeHGlobal;
    }

    internal string? VideoPath { get; set; }

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
        pitches = 0;
        lines = 0;
        try
        {
            lock (_sync)
            {
                if (_disposed)
                    return 0;

                FreeBufferLocked();
                if (chroma == IntPtr.Zero || !TryGetFrameLayout(width, height, out var pitch, out var size))
                    throw new ArgumentOutOfRangeException(nameof(width), "Unsupported video frame dimensions or format pointer.");

                // LibVLC requires plane pointers aligned to 32 bytes. Retain the
                // original allocation for FreeHGlobal; rows stay tightly packed BGRA.
                _allocation = _allocate(checked(size + 31));
                if (_allocation == IntPtr.Zero)
                    throw new OutOfMemoryException("Could not allocate the video frame buffer.");
                _buffer = (IntPtr)(((nuint)_allocation + 31) & ~(nuint)31);
                Marshal.WriteInt32(chroma, BitConverter.ToInt32([(byte)'R', (byte)'V', (byte)'3', (byte)'2']));
                Width = (int)width;
                Height = (int)height;
                pitches = (uint)pitch;
                lines = height;
            }

            return 1; // Number of picture buffers allocated (not the number of planes).
        }
        catch (Exception ex)
        {
            ReleaseBuffer("VideoFormatCleanup");
            ReportFailure("Video.PlaybackFailed", "VideoFormat", ex, new Dictionary<string, string>
            {
                ["Width"] = width.ToString(CultureInfo.InvariantCulture),
                ["Height"] = height.ToString(CultureInfo.InvariantCulture)
            });
            return 0; // LibVLC's setup failure contract; no managed exception crosses the callback.
        }
    }

    internal static bool TryGetFrameLayout(uint width, uint height, out int pitch, out int size)
    {
        pitch = 0;
        size = 0;
        if (width == 0 || height == 0 || width > MaximumFrameDimension || height > MaximumFrameDimension)
            return false;

        var stride = (ulong)width * 4;
        var bytes = stride * height;
        if (bytes > MaximumFrameBytes)
            return false;

        pitch = checked((int)stride);
        size = checked((int)bytes);
        return true;
    }

    /// <summary>
    /// VideoCleanup callback for LibVLC.
    /// </summary>
    internal void VideoCleanup(ref IntPtr opaque)
    {
        // Cleanup is part of format changes and stop/start; it does not dispose the surface.
        ReleaseBuffer("VideoCleanup");
    }

    /// <summary>
    /// VideoLock callback for LibVLC.
    /// Signature must match LibVLCVideoLockCb(IntPtr opaque, IntPtr planes).
    /// planes is a pointer to a void*[] allocated by LibVLC.
    /// </summary>
    internal IntPtr VideoLock(IntPtr opaque, IntPtr planes)
    {
        if (planes == IntPtr.Zero)
            return IntPtr.Zero;
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
        try
        {
            lock (_sync)
                if (_disposed || _buffer == IntPtr.Zero)
                    return;
            FrameReady?.Invoke();
        }
        catch (Exception ex)
        {
            ReportFailure("Video.PlaybackFailed", "VideoDisplay", ex);
        }
    }

    public IntPtr GetCurrentFrame()
    {
        lock (_sync)
        {
            return _buffer;
        }
    }

    internal bool TryGetFrameSize(out int width, out int height)
    {
        lock (_sync)
        {
            width = Width;
            height = Height;
            return !_disposed && _buffer != IntPtr.Zero && width > 0 && height > 0;
        }
    }

    public void Dispose()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                FreeBufferLocked();
            }
        }
        catch (Exception ex)
        {
            ReportFailure("Video.ResourceCleanupFailed", "DisposeSurface", ex);
        }
    }

    public bool CopyFrameTo(IntPtr destination, int destinationSize)
    {
        if (destination == IntPtr.Zero || destinationSize <= 0)
            return false;

        lock (_sync)
        {
            if (_disposed || _buffer == IntPtr.Zero || Width <= 0 || Height <= 0)
                return false;

            var srcSize = (long)Width * Height * 4;
            var bytesToCopy = (long)Math.Min(destinationSize, srcSize);
            if (bytesToCopy <= 0)
                return false;

            unsafe
            {
                Buffer.MemoryCopy(
                    source: (void*)_buffer,
                    destination: (void*)destination,
                    destinationSizeInBytes: destinationSize,
                    sourceBytesToCopy: bytesToCopy);
            }

            return true;
        }
    }

    private void FreeBufferLocked()
    {
        var allocation = _allocation;
        _allocation = IntPtr.Zero;
        _buffer = IntPtr.Zero;
        Width = 0;
        Height = 0;
        if (allocation != IntPtr.Zero)
            _free(allocation);
    }

    private void ReleaseBuffer(string operation)
    {
        try
        {
            lock (_sync)
                FreeBufferLocked();
        }
        catch (Exception ex)
        {
            ReportFailure("Video.ResourceCleanupFailed", operation, ex);
        }
    }

    internal void ReportFailure(string code, string operation, Exception exception,
        IReadOnlyDictionary<string, string>? details = null)
    {
        try
        {
            _reportError?.Invoke(VideoDiagnostics.CreateReport(nameof(LibVlcVideoSurface), code,
                operation, _surfaceIndex, VideoPath, exception, details));
        }
        catch
        {
            // Diagnostics are best effort, including when called across the native boundary.
        }
    }
}
