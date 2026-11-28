using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Retromind.Helpers.Video;

public class VideoSurfaceControl : Control
{
    public static readonly StyledProperty<IVideoSurface?> SurfaceProperty =
        AvaloniaProperty.Register<VideoSurfaceControl, IVideoSurface?>(
            nameof(Surface));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<VideoSurfaceControl, Stretch>(
            nameof(Stretch),
            defaultValue: Stretch.Fill);

    private WriteableBitmap? _bitmap;
    private int _latestFrameRevision;
    private int _frameCopyPending;
    private int _surfaceGeneration;
    private int _requestedSurfaceGeneration;
    private Action? _frameReadyHandler;
    private bool _bitmapHasFrame;
    // A bound but never attached theme slot must not allocate presentation pixels.
    private bool _detached = true;

    public IVideoSurface? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    /// <summary>
    /// Controls how the video is scaled into the bounds.
    /// Fill (default), Uniform, UniformToFill.
    /// </summary>
    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    static VideoSurfaceControl()
    {
        SurfaceProperty.Changed.AddClassHandler<VideoSurfaceControl>((c, e) =>
        {
            c.OnSurfaceChanged((IVideoSurface?)e.OldValue, (IVideoSurface?)e.NewValue);
        });
    }

    private void OnSurfaceChanged(IVideoSurface? oldSurface, IVideoSurface? newSurface)
    {
        Unsubscribe(oldSurface);
        DisposeBitmap(oldSurface);
        if (!_detached)
            Subscribe(newSurface);
        InvalidateVisual();
    }

    private void Unsubscribe(IVideoSurface? surface)
    {
        Interlocked.Increment(ref _surfaceGeneration);
        if (surface != null && _frameReadyHandler != null)
            surface.FrameReady -= _frameReadyHandler;
        _frameReadyHandler = null;
    }

    private void Subscribe(IVideoSurface? surface)
    {
        if (surface == null)
            return;
        var generation = Volatile.Read(ref _surfaceGeneration);
        _frameReadyHandler = () => OnFrameReady(generation);
        surface.FrameReady += _frameReadyHandler;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _detached = false;
        Unsubscribe(Surface);
        Subscribe(Surface);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _detached = true;
        Unsubscribe(Surface);
        DisposeBitmap(Surface);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnFrameReady(int generation)
    {
        // A captured callback from a replaced/detached surface cannot enqueue a new copy.
        if (generation != Volatile.Read(ref _surfaceGeneration))
            return;
        Volatile.Write(ref _requestedSurfaceGeneration, generation);
        Interlocked.Increment(ref _latestFrameRevision);
        QueueLatestFrameCopy();
    }

    private void QueueLatestFrameCopy()
    {
        if (Interlocked.CompareExchange(ref _frameCopyPending, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(ProcessLatestFrame, DispatcherPriority.Render);
    }

    private void ProcessLatestFrame()
    {
        var processedRevision = Volatile.Read(ref _latestFrameRevision);

        try
        {
            if (!_detached && _surfaceGeneration == Volatile.Read(ref _requestedSurfaceGeneration))
                CopyFrameAndInvalidate();
        }
        catch (Exception ex) when (Surface is LibVlcVideoSurface)
        {
            ((LibVlcVideoSurface)Surface).ReportFailure("Video.PlaybackFailed", "RenderFrame", ex);
        }
        finally
        {
            Volatile.Write(ref _frameCopyPending, 0);

            if (processedRevision != Volatile.Read(ref _latestFrameRevision))
                QueueLatestFrameCopy();
        }
    }

    private void CopyFrameAndInvalidate()
    {
        var surface = Surface;
        if (surface == null)
            return;

        int width, height;
        if (surface is LibVlcVideoSurface vlcSurface)
        {
            if (!vlcSurface.TryGetFrameSize(out width, out height))
                return;
        }
        else
        {
            width = surface.Width;
            height = surface.Height;
        }

        // If we do not have a bitmap yet or the size changed, create it here.
        if (width > 0 && height > 0)
        {
            if (_bitmap == null ||
                _bitmap.PixelSize.Width != width ||
                _bitmap.PixelSize.Height != height)
            {
                DisposeBitmap(surface);
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);
            }
        }

        if (_bitmap == null)
            return;

        using (var fb = _bitmap.Lock())
        {
            var destSize = checked(fb.RowBytes * fb.Size.Height);

            if (surface is IFrameCopySource copySource)
            {
                if (!copySource.CopyFrameTo(fb.Address, destSize))
                    return;
            }
            else
            {
                var ptr = surface.GetCurrentFrame();
                if (ptr == IntPtr.Zero)
                    return;

                unsafe
                {
                    // Compute source buffer size (Width * Height * 4 for BGRA32).
                    var srcSize = checked((long)width * height * 4);

                    var bytesToCopy = (long)Math.Min(destSize, srcSize);

                    Buffer.MemoryCopy(
                        source: (void*)ptr,
                        destination: (void*)fb.Address,
                        destinationSizeInBytes: destSize,
                        sourceBytesToCopy: bytesToCopy);
                }
            }
        }

        _bitmapHasFrame = true;
        InvalidateVisual();
    }

    private void DisposeBitmap(IVideoSurface? surface)
    {
        var bitmap = _bitmap;
        _bitmap = null;
        _bitmapHasFrame = false;
        // DrawingContext records its own ref-counted platform-bitmap reference.
        // Dropping this UI owner's reference does not invalidate render work that
        // Avalonia has already recorded for the compositor.
        try
        {
            bitmap?.Dispose();
        }
        catch (Exception ex) when (surface is LibVlcVideoSurface)
        {
            ((LibVlcVideoSurface)surface).ReportFailure("Video.ResourceCleanupFailed", "DisposeBitmap", ex);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_bitmap == null || !_bitmapHasFrame)
            return;

        var sourceSize = _bitmap.Size;
        var destRect = CalculateDestRect(sourceSize, Bounds, Stretch);

        context.DrawImage(_bitmap, new Rect(sourceSize), destRect);
    }

    private static Rect CalculateDestRect(Size source, Rect destBounds, Stretch stretch)
    {
        if (source.Width <= 0 || source.Height <= 0 ||
            destBounds.Width <= 0 || destBounds.Height <= 0)
        {
            return destBounds;
        }

        var srcAspect = source.Width / source.Height;
        var destAspect = destBounds.Width / destBounds.Height;

        switch (stretch)
        {
            case Stretch.Uniform:
            {
                // Entire video visible, letterboxing/pillarboxing allowed, centered.
                bool fitWidth = destAspect <= srcAspect;
                double scale = fitWidth
                    ? destBounds.Width / source.Width
                    : destBounds.Height / source.Height;

                var w = source.Width * scale;
                var h = source.Height * scale;
                var x = destBounds.X + (destBounds.Width - w) / 2.0;
                var y = destBounds.Y + (destBounds.Height - h) / 2.0;
                return new Rect(x, y, w, h);
            }

            case Stretch.UniformToFill:
            {
                // Video fills the slot, overflow clipped, centered.
                bool fillWidth = destAspect >= srcAspect;
                double scale = fillWidth
                    ? destBounds.Width / source.Width
                    : destBounds.Height / source.Height;

                var w = source.Width * scale;
                var h = source.Height * scale;
                var x = destBounds.X + (destBounds.Width - w) / 2.0;
                var y = destBounds.Y + (destBounds.Height - h) / 2.0;
                return new Rect(x, y, w, h);
            }

            case Stretch.Fill:
            default:
                // Stretch to bounds.
                return destBounds;
        }
    }
}
