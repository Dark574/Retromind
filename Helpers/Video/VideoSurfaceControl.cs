using System;
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

    public IVideoSurface? Surface
    {
        get => GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    /// <summary>
    /// Steuert, wie das Video in die Bounds skaliert wird.
    /// Fill (Standard), Uniform, UniformToFill.
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

        _bitmap = null;

        if (newSurface != null)
        {
            newSurface.FrameReady += OnFrameReady;
            // Bitmap wird dynamisch in CopyFrameAndInvalidate erstellt,
            // sobald Width/Height vom Video-Callback bekannt sind.
        }

        InvalidateVisual();
    }

    private void OnFrameReady()
    {
        // Wird vermutlich von einem Hintergrundthread (Timer, LibVLC, ...) aufgerufen.
        // Die eigentliche Arbeit MUSS auf dem UI-Thread passieren.
        Dispatcher.UIThread.Post(CopyFrameAndInvalidate);
    }

    private void CopyFrameAndInvalidate()
    {
        var surface = Surface;
        if (surface == null)
            return;

        // Falls wir noch kein Bitmap haben oder die Größe sich geändert hat,
        // hier neu erzeugen.
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

        var ptr = surface.GetCurrentFrame();
        if (ptr == IntPtr.Zero)
            return;

        using (var fb = _bitmap.Lock())
        {
            unsafe
            {
                // Ziel- und Quellgröße berechnen
                var destSize = fb.RowBytes * fb.Size.Height;

                // Pufferlänge der Quelle bestimmen (Width * Height * 4 für BGRA32)
                var srcSize = surface.Width * surface.Height * 4;

                var bytesToCopy = (long)Math.Min(destSize, srcSize);

                Buffer.MemoryCopy(
                    source: (void*)ptr,
                    destination: (void*)fb.Address,
                    destinationSizeInBytes: destSize,
                    sourceBytesToCopy: bytesToCopy);
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
                // Ganzes Video sichtbar, letterboxing/pillarboxing erlaubt, zentriert
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
                // Video füllt den Slot, überschüssige Teile werden abgeschnitten, zentriert
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
                // Einfach an die Bounds stretchen
                return destBounds;
        }
    }
}