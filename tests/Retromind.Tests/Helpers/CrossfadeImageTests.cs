using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Retromind.Extensions;
using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;
using SkiaSharp;

namespace Retromind.Tests.Helpers;

public sealed class CrossfadeImageTests
{
    [Fact]
    public async Task RapidChanges_CancelPendingTargetWithoutReleasingLastValidImage()
    {
        using var temp = new TemporaryDirectory();
        var first = temp.GetPath("first.png");
        var skipped = temp.GetPath("skipped.png");
        var final = temp.GetPath("final.png");
        WritePng(first, 16, 8);
        WritePng(skipped, 16, 16);
        WritePng(final, 8, 16);
        await OnDispatcher(dispatcher =>
        {
            var image = new CrossfadeImage { DecodeWidth = 8, FadeDurationMs = 50 };
            AsyncImageHelper.SetUseSerialDecode(image, true);
            image.Url = first;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var active = image.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var validBitmap = active.Source;
            var gate = (SemaphoreSlim)typeof(AsyncImageHelper).GetField("SerialDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            gate.Wait();
            try
            {
                image.Url = skipped; // Target waits behind the real decode gate.
                var pending = image.Children.OfType<Image>().Single(i => !ReferenceEquals(i, active));
                var ctsProperty = (AttachedProperty<CancellationTokenSource?>)typeof(AsyncImageHelper)
                    .GetField("CurrentLoadCtsProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var pendingCts = pending.GetValue(ctsProperty);
                Assert.NotNull(pendingCts);
                image.LoadDelayMs = 30;
                image.Url = final;
                Assert.True(pendingCts.IsCancellationRequested);
                Assert.Null(AsyncImageHelper.GetUrl(pending));
                Assert.Same(validBitmap, active.Source);
                Assert.True(AsyncImageHelper.GetIsLoaded(active));
            }
            finally { gate.Release(); }
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var target = image.Children.OfType<Image>().Single(i => AsyncImageHelper.GetUrl(i) == final);
            Assert.Equal(new PixelSize(8, 16), ((Bitmap)target.Source!).PixelSize);
            image.Url = null;
            foreach (var path in new[] { first, skipped, final }) AsyncImageHelper.InvalidateCache(path);
        });
        Assert.False(await AsyncImageHelper.SaveCachedImageAsync(skipped, temp.GetPath("unexpected.png")));
    }

    [Fact]
    public async Task DelayedBurst_LoadsFinalImageAndClearReleasesUncachedPixels()
    {
        using var temp = new TemporaryDirectory();
        var final = temp.GetPath("final.png");
        WritePng(final, 16, 8);
        await OnDispatcher(dispatcher =>
        {
            var image = new CrossfadeImage { DecodeWidth = 8, DisableCache = true, LoadDelayMs = 30 };
            for (var i = 0; i < 100; i++) image.Url = temp.GetPath($"missing-{i}.png");
            image.Url = final;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var bitmap = (Bitmap)image.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded).Source!;
            Assert.Equal(new PixelSize(8, 4), bitmap.PixelSize);
            image.Url = null;
            using var output = new MemoryStream();
            Assert.ThrowsAny<Exception>(() => bitmap.Save(output, new PngBitmapEncoderOptions()));
        });
    }

    [Fact]
    public async Task TwoImageControls_ShareCachedPixelsAndClearIndependently()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("shared.png");
        WritePng(path, 16, 8);
        await OnDispatcher(dispatcher =>
        {
            var first = new Image();
            var second = new Image();
            AsyncImageHelper.SetUrl(first, path);
            AsyncImageHelper.SetUrl(second, path);
            PumpUntil(dispatcher, () => AsyncImageHelper.GetIsLoaded(first) && AsyncImageHelper.GetIsLoaded(second));
            var bitmap = (Bitmap)first.Source!;
            Assert.Same(bitmap, second.Source);
            AsyncImageHelper.InvalidateCache(path);
            AsyncImageHelper.SetUrl(first, null);
            using var output = new MemoryStream();
            bitmap.Save(output, new PngBitmapEncoderOptions());
            AsyncImageHelper.SetUrl(second, null);
            Assert.ThrowsAny<Exception>(() => bitmap.Save(output, new PngBitmapEncoderOptions()));
        });
    }

    [Fact]
    public async Task UncachedOutgoingLease_HidesAfterBothAnimatedOpacitiesAndReleasesOnClear()
    {
        using var temp = new TemporaryDirectory();
        var first = temp.GetPath("first.png");
        var next = temp.GetPath("next.png");
        WritePng(first, 16, 8);
        WritePng(next, 8, 16);
        await OnDispatcher(dispatcher =>
        {
            var image = new CrossfadeImage { DisableCache = true, FadeDurationMs = 30 };
            image.Url = first;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var outgoing = image.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var bitmap = (Bitmap)outgoing.Source!;
            var releases = TrackRelease(outgoing);
            image.Url = next;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var incoming = image.Children.OfType<Image>().Single(i => AsyncImageHelper.GetUrl(i) == next);

            // Model a render clock that has not completed when wall time expires.
            var outgoingAnimation = outgoing.SetValue(Visual.OpacityProperty, .25, BindingPriority.Animation);
            var incomingAnimation = incoming.SetValue(Visual.OpacityProperty, .75, BindingPriority.Animation);
            PumpFor(dispatcher, 90);
            Assert.Same(bitmap, outgoing.Source);
            Assert.Equal(0, releases());
            outgoingAnimation!.Dispose();
            incomingAnimation!.Dispose();
            PumpUntil(dispatcher, () => !outgoing.IsVisible);
            Assert.False(outgoing.IsVisible);
            Assert.Equal(0, releases());
            Assert.Same(bitmap, outgoing.Source);
            Assert.True(image.IsCurrentImageReady);
            Assert.Equal(new PixelSize(8, 16), ((Bitmap)incoming.Source!).PixelSize);
            image.Url = null;
            PumpFor(dispatcher, 60);
            Assert.Equal(1, releases());
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RapidReverseAndReturn_ObsoleteFadeCannotReleaseReactivatedImage(bool uncached)
    {
        using var temp = new TemporaryDirectory();
        var first = temp.GetPath("first.png");
        var second = temp.GetPath("second.png");
        WritePng(first, 16, 8);
        WritePng(second, 8, 16);
        await OnDispatcher(dispatcher =>
        {
            var image = new CrossfadeImage { DisableCache = uncached, FadeDurationMs = 40, FadeOutOnClear = true };
            image.Url = first;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var original = image.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var bitmap = (Bitmap)original.Source!;
            var releases = TrackRelease(original);
            for (var i = 0; i < 8; i++)
            {
                image.Url = second;
                PumpUntil(dispatcher, () => image.IsCurrentImageReady);
                image.Url = first;
                PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            }
            PumpFor(dispatcher, 120);
            Assert.Same(bitmap, original.Source);
            Assert.True(original.IsVisible);
            Assert.Equal(0, releases());
            using var output = new MemoryStream();
            bitmap.Save(output, new PngBitmapEncoderOptions());
            Assert.Equal(2, image.Children.OfType<Image>().Count(AsyncImageHelper.GetIsLoaded));
            image.Url = null;
            image.Url = first; // Supersede clear's continuation with a new active consumer.
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var reactivated = image.Children.OfType<Image>().Single(i => AsyncImageHelper.GetUrl(i) == first && !ReferenceEquals(i, original));
            var reactivatedBitmap = (Bitmap)reactivated.Source!;
            PumpFor(dispatcher, 100);
            Assert.Same(reactivatedBitmap, reactivated.Source);
            reactivatedBitmap.Save(output, new PngBitmapEncoderOptions());
            image.FadeOutOnClear = false;
            image.Url = null;
            Assert.Equal(1, releases());
            AsyncImageHelper.InvalidateCache(first);
            AsyncImageHelper.InvalidateCache(second);
            AssertDisposed(bitmap);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptReplacement_RetainsBoundedPreviousImageUntilClear(bool corrupt)
    {
        using var temp = new TemporaryDirectory();
        var first = temp.GetPath("first.png");
        var broken = temp.GetPath("broken.png");
        WritePng(first, 16, 8);
        if (corrupt) File.WriteAllText(broken, "not an image");
        await OnDispatcher(dispatcher =>
        {
            var image = new CrossfadeImage { DisableCache = true, FadeDurationMs = 25 };
            image.Url = first;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var original = image.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var bitmap = (Bitmap)original.Source!;
            var releases = TrackRelease(original);
            image.Url = broken;
            PumpUntil(dispatcher, () => original.Opacity == 0);
            Assert.Equal(0, releases());
            Assert.Same(bitmap, original.Source);
            Assert.False(image.IsCurrentImageReady);
            image.Url = first; // Return without another decode of the previous title.
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            Assert.Same(bitmap, original.Source);
            image.Url = null;
            Assert.Equal(1, releases());
            AssertDisposed(bitmap);
        });
    }

    [Fact]
    public async Task SharedFallbackAndIdenticalUrl_ClearReleasesOnlyThisConsumer()
    {
        using var temp = new TemporaryDirectory();
        var shared = temp.GetPath("shared.png");
        var next = temp.GetPath("next.png");
        WritePng(shared, 16, 8);
        WritePng(next, 8, 16);
        await OnDispatcher(dispatcher =>
        {
            var first = new CrossfadeImage { FadeDurationMs = 25 };
            var second = new CrossfadeImage { FadeDurationMs = 25 };
            first.Url = shared;
            second.Url = shared;
            PumpUntil(dispatcher, () => first.IsCurrentImageReady && second.IsCurrentImageReady);
            var outgoing = first.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var other = second.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var bitmap = (Bitmap)outgoing.Source!;
            Assert.Same(bitmap, other.Source);
            var releases = TrackRelease(outgoing);
            first.Url = shared;
            PumpFor(dispatcher, 60);
            Assert.Equal(0, releases());
            AsyncImageHelper.InvalidateCache(shared);
            first.Url = next;
            PumpUntil(dispatcher, () => first.IsCurrentImageReady && !outgoing.IsVisible);
            Assert.Equal(0, releases());
            using var output = new MemoryStream();
            bitmap.Save(output, new PngBitmapEncoderOptions());
            first.Url = null;
            Assert.Equal(1, releases());
            bitmap.Save(output, new PngBitmapEncoderOptions());
            second.Url = null;
            AssertDisposed(bitmap);
            AsyncImageHelper.InvalidateCache(next);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettledReturn_ReusesPreviousPixelsWithoutWaitingForDecode(bool uncached)
    {
        using var temp = new TemporaryDirectory();
        var first = temp.GetPath("first.png");
        var second = temp.GetPath("second.png");
        WritePng(first, 16, 8);
        WritePng(second, 8, 16);
        await OnDispatcher(dispatcher =>
        {
            var image = new CrossfadeImage { DisableCache = uncached, FadeDurationMs = 25 };
            AsyncImageHelper.SetUseSerialDecode(image, true);
            image.Url = first;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            var original = image.Children.OfType<Image>().Single(AsyncImageHelper.GetIsLoaded);
            var bitmap = original.Source;
            image.Url = second;
            PumpUntil(dispatcher, () => image.IsCurrentImageReady);
            PumpUntil(dispatcher, () => !original.IsVisible);
            var gate = (SemaphoreSlim)typeof(AsyncImageHelper)
                .GetField("SerialDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            gate.Wait();
            try
            {
                image.Url = first;
                PumpUntil(dispatcher, () => image.IsCurrentImageReady);
                Assert.Same(bitmap, original.Source);
                Assert.True(original.IsVisible);
            }
            finally { gate.Release(); }
            image.Url = null;
            AsyncImageHelper.InvalidateCache(first);
            AsyncImageHelper.InvalidateCache(second);
        });
    }

    private static Func<int> TrackRelease(Image image)
    {
        var property = (AttachedProperty<AsyncImageHelper.BitmapLease?>)typeof(AsyncImageHelper)
            .GetField("CurrentBitmapLeaseProperty", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var original = image.GetValue(property)!;
        var releases = 0;
        image.SetValue(property, new AsyncImageHelper.BitmapLease(original.Bitmap, () =>
        {
            releases++;
            original.Dispose();
        }));
        return () => releases;
    }

    private static void AssertDisposed(Bitmap bitmap)
    {
        using var output = new MemoryStream();
        Assert.ThrowsAny<Exception>(() => bitmap.Save(output, new PngBitmapEncoderOptions()));
    }

    private static void PumpFor(Dispatcher dispatcher, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            dispatcher.RunJobs();
            Thread.Sleep(1);
        }
    }

    private static Task OnDispatcher(Action<Dispatcher> action)
        => AvaloniaTestDispatcher.InvokeAsync(action).WaitAsync(TimeSpan.FromSeconds(15));

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Image did not become ready.");
            dispatcher.RunJobs();
            Thread.Sleep(1);
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, encoded.ToArray());
    }
}
