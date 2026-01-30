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

    private const int MaxCacheSize = 250;

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
    private static readonly HashSet<string> InvalidatedKeys = new(StringComparer.Ordinal);
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();
    
    // Transparent 1x1 fallback to avoid null Image.Source crashes during measure.
    private static readonly IImage PlaceholderImage = CreatePlaceholderImage();

    static AsyncImageHelper()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = args.NewValue as string;
            var width = GetDecodeWidth(image);
            _ = LoadImageAsync(image, url, width);
        });

        DecodeWidthProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            EnsureDetachHandler(image);
            var url = GetUrl(image);
            var width = args.NewValue as int?;
            _ = LoadImageAsync(image, url, width);
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

    // Private property to store the Cancellation Token Source for the current load operation on this Image control
    private static readonly AttachedProperty<CancellationTokenSource?> CurrentLoadCtsProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, CancellationTokenSource?>("CurrentLoadCts");

    // Private property to track the current cache key used by an Image control
    private static readonly AttachedProperty<string?> CurrentCacheKeyProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, string?>("CurrentCacheKey");

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
        image.Source = PlaceholderImage;
        image.SetValue(CurrentLoadCtsProperty, null);
    }
    
    private static async Task LoadImageAsync(Image image, string? url, int? decodeWidth)
    {
        ResetImage(image);

        if (string.IsNullOrEmpty(url)) return;

        var cts = new CancellationTokenSource();
        image.SetValue(CurrentLoadCtsProperty, cts);

        var cacheKey = decodeWidth.HasValue ? $"{url}_{decodeWidth}" : url;
        var cachedBitmap = GetFromCache(cacheKey);

        if (cachedBitmap != null)
        {
            UiThreadHelper.Post(() =>
            {
                if (image.GetValue(CurrentLoadCtsProperty) != cts) return;
                AssignImageSource(image, cachedBitmap, cacheKey);
            });
            return;
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
                    byte[]? data;
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        data = await http.GetByteArrayAsync(url, token);
                    }
                    else
                    {
                        if (!File.Exists(url)) return null;
                        data = await File.ReadAllBytesAsync(url, token);
                    }

                    using var stream = new MemoryStream(data);
                    return DecodeBitmap(stream, decodeWidth);
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

            AddToCache(cacheKey, loadedBitmap);

            UiThreadHelper.Post(() =>
            {
                if (image.GetValue(CurrentLoadCtsProperty) != cts) return;
                AssignImageSource(image, loadedBitmap, cacheKey);
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

    private static Bitmap DecodeBitmap(Stream stream, int? decodeWidth)
    {
        stream.Position = 0;
        if (decodeWidth is > 0)
        {
            return Bitmap.DecodeToWidth(stream, decodeWidth.Value);
        }
        return new Bitmap(stream);
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
    
    private static Bitmap? GetFromCache(string key)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                if (InvalidatedKeys.Contains(key))
                    return null;

                LruList.Remove(entry.Node);
                LruList.AddLast(entry.Node); // Move to MRU position
                return entry.Bitmap;
            }
        }
        return null;
    }

    private static Bitmap? GetAnyFromCacheByUrl(string url)
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

                    return Cache.TryGetValue(key, out var entry) ? entry.Bitmap : null;
                }
            }

            return null;
        }
    }
    
    private static void AddToCache(string key, Bitmap bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(key)) return;

            if (Cache.Count >= MaxCacheSize)
            {
                TryEvictOne();
            }

            InvalidatedKeys.Remove(key);

            var node = LruList.AddLast(key);
            Cache[key] = (bitmap, node);
        }
    }

    private static void AssignImageSource(Image image, Bitmap bitmap, string cacheKey)
    {
        var oldKey = image.GetValue(CurrentCacheKeyProperty);
        if (!string.Equals(oldKey, cacheKey, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(oldKey))
                ReleaseImageCacheKey(image);

            IncrementCacheRef(cacheKey);
            image.SetValue(CurrentCacheKeyProperty, cacheKey);
        }

        image.Source = bitmap;
    }

    private static void ReleaseImageCacheKey(Image image)
    {
        var oldKey = image.GetValue(CurrentCacheKeyProperty);
        if (string.IsNullOrEmpty(oldKey))
            return;

        image.SetValue(CurrentCacheKeyProperty, null);
        DecrementCacheRef(oldKey);
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

    private static void TryEvictOne()
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
            }

            break;
        }
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
        _ = LoadImageAsync(image, url, width);
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
        var bitmap = GetAnyFromCacheByUrl(url);
        if (bitmap == null) return false;

        return await Task.Run(() =>
        {
            try
            {
                bitmap.Save(destinationPath);
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
