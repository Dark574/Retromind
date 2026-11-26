using System.Diagnostics;
using System.Reflection;
using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed partial class AsyncImageHelperTests
{
    [Fact]
    public async Task CancelledPreload_DoesNotCancelVisibleOrOverlappingPreload()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("shared.png");
        await File.WriteAllBytesAsync(path, CreatePng(16, 8));
        var gate = (SemaphoreSlim)typeof(AsyncImageHelper)
            .GetField("SerialDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        using var old = new CancellationTokenSource();
        using var next = new CancellationTokenSource();
        await gate.WaitAsync();
        Task<AsyncImageHelper.BitmapLease?> visible;
        Task preload;
        Task replacement;
        try
        {
            preload = AsyncImageHelper.PreloadLocalAsync(path, 8, true, old.Token);
            replacement = AsyncImageHelper.PreloadLocalAsync(path, 8, true, next.Token);
            visible = AsyncImageHelper.AcquireBitmapAsync(path, 8, true, default);
            old.Cancel();
            await preload.WaitAsync(TestTimeout);
            Assert.False(visible.IsCompleted);
            Assert.False(replacement.IsCompleted);
        }
        finally { gate.Release(); }
        try
        {
            using var lease = await visible.WaitAsync(TestTimeout);
            await replacement.WaitAsync(TestTimeout);
            using var cached = await AsyncImageHelper.AcquireBitmapAsync(path, 8, true, default);
            Assert.NotNull(lease);
            Assert.Same(lease.Bitmap, cached!.Bitmap);
        }
        finally { AsyncImageHelper.InvalidateCache(path); }
    }

    [Fact]
    public async Task PreloadBurst_BehindBusyDecoder()
    {
        using var temp = new TemporaryDirectory();
        var paths = Enumerable.Range(0, 300).Select(i => temp.GetPath($"logo-{i}.png")).ToArray();
        var png = CreatePng(512, 256);
        foreach (var path in paths) await File.WriteAllBytesAsync(path, png);
        var gate = (SemaphoreSlim)typeof(AsyncImageHelper)
            .GetField("SerialDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var loads = (System.Collections.IDictionary)typeof(AsyncImageHelper)
            .GetField("SharedLoads", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var cacheLock = typeof(AsyncImageHelper).GetField("CacheLock", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var baselineBytes = AsyncImageHelper.GetCacheUsage().Bytes;
        var requests = new List<Task>();
        CancellationTokenSource? previous = null;
        Task[] operations;
        await gate.WaitAsync();
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var path in paths)
            {
                var current = new CancellationTokenSource();
                requests.Add(AsyncImageHelper.PreloadLocalAsync(path, 256, true, current.Token));
                previous?.Cancel();
                previous?.Dispose();
                previous = current;
            }
            lock (cacheLock)
                operations = loads.Values.Cast<object>().Select(load => (Task)load.GetType().GetProperty("Task")!.GetValue(load)!).ToArray();
        }
        finally { gate.Release(); }
        try
        {
            await Task.WhenAll(requests.Concat(operations)).WaitAsync(TestTimeout);
            watch.Stop();
            var usage = AsyncImageHelper.GetCacheUsage();
            System.Console.WriteLine($"Preload burst: requests={requests.Count}; completed={requests.Count(task => task.IsCompletedSuccessfully)}; " +
                $"cache delta={usage.Bytes - baselineBytes}B; entries={usage.Count}; elapsed={watch.Elapsed.TotalMilliseconds:F1}ms");
            Assert.InRange(usage.Bytes, 0, AsyncImageHelper.MaxCacheBytes);
            Assert.Equal(256 * 128 * 4, usage.Bytes - baselineBytes);
        }
        finally
        {
            previous?.Dispose();
            foreach (var path in paths) AsyncImageHelper.InvalidateCache(path);
        }
    }
}
