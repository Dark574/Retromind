using Avalonia;
using Retromind.Helpers;

namespace Retromind.Tests.Helpers;

public sealed partial class AsyncImageHelperTests
{
    [Fact]
    public async Task ByteBudget_PinnedEntriesRejectAdmissionWithoutGrowthAndReleaseOnInvalidation()
    {
        const int side = 1024;
        const long bytesPerImage = side * side * 4L;
        var encoded = CreatePng(side, side);
        var sources = new List<string>();
        var held = new List<AsyncImageHelper.BitmapLease>();
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(Response(new TrackingStream(encoded)))));
        try
        {
            for (var i = 0; i < AsyncImageHelper.MaxCacheBytes / bytesPerImage; i++)
            {
                var source = UniqueUrl();
                sources.Add(source);
                held.Add((await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, client))!);
                Assert.InRange(AsyncImageHelper.GetCacheUsage().Bytes, 0, AsyncImageHelper.MaxCacheBytes);
            }
            Assert.Equal(AsyncImageHelper.MaxCacheBytes, AsyncImageHelper.GetCacheUsage().Bytes);
            var overflowSource = UniqueUrl();
            using var overflow = await AsyncImageHelper.AcquireBitmapAsync(overflowSource, null, false, default, client);
            Assert.NotNull(overflow);
            Assert.Equal(AsyncImageHelper.MaxCacheBytes, AsyncImageHelper.GetCacheUsage().Bytes);
            overflow.Dispose();
            AssertBitmapDisposed(overflow.Bitmap);

            // Invalidating an in-use image preserves its pixels and budget charge.
            AsyncImageHelper.InvalidateCache(sources[0]);
            Assert.Equal(AsyncImageHelper.MaxCacheBytes, AsyncImageHelper.GetCacheUsage().Bytes);
            held[0].Dispose();
            Assert.Equal(AsyncImageHelper.MaxCacheBytes - bytesPerImage, AsyncImageHelper.GetCacheUsage().Bytes);
            AssertBitmapDisposed(held[0].Bitmap);

            sources.Add(overflowSource);
            held.Add((await AsyncImageHelper.AcquireBitmapAsync(overflowSource, null, false, default, client))!);
            Assert.Equal(AsyncImageHelper.MaxCacheBytes, AsyncImageHelper.GetCacheUsage().Bytes);
        }
        finally
        {
            foreach (var source in sources) AsyncImageHelper.InvalidateCache(source);
            foreach (var lease in held) lease.Dispose();
        }
        Assert.Equal(0, AsyncImageHelper.GetCacheUsage().Bytes);
    }

    [Fact]
    public async Task ByteBudget_EvictsLeastRecentlyUsedUnpinnedImagesWhileKeepingHits()
    {
        var sources = new List<string>();
        var encoded = CreatePng(1024, 1024);
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(Response(new TrackingStream(encoded)));
        }));
        try
        {
            for (var i = 0; i < 32; i++)
            {
                var source = UniqueUrl();
                sources.Add(source);
                using var loaded = await AsyncImageHelper.AcquireBitmapAsync(source, null, false, default, client);
            }
            using var touched = await AsyncImageHelper.AcquireBitmapAsync(sources[0], null, false, default, client);
            var newest = UniqueUrl();
            sources.Add(newest);
            using var extra = await AsyncImageHelper.AcquireBitmapAsync(newest, null, false, default, client);
            Assert.Equal(33, requests);
            using var hit = await AsyncImageHelper.AcquireBitmapAsync(sources[0], null, false, default, client);
            Assert.Equal(33, requests);
            Assert.Same(touched!.Bitmap, hit!.Bitmap);
            using var evicted = await AsyncImageHelper.AcquireBitmapAsync(sources[1], null, false, default, client);
            Assert.Equal(34, requests);
            Assert.InRange(AsyncImageHelper.GetCacheUsage().Bytes, 0, AsyncImageHelper.MaxCacheBytes);
            Assert.Equal(new PixelSize(1024, 1024), evicted!.Bitmap.PixelSize);
        }
        finally
        {
            foreach (var source in sources) AsyncImageHelper.InvalidateCache(source);
        }
    }
}
