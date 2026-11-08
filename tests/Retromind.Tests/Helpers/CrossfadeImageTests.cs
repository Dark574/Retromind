using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
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
