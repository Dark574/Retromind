using System.Reflection;
using Avalonia;
using Avalonia.Media.Imaging;
using Retromind.Helpers;
using Retromind.Tests.TestInfrastructure;

namespace Retromind.Tests.Helpers;

public sealed partial class AsyncImageHelperTests
{
    [Fact]
    public async Task PreloadAndVisibleRequest_JoinTheSameOperation()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.GetPath("preload-shared.png");
        await File.WriteAllBytesAsync(path, CreatePng(16, 8));
        var gate = (SemaphoreSlim)typeof(AsyncImageHelper)
            .GetField("SerialDecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        await gate.WaitAsync();
        var preload = AsyncImageHelper.PreloadLocalAsync(path, 8, true);
        var visible = AsyncImageHelper.AcquireBitmapAsync(path, 8, true, default);
        try
        {
            var loads = (System.Collections.IDictionary)typeof(AsyncImageHelper)
                .GetField("SharedLoads", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var operation = loads[$"{path}_8"]!;
            Assert.Equal(2, operation.GetType().GetProperty("Consumers")!.GetValue(operation));
            Assert.False(preload.IsCompleted);
            Assert.False(visible.IsCompleted);
        }
        finally { gate.Release(); }
        using var lease = await visible.WaitAsync(TestTimeout);
        await preload.WaitAsync(TestTimeout);
        using var cached = await AsyncImageHelper.AcquireBitmapAsync(path, 8, true, default);
        Assert.Same(lease!.Bitmap, cached!.Bitmap);
        AsyncImageHelper.InvalidateCache(path);
    }

    [Fact]
    public async Task CacheableLoads_ThirtyTwoConsumersShareOneRequestAndBitmap()
    {
        var source = UniqueUrl();
        var requests = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            await release.Task.WaitAsync(token);
            return Response(new TrackingStream(CreatePng(16, 8)));
        }));
        var loads = Enumerable.Range(0, 32).Select(_ => AsyncImageHelper.AcquireBitmapAsync(source, 8, false, default, client)).ToArray();
        release.SetResult();
        var leases = await Task.WhenAll(loads).WaitAsync(TestTimeout);
        try
        {
            Assert.Equal(1, requests);
            Assert.All(leases, lease => Assert.Same(leases[0]!.Bitmap, lease!.Bitmap));
            Assert.Equal(new PixelSize(8, 4), leases[0]!.Bitmap.PixelSize);
            AsyncImageHelper.InvalidateCache(source);
            for (var i = 0; i < leases.Length - 1; i++) leases[i]!.Dispose();
            using var encoded = new MemoryStream();
            leases[^1]!.Bitmap.Save(encoded, new PngBitmapEncoderOptions()); // Other consumers' disposal must not invalidate it.
            Assert.True(encoded.Length > 0);
        }
        finally
        {
            AsyncImageHelper.InvalidateCache(source);
            foreach (var lease in leases) lease?.Dispose();
        }
        AssertBitmapDisposed(leases[0]!.Bitmap);
    }

    [Fact]
    public async Task CacheableLoads_ConsumerCancellationDoesNotCancelRemainingConsumer()
    {
        var source = UniqueUrl();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new BlockingStream(CreatePng(), true, release);
        using var client = CreateClient(stream);
        using var cancellation = new CancellationTokenSource();
        var first = AsyncImageHelper.AcquireBitmapAsync(source, null, false, cancellation.Token, client);
        var second = AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, client);
        try
        {
            await stream.Blocked.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(stream.IsDisposed);
            release.SetResult();
            using var survivor = await second.WaitAsync(TestTimeout);
            Assert.NotNull(survivor);
            Assert.True(stream.IsDisposed);
        }
        finally { release.TrySetResult(); AsyncImageHelper.InvalidateCache(source); }
    }

    [Fact]
    public async Task CacheableLoads_LastCancellationReleasesStreamAndAllowsRetry()
    {
        var source = UniqueUrl();
        var stream = new BlockingStream(CreatePng(), true);
        using var client = CreateClient(stream);
        using var cancellation = new CancellationTokenSource();
        var load = AsyncImageHelper.AcquireBitmapAsync(source, null, false, cancellation.Token, client);
        await stream.Blocked.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        using var retryClient = CreateClient(new TrackingStream(CreatePng(8, 16)));
        using var retry = await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, retryClient).WaitAsync(TestTimeout);
        Assert.True(stream.IsDisposed);
        Assert.Equal(new PixelSize(8, 16), retry!.Bitmap.PixelSize);
        AsyncImageHelper.InvalidateCache(source);
    }

    [Fact]
    public async Task CacheableLoads_InvalidationDuringLoadCannotPublishOldResultOrRemoveNewRequest()
    {
        var source = UniqueUrl();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new BlockingStream(CreatePng(16, 8), true, release);
        using var firstClient = CreateClient(stream);
        using var secondClient = CreateClient(new TrackingStream(CreatePng(8, 16)));
        var first = AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, firstClient);
        await stream.Blocked.Task.WaitAsync(TestTimeout);
        AsyncImageHelper.InvalidateCache(source);
        var second = AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, secondClient);
        release.SetResult();
        using var oldLease = await first.WaitAsync(TestTimeout);
        using var newLease = await second.WaitAsync(TestTimeout);
        try
        {
            Assert.Equal(new PixelSize(16, 8), oldLease!.Bitmap.PixelSize);
            Assert.Equal(new PixelSize(8, 16), newLease!.Bitmap.PixelSize);
            using var cached = await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default);
            Assert.Same(newLease.Bitmap, cached!.Bitmap);
        }
        finally { AsyncImageHelper.InvalidateCache(source); }
    }

    [Fact]
    public async Task CacheableLoads_InvalidatedPinnedBitmapRemainsUsableButIsNeverReassigned()
    {
        var source = UniqueUrl();
        using var firstClient = CreateClient(new TrackingStream(CreatePng(16, 8)));
        using var first = await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, firstClient);
        AsyncImageHelper.InvalidateCache(source);
        using var secondClient = CreateClient(new TrackingStream(CreatePng(8, 16)));
        using var second = await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, secondClient);
        Assert.NotSame(first!.Bitmap, second!.Bitmap);
        using var output = new MemoryStream();
        first.Bitmap.Save(output, new PngBitmapEncoderOptions());
        Assert.Equal(new PixelSize(8, 16), second.Bitmap.PixelSize);
        first.Dispose();
        AssertBitmapDisposed(first.Bitmap);
        second.Dispose();
        AssertBitmapDisposed(second.Bitmap); // Invalidated pinned key rejected cache admission.
        AsyncImageHelper.InvalidateCache(source);
    }

    [Fact]
    public async Task CacheableLoads_FullPinnedCacheSharesUncachedResultAndReleasesItOnce()
    {
        var held = new List<AsyncImageHelper.BitmapLease>();
        var sources = new List<string>();
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(Response(new TrackingStream(CreatePng())))));
        try
        {
            for (var i = 0; i < 200; i++)
            {
                var source = UniqueUrl();
                sources.Add(source);
                held.Add((await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, client))!);
            }
            var overflow = UniqueUrl();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var overflowClient = new HttpClient(new StubHandler(async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return Response(new TrackingStream(CreatePng()));
            }));
            var first = AsyncImageHelper.AcquireBitmapAsync(overflow, null, false, default, overflowClient);
            var second = AsyncImageHelper.AcquireBitmapAsync(overflow, null, false, default, overflowClient);
            release.SetResult();
            using var a = await first.WaitAsync(TestTimeout);
            using var b = await second.WaitAsync(TestTimeout);
            Assert.Same(a!.Bitmap, b!.Bitmap);
            a.Dispose();
            using var output = new MemoryStream();
            b.Bitmap.Save(output, new PngBitmapEncoderOptions());
            b.Dispose();
            AssertBitmapDisposed(b.Bitmap);
        }
        finally
        {
            foreach (var source in sources) AsyncImageHelper.InvalidateCache(source);
            foreach (var lease in held) lease.Dispose();
        }
    }

    [Fact]
    public async Task CacheableLoads_DifferentDecodeWidthsRemainIndependent()
    {
        var source = UniqueUrl();
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Response(new TrackingStream(CreatePng(16, 8))));
        }));
        try
        {
            using var small = await AsyncImageHelper.AcquireBitmapAsync(source, 4, false, default, client);
            using var large = await AsyncImageHelper.AcquireBitmapAsync(source, 8, false, default, client);
            Assert.Equal(2, requests);
            Assert.Equal(new PixelSize(4, 2), small!.Bitmap.PixelSize);
            Assert.Equal(new PixelSize(8, 4), large!.Bitmap.PixelSize);
        }
        finally { AsyncImageHelper.InvalidateCache(source); }
    }

    [Fact]
    public async Task CacheableLoads_FailureAndPreCancellationDoNotPoisonKey()
    {
        var source = UniqueUrl();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AsyncImageHelper.AcquireBitmapAsync(source, null, false, cancellation.Token));
        using var bad = CreateClient(new TrackingStream("invalid"u8.ToArray()));
        Assert.Null(await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, bad));
        using var good = CreateClient(new TrackingStream(CreatePng()));
        using var lease = await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, good);
        Assert.NotNull(lease);
        AsyncImageHelper.InvalidateCache(source);
    }

    private static void AssertBitmapDisposed(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        Assert.ThrowsAny<Exception>(() => bitmap.Save(stream, new PngBitmapEncoderOptions()));
    }
}
