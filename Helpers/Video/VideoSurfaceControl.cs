using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
        if (oldSurface != null)
            oldSurface.FrameReady -= OnFrameReady;

        _bitmap?.Dispose();
        _bitmap = null;

        if (newSurface != null)
        {
            newSurface.FrameReady += OnFrameReady;
            // Bitmap is created dynamically in CopyFrameAndInvalidate
            // once Width/Height are known from the video callback.
        }

        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (Surface != null)
        {
            // Avoid double subscription if we're reattached with the same surface.
            Surface.FrameReady -= OnFrameReady;
            Surface.FrameReady += OnFrameReady;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Surface != null)
            Surface.FrameReady -= OnFrameReady;

        _bitmap?.Dispose();
        _bitmap = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void OnFrameReady()
    {
        // Likely called from a background thread (timer, LibVLC, ...).
        // The actual work MUST run on the UI thread.
        Dispatcher.UIThread.Post(CopyFrameAndInvalidate);
    }

    private void CopyFrameAndInvalidate()
    {
        var surface = Surface;
        if (surface == null)
            return;

        // If we do not have a bitmap yet or the size changed, create it here.
        if (surface.Width > 0 && surface.Height > 0)
        {
            if (_bitmap == null ||
                _bitmap.PixelSize.Width != surface.Width ||
                _bitmap.PixelSize.Height != surface.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(surface.Width, surface.Height),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);
            }
        }

        if (_bitmap == null)
            return;

        using (var fb = _bitmap.Lock())
        {
            var destSize = fb.RowBytes * fb.Size.Height;

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
                    var srcSize = surface.Width * surface.Height * 4;

                    var bytesToCopy = (long)Math.Min(destSize, srcSize);

                    Buffer.MemoryCopy(
                        source: (void*)ptr,
                        destination: (void*)fb.Address,
                        destinationSizeInBytes: destSize,
                        sourceBytesToCopy: bytesToCopy);
                }
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_bitmap == null)
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
