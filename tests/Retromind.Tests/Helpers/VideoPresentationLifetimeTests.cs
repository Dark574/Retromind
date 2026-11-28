using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Retromind.Helpers.Video;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed class VideoPresentationLifetimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoppedSlot_RetainsCompatibleStorageThroughEitherStopAndFadeOrder(bool stopFirst) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var first = new Surface();
            using var second = new Surface();
            var fade = new CrossfadeVideoSurfaceControl
            {
                SurfaceA = first, SurfaceB = second, ActiveIndex = 0,
                FadeDurationMs = 30, RetainPreviousSurfaceDuringFade = true
            };
            Attach(fade.Children.OfType<VideoSurfaceControl>().ToArray());
            var a = fade.Children.OfType<VideoSurfaceControl>().First();
            first.Start();
            dispatcher.RunJobs();
            var bitmap = Bitmap(a)!;
            Assert.NotNull(bitmap);
            second.Start();
            dispatcher.RunJobs();
            fade.ActiveIndex = -1;
            var animation = a.SetValue(Visual.OpacityProperty, .5, BindingPriority.Animation);
            if (stopFirst) first.Stop();
            PumpFor(dispatcher, 80);
            Assert.Same(bitmap, Bitmap(a));
            animation!.Dispose();
            PumpFor(dispatcher, 60);
            if (!stopFirst)
            {
                Assert.Same(bitmap, Bitmap(a));
                first.Stop();
            }
            dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(a));
            first.Emit(); // A late stopped frame must not replace retained storage.
            dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(a));
            Lifecycle(a, "OnDetachedFromVisualTree");
            Assert.Null(Bitmap(a));
            AssertDisposed(bitmap);
            fade.SurfaceA = null;
            fade.SurfaceB = null;
        });

    [Fact]
    public async Task RapidReverse_ObsoleteFadeCannotReleaseReactivatedSlot() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var first = new Surface();
            using var second = new Surface();
            var fade = new CrossfadeVideoSurfaceControl
            {
                SurfaceA = first, SurfaceB = second, ActiveIndex = 0,
                FadeDurationMs = 30, RetainPreviousSurfaceDuringFade = true
            };
            Attach(fade.Children.OfType<VideoSurfaceControl>().ToArray());
            first.Start();
            second.Start();
            dispatcher.RunJobs();
            var a = fade.Children.OfType<VideoSurfaceControl>().First();
            var bitmap = Bitmap(a)!;
            fade.ActiveIndex = 1;
            first.Stop();
            first.Start();
            fade.ActiveIndex = 0;
            PumpFor(dispatcher, 90);
            Assert.Same(bitmap, Bitmap(a));
            using (var pixels = bitmap.Lock())
                Assert.Equal(first.Color, Marshal.ReadByte(pixels.Address));
            fade.SurfaceA = null;
            fade.SurfaceB = null;
        });

    [Fact]
    public async Task QueuedFrames_FromRetiredOrDetachedSurfaceCannotRecreateBitmap() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var old = new Surface();
            using var current = new Surface();
            var control = new VideoSurfaceControl { Surface = old };
            Attach(control);
            old.Start();
            var capturedCallback = old.FrameReady;
            control.Surface = current;
            capturedCallback?.Invoke();
            dispatcher.RunJobs();
            Assert.Null(Bitmap(control));

            current.Start(); // Queue a current frame, then detach before the UI copy.
            Lifecycle(control, "OnDetachedFromVisualTree");
            dispatcher.RunJobs();
            Assert.Null(Bitmap(control));
            Attach(control);
            current.Emit();
            dispatcher.RunJobs();
            Assert.NotNull(Bitmap(control));
            control.Surface = null;
        });

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public async Task ResolutionChange_ReleasesPreviousBitmapAndPreservesNativeOutputSize(int width, int height) =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            Attach(control);
            surface.Start();
            dispatcher.RunJobs();
            var initial = Bitmap(control)!;
            surface.Width = width;
            surface.Height = height;
            surface.Emit();
            dispatcher.RunJobs();
            AssertDisposed(initial);
            var resized = Bitmap(control)!;
            Assert.Equal(new PixelSize(width, height), resized.PixelSize);
            surface.Stop();
            Lifecycle(control, "OnDetachedFromVisualTree");
            dispatcher.RunJobs();
            Assert.Null(Bitmap(control));
            AssertDisposed(resized);
            control.Surface = null;
        });

    [Fact]
    public async Task RecordedRenderAccess_KeepsNativeBitmapAliveAfterTheUiLeaseIsReleased() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            Attach(control);
            surface.Start();
            dispatcher.RunJobs();
            Bitmap(control)!.Dispose();
            var tracked = new TrackedBitmap();
            typeof(VideoSurfaceControl).GetField("_bitmap", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(control, tracked);
            control.Measure(new Size(16, 8));
            control.Arrange(new Rect(0, 0, 16, 8));
            var platform = typeof(Bitmap)
                .GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracked)!;
            var native = platform.GetType().GetProperty("Item")!.GetValue(platform)!;

            // Exercise Avalonia's actual recorded DrawBitmap path, which clones
            // the platform reference for in-flight compositor/render work.
            var contextType = typeof(DrawingContext).Assembly.GetType(
                "Avalonia.Rendering.Composition.Drawing.RenderDataDrawingContext")!;
            using var context = (DrawingContext)Activator.CreateInstance(contextType, new object?[] { null })!;
            control.Render(context);
            surface.Stop();
            Lifecycle(control, "OnDetachedFromVisualTree");
            dispatcher.RunJobs();
            Assert.Null(Bitmap(control));
            Assert.Equal(1, tracked.DisposeCalls);
            Assert.False((bool)platform.GetType().GetProperty("IsAlive")!.GetValue(platform)!);
            using (var frame = (ILockedFramebuffer)native.GetType().GetMethod("Lock")!.Invoke(native, null)!)
                Assert.Equal(new PixelSize(16, 8), frame.Size);
            Lifecycle(control, "OnDetachedFromVisualTree");
            surface.Emit();
            dispatcher.RunJobs();
            Assert.Equal(1, tracked.DisposeCalls);
            control.Surface = null;
        });

    [Fact]
    public async Task HiddenIndependentChannel_RetainsOnlyItsSlotStorageUntilDetach() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            Attach(control);
            surface.Start();
            dispatcher.RunJobs();
            var bitmap = Bitmap(control)!;
            control.IsVisible = false;
            dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(control));
            surface.Stop();
            dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(control));
            Lifecycle(control, "OnDetachedFromVisualTree");
            Assert.Null(Bitmap(control));
            AssertDisposed(bitmap);
            control.Surface = null;
        });

    [Fact]
    public async Task ReattachedCrossfade_ReplacesDisposedStorageWithoutKeepingOldSubscriptions() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var first = new Surface();
            using var second = new Surface();
            var fade = new CrossfadeVideoSurfaceControl
            {
                SurfaceA = first, SurfaceB = second, ActiveIndex = 0,
                FadeDurationMs = 30, RetainPreviousSurfaceDuringFade = true
            };
            var children = fade.Children.OfType<VideoSurfaceControl>().ToArray();
            Attach(children);
            first.Start();
            second.Start();
            dispatcher.RunJobs();
            fade.ActiveIndex = -1;
            foreach (var child in children) Lifecycle(child, "OnDetachedFromVisualTree");
            Attach(children);
            first.Emit();
            second.Emit();
            dispatcher.RunJobs();
            var outgoing = Bitmap(children[0])!;
            Assert.NotNull(outgoing);
            first.Stop();
            PumpFor(dispatcher, 90);
            Assert.Same(outgoing, Bitmap(children[0]));
            Assert.NotNull(Bitmap(children[1]));
            fade.SurfaceA = null;
            AssertDisposed(outgoing);
            fade.SurfaceB = null;
            foreach (var child in children) Lifecycle(child, "OnDetachedFromVisualTree");
        });

    [Fact]
    public async Task ParentHiddenAfterStop_KeepsBoundedStorageAndDetachRemovesSubscriptions() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            var parent = new Grid { Children = { control } };
            Attach(control);
            surface.Start();
            dispatcher.RunJobs();
            var bitmap = Bitmap(control)!;
            surface.Stop();
            dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(control));
            parent.IsVisible = false;
            dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(control));
            Lifecycle(control, "OnDetachedFromVisualTree");
            Assert.Null(Bitmap(control));
            AssertDisposed(bitmap);
            parent.IsVisible = true;
            dispatcher.RunJobs();
            Assert.Null(surface.FrameReady);
            Assert.Null(Bitmap(control));
            control.Surface = null;
        });

    [Fact]
    public async Task UnattachedAndDetachedSlots_DoNotAllocateOrRetainPresentationPixels() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            surface.Start();
            dispatcher.RunJobs();
            Assert.Null(Bitmap(control));
            Assert.Null(surface.FrameReady);
            Attach(control);
            surface.Emit();
            dispatcher.RunJobs();
            var bitmap = Bitmap(control)!;
            var late = surface.FrameReady;
            Lifecycle(control, "OnDetachedFromVisualTree");
            late?.Invoke();
            dispatcher.RunJobs();
            Assert.Null(surface.FrameReady);
            Assert.Null(Bitmap(control));
            AssertDisposed(bitmap);
            Attach(control);
            surface.Emit();
            dispatcher.RunJobs();
            Assert.NotNull(Bitmap(control));
            Lifecycle(control, "OnDetachedFromVisualTree");
            Assert.Null(surface.FrameReady);
            control.Surface = null;
        });

    [Fact]
    public async Task CompatibleSlot_ReusesStorageAndCopiesFreshPixels() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            Attach(control);
            surface.Start(); dispatcher.RunJobs();
            var bitmap = Bitmap(control)!;
            surface.Stop();
            surface.Color = 211;
            surface.Start(); dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(control));
            using (var pixels = bitmap.Lock()) Assert.Equal(211, Marshal.ReadByte(pixels.Address));
            Lifecycle(control, "OnDetachedFromVisualTree");
            AssertDisposed(bitmap);
            control.Surface = null;
        });

    [Fact]
    public async Task AlternatingSlots_RetainOnlyTheirOwnCompatibleBitmapUntilDetachOrReplacement() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var first = new Surface();
            using var second = new Surface();
            var fade = new CrossfadeVideoSurfaceControl
            {
                SurfaceA = first, SurfaceB = second, ActiveIndex = 0,
                FadeDurationMs = 0, RetainPreviousSurfaceDuringFade = true
            };
            var children = fade.Children.OfType<VideoSurfaceControl>().ToArray();
            Attach(children);
            first.Start(); second.Start(); dispatcher.RunJobs();
            var a = Bitmap(children[0])!;
            var b = Bitmap(children[1])!;
            for (var i = 0; i < 12; i++)
            {
                var index = i % 2;
                var current = index == 0 ? first : second;
                current.Start(); dispatcher.RunJobs();
                fade.ActiveIndex = index;
                (index == 0 ? second : first).Stop();
                PumpFor(dispatcher, 5);
                Assert.Same(a, Bitmap(children[0]));
                Assert.Same(b, Bitmap(children[1]));
            }
            fade.ActiveIndex = -1;
            first.Stop(); second.Stop();
            PumpFor(dispatcher, 10);
            Assert.Same(a, Bitmap(children[0])); Assert.Same(b, Bitmap(children[1]));
            fade.SurfaceA = null; fade.SurfaceB = null;
            AssertDisposed(a); AssertDisposed(b);
            foreach (var child in children) Lifecycle(child, "OnDetachedFromVisualTree");
        });

    [Fact]
    public async Task FailedFirstCopy_AfterResizeDoesNotMarkUninitializedPixelsAsCurrent() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var surface = new Surface();
            var control = new VideoSurfaceControl { Surface = surface };
            Attach(control);
            surface.Start(); dispatcher.RunJobs();
            var first = Bitmap(control)!;
            surface.Width = 32;
            surface.CanCopy = false;
            surface.Emit(); dispatcher.RunJobs();
            AssertDisposed(first);
            Assert.NotNull(Bitmap(control));
            control.Measure(new Size(32, 8));
            control.Arrange(new Rect(0, 0, 32, 8));
            // A Render call must not touch newly allocated pixels until a copy succeeds.
            // Disposing the wrapper makes an accidental DrawImage observable.
            Bitmap(control)!.Dispose();
            var contextType = typeof(DrawingContext).Assembly.GetType(
                "Avalonia.Rendering.Composition.Drawing.RenderDataDrawingContext")!;
            using var context = (DrawingContext)Activator.CreateInstance(contextType, new object?[] { null })!;
            control.Render(context);
            typeof(VideoSurfaceControl).GetField("_bitmap", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(control, null);
            surface.CanCopy = true;
            surface.Emit(); dispatcher.RunJobs();
            using (var pixels = Bitmap(control)!.Lock()) Assert.Equal(surface.Color, Marshal.ReadByte(pixels.Address));
            Lifecycle(control, "OnDetachedFromVisualTree");
            control.Surface = null;
        });

    [Fact]
    public async Task SurfaceReplacementAndDetach_RemoveFrameHandlersAndRejectCapturedNotifications() =>
        await AvaloniaTestDispatcher.InvokeAsync(dispatcher =>
        {
            using var old = new Surface();
            using var current = new Surface();
            var control = new VideoSurfaceControl { Surface = old };
            Attach(control);
            old.Start(); dispatcher.RunJobs();
            var lateFrame = old.FrameReady;
            Assert.NotNull(lateFrame);
            control.Surface = current;
            Assert.Null(old.FrameReady);
            current.Start(); dispatcher.RunJobs();
            var bitmap = Bitmap(control)!;
            old.Stop(); lateFrame?.Invoke(); dispatcher.RunJobs();
            Assert.Same(bitmap, Bitmap(control));
            Lifecycle(control, "OnDetachedFromVisualTree");
            Assert.Null(current.FrameReady);
            lateFrame?.Invoke(); current.Emit(); dispatcher.RunJobs();
            Assert.Null(Bitmap(control));
            AssertDisposed(bitmap);
            control.Surface = null;
        });

    // Exercise the real lifecycle overrides on the managed dispatcher without a
    // windowing backend. These overrides do not consume the attachment arguments.
    private static void Attach(params VideoSurfaceControl[] controls)
    {
        foreach (var control in controls) Lifecycle(control, "OnAttachedToVisualTree");
    }

    private static void Lifecycle(Visual control, string method) =>
        control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(control, new object?[] { null });

    private static WriteableBitmap? Bitmap(VideoSurfaceControl control) =>
        (WriteableBitmap?)typeof(VideoSurfaceControl).GetField("_bitmap", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control);

    private static void AssertDisposed(WriteableBitmap bitmap) =>
        Assert.Throws<ObjectDisposedException>(() => bitmap.Lock());

    private static void PumpFor(Dispatcher dispatcher, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            dispatcher.RunJobs();
            Thread.Sleep(1);
        }
    }

    private sealed class TrackedBitmap() : WriteableBitmap(new PixelSize(16, 8), new Vector(96, 96),
        PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul)
    {
        public int DisposeCalls { get; private set; }
        public override void Dispose() { DisposeCalls++; base.Dispose(); }
    }

    private sealed class Surface : IVideoSurface, IFrameCopySource
    {
        public int Width { get; set; } = 16;
        public int Height { get; set; } = 8;
        public byte Color { get; set; } = 127;
        public bool CanCopy { get; set; } = true;
        public Action? FrameReady;
        event Action? IVideoSurface.FrameReady { add => FrameReady += value; remove => FrameReady -= value; }
        public IntPtr GetCurrentFrame() => throw new InvalidOperationException("Must use the safe copy contract.");
        public bool CopyFrameTo(IntPtr destination, int destinationSize)
        {
            if (!CanCopy) return false;
            Assert.True(destinationSize >= Width * Height * 4);
            Marshal.WriteByte(destination, Color);
            return true;
        }
        public void Start() => Emit();
        public void Stop() { }
        public void Emit() => FrameReady?.Invoke();
        public void Dispose() { }
    }
}
