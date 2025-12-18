using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to load images asynchronously from URLs or local paths.
/// Features an LRU (Least Recently Used) Cache, Downsampling, and Task Cancellation.
/// </summary>
public class AsyncImageHelper : AvaloniaObject
{
    // --- Configuration ---

    private const int MaxCacheSize = 500;

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
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();

    static AsyncImageHelper()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            var url = args.NewValue as string;
            var width = GetDecodeWidth(image);
            _ = LoadImageAsync(image, url, width);
        });

        DecodeWidthProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
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

    // --- Core Logic ---

    /// <summary>
    /// Resets the image source and cancels any ongoing load operation.
    /// </summary>
    private static void ResetImage(Image image)
    {
        var oldCts = image.GetValue(CurrentLoadCtsProperty);
        oldCts?.Cancel();
        oldCts?.Dispose();
        image.Source = null;
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
                image.Source = cachedBitmap;
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
                image.Source = loadedBitmap;
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
                    entry.Bitmap.Dispose();
                    LruList.Remove(entry.Node);
                    Cache.Remove(key);
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
            // Nimm die zuletzt genutzte Variante (MRU), die zur URL passt.
            for (var node = LruList.Last; node != null; node = node.Previous)
            {
                var key = node.Value;
                if (key == url || key.StartsWith(url + "_", StringComparison.Ordinal))
                {
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
                // Remove LRU item
                var lruKey = LruList.First?.Value;
                if (lruKey != null)
                {
                    if (Cache.TryGetValue(lruKey, out var entry))
                    {
                        // Explicitly dispose to free unmanaged resources immediately (performance boost for large caches)
                        entry.Bitmap.Dispose();
                    }
                    Cache.Remove(lruKey);
                    LruList.RemoveFirst();
                }
            }

            var node = LruList.AddLast(key);
            Cache[key] = (bitmap, node);
        }
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