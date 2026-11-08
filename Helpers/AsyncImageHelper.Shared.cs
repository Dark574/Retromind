using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Retromind.Services;

namespace Retromind.Helpers;

public partial class AsyncImageHelper
{
    private static readonly Dictionary<string, SharedLoad> SharedLoads = new(StringComparer.Ordinal);

    internal sealed class BitmapLease(Bitmap bitmap, Action release) : IDisposable
    {
        private Action? _release = release;
        public Bitmap Bitmap { get; } = bitmap;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    // The operation holds one reference until every waiter has either acquired
    // its own lease or canceled. This also works when a full cache rejects the bitmap.
    private sealed class SharedBitmap(Bitmap bitmap, Action release) : IDisposable
    {
        private int _references = 1;
        public BitmapLease Acquire()
        {
            Interlocked.Increment(ref _references);
            return new BitmapLease(bitmap, Dispose);
        }
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _references) == 0) release();
        }
    }

    private sealed class SharedLoad
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<SharedBitmap?> Task { get; set; } = null!;
        public int Consumers { get; set; }
        public bool Invalidated { get; set; }
    }

    internal static async Task<BitmapLease?> AcquireBitmapAsync(
        string source, int? decodeWidth, bool useSerialDecode, CancellationToken cancellationToken,
        HttpClient? httpClient = null, IErrorService? errorService = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = decodeWidth.HasValue ? $"{source}_{decodeWidth}" : source;
        SharedLoad request;
        lock (CacheLock)
        {
            var cached = TryAcquireFromCache(key);
            if (cached != null)
                return new BitmapLease(cached.Bitmap, cached.Dispose);

            if (!SharedLoads.TryGetValue(key, out request!) || request.Invalidated || request.Consumers == 0)
            {
                // Let a canceled native codec finish before a replacement starts.
                // Native decoding cannot be interrupted safely once entered.
                var predecessor = request?.Task;
                request = new SharedLoad();
                var created = request;
                SharedLoads[key] = request;
                request.Task = Task.Run(() => LoadSharedAsync(
                    created, predecessor, key, source, decodeWidth, useSerialDecode, httpClient, errorService));
            }
            request.Consumers++;
        }

        try
        {
            var result = await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result?.Acquire();
        }
        finally
        {
            ReleaseSharedConsumer(key, request);
        }
    }

    private static async Task<SharedBitmap?> LoadSharedAsync(
        SharedLoad request, Task<SharedBitmap?>? predecessor, string key, string source,
        int? decodeWidth, bool useSerialDecode, HttpClient? httpClient, IErrorService? errorService)
    {
        if (predecessor != null)
            await predecessor.ConfigureAwait(false);

        var bitmap = await LoadBitmapAsync(source, decodeWidth, useSerialDecode,
            request.Cancellation.Token, httpClient, errorService).ConfigureAwait(false);
        if (bitmap == null) return null;

        lock (CacheLock)
        {
            if (request.Cancellation.IsCancellationRequested)
            {
                bitmap.Dispose();
                return null;
            }

            if (request.Invalidated)
                return new SharedBitmap(bitmap, bitmap.Dispose);

            var result = AddToCache(key, bitmap);
            return result.AssignmentLease is { } lease
                ? new SharedBitmap(result.Bitmap, lease.Dispose)
                : new SharedBitmap(result.Bitmap, result.Bitmap.Dispose);
        }
    }

    private static void ReleaseSharedConsumer(string key, SharedLoad request)
    {
        lock (CacheLock)
        {
            if (--request.Consumers != 0) return;
        }

        request.Cancellation.Cancel();
        _ = request.Task.ContinueWith(completed =>
        {
            // No consumer can join an abandoned request. A replacement may already
            // occupy its key, and must not be removed by this completion.
            lock (CacheLock)
            {
                if (SharedLoads.TryGetValue(key, out var current) && ReferenceEquals(current, request))
                    SharedLoads.Remove(key);
            }
            if (completed.IsCompletedSuccessfully) completed.Result?.Dispose();
            request.Cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static async Task<BitmapLease?> LoadUncachedAsync(
        string source, int? width, bool serial, CancellationToken cancellationToken)
    {
        var bitmap = await Task.Run(() => LoadBitmapAsync(source, width, serial, cancellationToken), cancellationToken);
        return bitmap == null ? null : new BitmapLease(bitmap, bitmap.Dispose);
    }
}
