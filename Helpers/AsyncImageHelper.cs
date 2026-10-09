using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to load images asynchronously from URLs or local paths.
/// Features an LRU (Least Recently Used) Cache, Downsampling, and Task Cancellation.
/// </summary>
public class AsyncImageHelper : AvaloniaObject
{
    // --- Configuration ---

    private const int MaxCacheSize = 200;

    // Skia bitmap decoding and resizing is CPU-heavy and cannot be interrupted
    // once it has entered the native codec. Serializing that short section keeps
    // rapid carousel navigation responsive: superseded requests can be cancelled
    // while waiting instead of decoding several obsolete images in parallel.
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);

    // Shared HttpClient to prevent socket exhaustion (used only if DI is not available)
    private static readonly HttpClient FallbackHttpClient = new();

    private static HttpClient GetHttpClient()
    {
        var svc = Retromind.App.Current?.Services;
        if (svc != null && svc.GetService(typeof(HttpClient)) is HttpClient client)
            return client;

        return FallbackHttpClient;
    }

    // --- Cache State ---
    private static readonly Dictionary<string, (Bitmap Bitmap, LinkedListNode<string> Node)> Cache = new();
    private static readonly Dictionary<string, int> CacheRefCounts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task> PreloadTasks = new(StringComparer.Ordinal);
    private static readonly HashSet<string> InvalidatedKeys = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();

    private readonly record struct CacheAddResult(
        Bitmap Bitmap,
        bool IsCached,
        CacheLease? AssignmentLease);

    private sealed class CacheLease : IDisposable
    {
        private string? _key;

        public CacheLease(string key, Bitmap bitmap)
        {
            _key = key;
            Bitmap = bitmap;
        }

        public Bitmap Bitmap { get; }

        public void Dispose()
        {
            var key = Interlocked.Exchange(ref _key, null);
            if (key != null)
                DecrementCacheRef(key);
        }
    }
    
    // Transparent 1x1 fallback to avoid null Image.Source crashes during measure.
    private static readonly IImage PlaceholderImage = CreatePlaceholderImage();

    static AsyncImageHelper()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = args.NewValue as string;
            var width = GetDecodeWidth(image);
            var disableCache = GetDisableCache(image);
            _ = LoadImageAsync(image, url, width, disableCache);
        });

        DecodeWidthProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = GetUrl(image);
            var width = args.NewValue as int?;
            var disableCache = GetDisableCache(image);
            _ = LoadImageAsync(image, url, width, disableCache);
        });

        DisableCacheProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = GetUrl(image);
            var width = GetDecodeWidth(image);
            var disableCache = args.NewValue is bool b && b;
            _ = LoadImageAsync(image, url, width, disableCache);
        });
    }

    // --- Attached Properties ---

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, string?>("Url");

    public static string? GetUrl(Image element) => element.GetValue(UrlProperty);
    public static void SetUrl(Image element, string? value) => element.SetValue(UrlProperty, value);

    public static readonly AttachedProperty<int?> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, int?>("DecodeWidth");

    public static int? GetDecodeWidth(Image element) => element.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image element, int? value) => element.SetValue(DecodeWidthProperty, value);

    public static readonly AttachedProperty<bool> DisableCacheProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, bool>("DisableCache");

    public static bool GetDisableCache(Image element) => element.GetValue(DisableCacheProperty);
    public static void SetDisableCache(Image element, bool value) => element.SetValue(DisableCacheProperty, value);

    public static readonly AttachedProperty<bool> IsLoadedProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, bool>("IsLoaded");

    public static bool GetIsLoaded(Image element) => element.GetValue(IsLoadedProperty);
    public static void SetIsLoaded(Image element, bool value) => element.SetValue(IsLoadedProperty, value);

    // Private property to store the Cancellation Token Source for the current load operation on this Image control
    private static readonly AttachedProperty<CancellationTokenSource?> CurrentLoadCtsProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, CancellationTokenSource?>("CurrentLoadCts");

    // Private property to track the current cache key used by an Image control
    private static readonly AttachedProperty<string?> CurrentCacheKeyProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, string?>("CurrentCacheKey");

    // Tracks the current uncached bitmap to dispose it when replaced.
    private static readonly AttachedProperty<Bitmap?> CurrentUncachedBitmapProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, Bitmap?>("CurrentUncachedBitmap");

    // Tracks whether we've attached a Detach handler for this Image instance
    private static readonly AttachedProperty<bool> DetachHandlerAttachedProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, bool>("DetachHandlerAttached");

    // --- Core Logic ---

    /// <summary>
    /// Resets the image source and cancels any ongoing load operation.
    /// </summary>
    private static void ResetImage(Image image)
    {
        var oldCts = image.GetValue(CurrentLoadCtsProperty);
        oldCts?.Cancel();
        oldCts?.Dispose();
        ReleaseImageCacheKey(image);
        DisposeUncachedBitmap(image);
        image.Source = PlaceholderImage;
        image.SetValue(IsLoadedProperty, false);
        image.SetValue(CurrentLoadCtsProperty, null);
    }
    
    private static async Task LoadImageAsync(Image image, string? url, int? decodeWidth, bool disableCache)
    {
        ResetImage(image);

        if (string.IsNullOrEmpty(url)) return;

        var cts = new CancellationTokenSource();
        image.SetValue(CurrentLoadCtsProperty, cts);

        string? cacheKey = null;
        if (!disableCache)
        {
            cacheKey = decodeWidth.HasValue ? $"{url}_{decodeWidth}" : url;
            if (TryAssignFromCache(image, cts, cacheKey))
                return;

            // A theme may already be warming this exact image just outside its
            // visible carousel window. Reuse that decode instead of starting a
            // duplicate one when the item becomes visible.
            var preloadTask = TryGetPreloadTask(cacheKey);
            if (preloadTask != null)
            {
                await preloadTask.ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                    return;
                if (TryAssignFromCache(image, cts, cacheKey))
                    return;
            }
        }

        try
        {
            var token = cts.Token;
            var http = GetHttpClient();

            var loadedBitmap = await Task.Run(async () =>
            {
                if (token.IsCancellationRequested) return null;

                try
                {
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        using var response = await http.GetAsync(
                            url,
                            HttpCompletionOption.ResponseHeadersRead,
                            token);
                        response.EnsureSuccessStatusCode();

                        await using var networkStream = await response.Content.ReadAsStreamAsync(token);
                        using var bufferedStream = new MemoryStream();
                        await networkStream.CopyToAsync(bufferedStream, token);
                        return await DecodeBitmapUnlessCancelledAsync(
                                bufferedStream,
                                decodeWidth,
                                token)
                            .ConfigureAwait(false);
                    }

                    if (!File.Exists(url)) return null;

                    await using var fileStream = new FileStream(
                        url,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    return await DecodeBitmapUnlessCancelledAsync(
                            fileStream,
                            decodeWidth,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AsyncImageHelper] Load error: {ex.Message}");
                    return null;
                }
            }, token);

            if (loadedBitmap == null) return;

            var bitmapToAssign = loadedBitmap;
            var isCached = false;
            CacheLease? assignmentLease = null;
            if (!disableCache && cacheKey != null)
            {
                var cacheResult = AddToCache(cacheKey, loadedBitmap);
                bitmapToAssign = cacheResult.Bitmap;
                isCached = cacheResult.IsCached;
                assignmentLease = cacheResult.AssignmentLease;
            }

            UiThreadHelper.Post(() =>
            {
                try
                {
                    if (image.GetValue(CurrentLoadCtsProperty) != cts)
                    {
                        if (!isCached)
                            bitmapToAssign.Dispose();
                        return;
                    }

                    AssignImageSource(
                        image,
                        bitmapToAssign,
                        isCached ? cacheKey : null,
                        disableCache: !isCached);
                }
                finally
                {
                    assignmentLease?.Dispose();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncImageHelper] Critical error: {ex.Message}");
        }
    }

    /// <summary>
    /// Warms the shared cache for local artwork without creating a visual. Calls
    /// for the same cache key share one decode operation.
    /// </summary>
    public static Task PreloadLocalAsync(string? path, int? decodeWidth = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.CompletedTask;

        var cacheKey = decodeWidth.HasValue ? $"{path}_{decodeWidth}" : path;
        lock (CacheLock)
        {
            if (Cache.ContainsKey(cacheKey) && !InvalidatedKeys.Contains(cacheKey))
                return Task.CompletedTask;

            if (PreloadTasks.TryGetValue(cacheKey, out var existingTask))
                return existingTask;

            var preloadTask = Task.Run(() => PreloadLocalCoreAsync(path, cacheKey, decodeWidth));
            PreloadTasks[cacheKey] = preloadTask;
            _ = preloadTask.ContinueWith(
                completedTask =>
                {
                    lock (CacheLock)
                    {
                        if (PreloadTasks.TryGetValue(cacheKey, out var currentTask) &&
                            ReferenceEquals(currentTask, completedTask))
                        {
                            PreloadTasks.Remove(cacheKey);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return preloadTask;
        }
    }

    private static async Task PreloadLocalCoreAsync(string path, string cacheKey, int? decodeWidth)
    {
        try
        {
            if (!File.Exists(path))
                return;

            await using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bitmap = await DecodeBitmapUnlessCancelledAsync(
                    fileStream,
                    decodeWidth,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (bitmap == null)
                return;

            var cacheResult = AddToCache(cacheKey, bitmap);
            cacheResult.AssignmentLease?.Dispose();
            if (!cacheResult.IsCached)
                cacheResult.Bitmap.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncImageHelper] Preload error: {ex.Message}");
        }
    }

    private static Task? TryGetPreloadTask(string cacheKey)
    {
        lock (CacheLock)
            return PreloadTasks.TryGetValue(cacheKey, out var task) ? task : null;
    }

    private static bool TryAssignFromCache(
        Image image,
        CancellationTokenSource cts,
        string cacheKey)
    {
        var cachedLease = TryAcquireFromCache(cacheKey);
        if (cachedLease == null)
            return false;

        UiThreadHelper.Post(() =>
        {
            try
            {
                if (image.GetValue(CurrentLoadCtsProperty) != cts)
                    return;

                AssignImageSource(image, cachedLease.Bitmap, cacheKey, disableCache: false);
            }
            finally
            {
                cachedLease.Dispose();
            }
        });
        return true;
    }

    private static Bitmap DecodeBitmap(Stream stream, int? decodeWidth)
    {
        stream.Position = 0;
        if (decodeWidth is > 0)
        {
            return Bitmap.DecodeToWidth(stream, decodeWidth.Value);
        }
        return new Bitmap(stream);
    }

    private static Bitmap? DecodeBitmapUnlessCancelled(
        Stream stream,
        int? decodeWidth,
        CancellationToken cancellationToken)
    {
        var bitmap = DecodeBitmap(stream, decodeWidth);
        if (!cancellationToken.IsCancellationRequested)
            return bitmap;

        bitmap.Dispose();
        return null;
    }

    private static async Task<Bitmap?> DecodeBitmapUnlessCancelledAsync(
        Stream stream,
        int? decodeWidth,
        CancellationToken cancellationToken)
    {
        await DecodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DecodeBitmapUnlessCancelled(stream, decodeWidth, cancellationToken);
        }
        finally
        {
            DecodeGate.Release();
        }
    }
    
    private static IImage CreatePlaceholderImage()
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(1, 1),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bitmap.Lock())
        {
            unsafe
            {
                *((uint*)fb.Address) = 0; // transparent
            }
        }

        return bitmap;
    }

    // --- Cache Helpers ---

    public static void InvalidateCache(string url)
    {
        lock (CacheLock)
        {
            var keysToRemove = new List<string>();
            foreach (var key in Cache.Keys)
            {
                if (key == url || key.StartsWith(url + "_", StringComparison.Ordinal))
                    keysToRemove.Add(key);
            }

            foreach (var key in keysToRemove)
            {
                if (Cache.TryGetValue(key, out var entry))
                {
                    if (GetCacheRefCount(key) == 0)
                    {
                        LruList.Remove(entry.Node);
                        Cache.Remove(key);
                        InvalidatedKeys.Remove(key);
                        entry.Bitmap.Dispose();
                    }
                    else
                    {
                        InvalidatedKeys.Add(key);
                    }
                }
            }
        }
    }
    
    private static CacheLease? TryAcquireFromCache(string key)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                if (InvalidatedKeys.Contains(key))
                    return null;

                LruList.Remove(entry.Node);
                LruList.AddLast(entry.Node); // Move to MRU position
                IncrementCacheRef(key);
                return new CacheLease(key, entry.Bitmap);
            }
        }
        return null;
    }

    private static CacheLease? TryAcquireAnyFromCacheByUrl(string url)
    {
        lock (CacheLock)
        {
            // Take the most recently used variant (MRU) that matches the URL
            for (var node = LruList.Last; node != null; node = node.Previous)
            {
                var key = node.Value;
                if (key == url || key.StartsWith(url + "_", StringComparison.Ordinal))
                {
                    if (InvalidatedKeys.Contains(key))
                        continue;

                    if (!Cache.TryGetValue(key, out var entry))
                        continue;

                    LruList.Remove(entry.Node);
                    LruList.AddLast(entry.Node);
                    IncrementCacheRef(key);
                    return new CacheLease(key, entry.Bitmap);
                }
            }

            return null;
        }
    }
    
    private static CacheAddResult AddToCache(string key, Bitmap bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var existing))
            {
                if (InvalidatedKeys.Contains(key))
                    return new CacheAddResult(bitmap, IsCached: false, AssignmentLease: null);

                LruList.Remove(existing.Node);
                LruList.AddLast(existing.Node);
                bitmap.Dispose();
                IncrementCacheRef(key);
                return new CacheAddResult(
                    existing.Bitmap,
                    IsCached: true,
                    new CacheLease(key, existing.Bitmap));
            }

            while (Cache.Count >= MaxCacheSize)
            {
                if (!TryEvictOne())
                    return new CacheAddResult(bitmap, IsCached: false, AssignmentLease: null);
            }

            InvalidatedKeys.Remove(key);

            var node = LruList.AddLast(key);
            Cache[key] = (bitmap, node);
            IncrementCacheRef(key);
            return new CacheAddResult(
                bitmap,
                IsCached: true,
                new CacheLease(key, bitmap));
        }
    }

    private static void AssignImageSource(Image image, Bitmap bitmap, string? cacheKey, bool disableCache)
    {
        if (disableCache)
        {
            DisposeUncachedBitmap(image);
            image.Source = bitmap;
            image.SetValue(CurrentUncachedBitmapProperty, bitmap);
            image.SetValue(IsLoadedProperty, true);
            return;
        }

        DisposeUncachedBitmap(image);

        var oldKey = image.GetValue(CurrentCacheKeyProperty);
        if (!string.Equals(oldKey, cacheKey, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(oldKey))
                ReleaseImageCacheKey(image);

            if (!string.IsNullOrEmpty(cacheKey))
            {
                IncrementCacheRef(cacheKey);
                image.SetValue(CurrentCacheKeyProperty, cacheKey);
            }
        }

        image.Source = bitmap;
        image.SetValue(IsLoadedProperty, true);
    }

    private static void ReleaseImageCacheKey(Image image)
    {
        var oldKey = image.GetValue(CurrentCacheKeyProperty);
        if (string.IsNullOrEmpty(oldKey))
            return;

        image.SetValue(CurrentCacheKeyProperty, null);
        DecrementCacheRef(oldKey);
    }

    private static void DisposeUncachedBitmap(Image image)
    {
        var oldBitmap = image.GetValue(CurrentUncachedBitmapProperty);
        if (oldBitmap == null)
            return;

        image.SetValue(CurrentUncachedBitmapProperty, null);
        oldBitmap.Dispose();
    }

    private static int GetCacheRefCount(string key)
        => CacheRefCounts.TryGetValue(key, out var count) ? count : 0;

    private static void IncrementCacheRef(string key)
    {
        lock (CacheLock)
        {
            CacheRefCounts[key] = CacheRefCounts.TryGetValue(key, out var count)
                ? count + 1
                : 1;
        }
    }

    private static void DecrementCacheRef(string key)
    {
        lock (CacheLock)
        {
            if (!CacheRefCounts.TryGetValue(key, out var count))
                return;

            count--;
            if (count <= 0)
            {
                CacheRefCounts.Remove(key);

                if (InvalidatedKeys.Contains(key) && Cache.TryGetValue(key, out var entry))
                {
                    Cache.Remove(key);
                    LruList.Remove(entry.Node);
                    InvalidatedKeys.Remove(key);
                    entry.Bitmap.Dispose();
                }
            }
            else
            {
                CacheRefCounts[key] = count;
            }
        }
    }

    private static bool TryEvictOne()
    {
        var node = LruList.First;
        while (node != null)
        {
            var key = node.Value;
            var next = node.Next;

            if (GetCacheRefCount(key) > 0)
            {
                node = next;
                continue;
            }

            if (Cache.TryGetValue(key, out var entry))
            {
                Cache.Remove(key);
                LruList.Remove(node);
                InvalidatedKeys.Remove(key);
                entry.Bitmap.Dispose();
                return true;
            }

            LruList.Remove(node);
            node = next;
        }

        return false;
    }

    private static void EnsureDetachHandler(Image image)
    {
        if (image.GetValue(DetachHandlerAttachedProperty))
            return;

        image.SetValue(DetachHandlerAttachedProperty, true);
        image.AttachedToVisualTree += OnImageAttached;
        image.DetachedFromVisualTree += OnImageDetached;
    }

    private static void OnImageAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Image image)
            return;

        if (image.GetValue(CurrentLoadCtsProperty) != null)
            return;

        var url = GetUrl(image);
        if (string.IsNullOrEmpty(url))
            return;

        if (image.Source != null && !ReferenceEquals(image.Source, PlaceholderImage))
            return;

        var width = GetDecodeWidth(image);
        var disableCache = GetDisableCache(image);
        _ = LoadImageAsync(image, url, width, disableCache);
    }

    private static void OnImageDetached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (sender is Image image)
            ResetImage(image);
    }

    /// <summary>
    /// Saves a cached image to disk.
    /// </summary>
    public static async Task<bool> SaveCachedImageAsync(string url, string destinationPath)
    {
        using var lease = TryAcquireAnyFromCacheByUrl(url);
        if (lease == null) return false;

        return await Task.Run(() =>
        {
            try
            {
                lease.Bitmap.Save(destinationPath, new PngBitmapEncoderOptions());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AsyncImageHelper] Save error: {ex.Message}");
                return false;
            }
        });
    }
}
