using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Temporarily displays a busy cursor while preserving the platform fallback.
/// </summary>
public sealed class BusyCursorScope : IDisposable
{
    private readonly Window _owner;
    private readonly Cursor? _previousCursor;
    private readonly Cursor _fallbackCursor;
    private readonly IReadOnlyList<CursorFrame>? _waylandFrames;
    private readonly DispatcherTimer? _animationTimer;
    private int _frameIndex;
    private bool _disposed;

    public BusyCursorScope(Window owner)
    {
        _owner = owner;
        _previousCursor = owner.Cursor;
        _fallbackCursor = new Cursor(StandardCursorType.Wait);

        // TODO: Remove this native-Wayland workaround once Avalonia uses the
        // desktop's active cursor theme and size and supports every animation
        // frame for StandardCursorType.Wait. Re-test when upgrading Avalonia;
        // versions 12.1.1 and 12.1.2 load the default 24 px theme and frame 0 only.
        if (IsNativeWayland(owner))
            _waylandFrames = XcursorThemeLoader.TryLoadWaitCursorFrames();

        if (_waylandFrames is { Count: > 0 })
        {
            _owner.Cursor = _waylandFrames[0].Cursor;
            if (_waylandFrames.Count > 1)
            {
                _animationTimer = new DispatcherTimer
                {
                    Interval = _waylandFrames[0].Delay
                };
                _animationTimer.Tick += OnAnimationTick;
                _animationTimer.Start();
            }
        }
        else
        {
            _owner.Cursor = _fallbackCursor;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_animationTimer is not null)
        {
            _animationTimer.Stop();
            _animationTimer.Tick -= OnAnimationTick;
        }

        _owner.Cursor = _previousCursor;
        if (_waylandFrames is not null)
        {
            foreach (var frame in _waylandFrames)
                frame.Cursor.Dispose();
        }

        _fallbackCursor.Dispose();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_disposed || _waylandFrames is not { Count: > 1 } || _animationTimer is null)
            return;

        _frameIndex = (_frameIndex + 1) % _waylandFrames.Count;
        var frame = _waylandFrames[_frameIndex];
        _owner.Cursor = frame.Cursor;
        _animationTimer.Interval = frame.Delay;
    }

    private static bool IsNativeWayland(Window owner)
    {
        if (!OperatingSystem.IsLinux() ||
            !string.Equals(
                Environment.GetEnvironmentVariable("AVALONIA_PLATFORM"),
                "wayland",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Native Wayland currently exposes no XID. If Wayland initialization
        // fell back to X11, leave Avalonia's already-correct behavior alone.
        return !string.Equals(
            owner.TryGetPlatformHandle()?.HandleDescriptor,
            "XID",
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CursorFrame(Cursor cursor, TimeSpan delay)
    {
        public Cursor Cursor { get; } = cursor;
        public TimeSpan Delay { get; } = delay;
    }

    private static class XcursorThemeLoader
    {
        private const string X11Library = "libX11.so.6";
        private const string XcursorLibrary = "libXcursor.so.1";
        private const int DefaultCursorSize = 24;

        public static IReadOnlyList<CursorFrame>? TryLoadWaitCursorFrames()
        {
            var frames = new List<CursorFrame>();
            IntPtr images = IntPtr.Zero;
            try
            {
                var theme = QueryActiveTheme();
                images = XcursorLibraryLoadImages("wait", theme.Name, theme.Size);
                if (images == IntPtr.Zero)
                    images = XcursorLibraryLoadImages("watch", theme.Name, theme.Size);
                if (images == IntPtr.Zero)
                    return null;

                var imageCollection = Marshal.PtrToStructure<XcursorImages>(images);
                if (imageCollection.Count <= 0 || imageCollection.Images == IntPtr.Zero)
                    return null;

                for (var index = 0; index < imageCollection.Count; index++)
                {
                    var imagePointer = Marshal.ReadIntPtr(imageCollection.Images, index * IntPtr.Size);
                    if (imagePointer == IntPtr.Zero)
                        continue;

                    var image = Marshal.PtrToStructure<XcursorImage>(imagePointer);
                    if (!IsUsable(image))
                        continue;

                    var width = checked((int)image.Width);
                    var height = checked((int)image.Height);
                    var stride = checked(width * 4);
                    using var bitmap = new Bitmap(
                        PixelFormats.Bgra8888,
                        AlphaFormat.Premul,
                        image.Pixels,
                        new PixelSize(width, height),
                        new Vector(96, 96),
                        stride);

                    var cursor = new Cursor(
                        bitmap,
                        new PixelPoint((int)image.HotspotX, (int)image.HotspotY));
                    var delay = TimeSpan.FromMilliseconds(Math.Clamp((int)image.Delay, 16, 1000));
                    frames.Add(new CursorFrame(cursor, delay));
                }

                return frames.Count > 0 ? frames : null;
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or
                EntryPointNotFoundException or
                ExternalException or
                OverflowException or
                ArgumentException)
            {
                Debug.WriteLine($"[Cursor] Could not load the desktop Xcursor theme: {ex.Message}");
                foreach (var frame in frames)
                    frame.Cursor.Dispose();
                return null;
            }
            finally
            {
                if (images != IntPtr.Zero)
                    XcursorImagesDestroy(images);
            }
        }

        private static XcursorTheme QueryActiveTheme()
        {
            string? name = null;
            var size = 0;
            IntPtr display = IntPtr.Zero;

            try
            {
                // Xcursor is desktop-environment neutral. In normal Wayland
                // sessions XWayland mirrors the active cursor theme here.
                display = XOpenDisplay(IntPtr.Zero);
                if (display != IntPtr.Zero)
                {
                    name = Marshal.PtrToStringUTF8(XcursorGetTheme(display));
                    size = XcursorGetDefaultSize(display);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Debug.WriteLine($"[Cursor] Could not query Xcursor desktop settings: {ex.Message}");
            }
            finally
            {
                if (display != IntPtr.Zero)
                    XCloseDisplay(display);
            }

            if (string.IsNullOrWhiteSpace(name))
                name = Environment.GetEnvironmentVariable("XCURSOR_THEME");
            if (string.IsNullOrWhiteSpace(name))
                name = "default";

            if (size <= 0 &&
                int.TryParse(Environment.GetEnvironmentVariable("XCURSOR_SIZE"), out var configuredSize))
            {
                size = configuredSize;
            }

            if (size is <= 0 or > 512)
                size = DefaultCursorSize;

            return new XcursorTheme(name, size);
        }

        private static bool IsUsable(XcursorImage image)
            => image.Pixels != IntPtr.Zero &&
               image.Width is > 0 and <= 512 &&
               image.Height is > 0 and <= 512 &&
               image.HotspotX < image.Width &&
               image.HotspotY < image.Height &&
               image.Delay <= int.MaxValue;

        private readonly record struct XcursorTheme(string Name, int Size);

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct XcursorImages
        {
            public readonly int Count;
            public readonly IntPtr Images;
            public readonly IntPtr Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct XcursorImage
        {
            public readonly uint Version;
            public readonly uint Size;
            public readonly uint Width;
            public readonly uint Height;
            public readonly uint HotspotX;
            public readonly uint HotspotY;
            public readonly uint Delay;
            public readonly IntPtr Pixels;
        }

        [DllImport(X11Library)]
        private static extern IntPtr XOpenDisplay(IntPtr displayName);

        [DllImport(X11Library)]
        private static extern int XCloseDisplay(IntPtr display);

        [DllImport(XcursorLibrary)]
        private static extern IntPtr XcursorGetTheme(IntPtr display);

        [DllImport(XcursorLibrary)]
        private static extern int XcursorGetDefaultSize(IntPtr display);

        [DllImport(XcursorLibrary, CharSet = CharSet.Ansi)]
        private static extern IntPtr XcursorLibraryLoadImages(string name, string theme, int size);

        [DllImport(XcursorLibrary)]
        private static extern void XcursorImagesDestroy(IntPtr images);
    }
}
