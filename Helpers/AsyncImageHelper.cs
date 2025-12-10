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
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to load images asynchronously from URLs or local paths.
/// Features an LRU (Least Recently Used) Cache, Downsampling, and Task Cancellation.
/// </summary>
public class AsyncImageHelper : AvaloniaObject
{
    // --- Configuration ---

    // Maximum number of images to keep in RAM.
    private const int MaxCacheSize = 200; 

    // Shared HttpClient to prevent socket exhaustion
    private static readonly HttpClient HttpClient = new();

    // --- Cache State ---
    private static readonly Dictionary<string, (Bitmap Bitmap, LinkedListNode<string> Node)> Cache = new();
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();

    static AsyncImageHelper()
    {
        // Set a proper User-Agent
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Retromind/1.0 (OpenSource Media Manager)");

        // Handle URL changes
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            var url = args.NewValue as string;
            var width = GetDecodeWidth(image);
            LoadImageAsync(image, url, width);
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

    private static async void LoadImageAsync(Image image, string? url, int? decodeWidth)
    {
        // 1. Cancel previous operation if running
        var oldCts = image.GetValue(CurrentLoadCtsProperty);
        oldCts?.Cancel();
        oldCts?.Dispose();

        // 2. Reset Source
        image.Source = null;

        if (string.IsNullOrEmpty(url)) 
        {
            image.SetValue(CurrentLoadCtsProperty, null);
            return;
        }

        // 3. Create new Cancellation Token
        var cts = new CancellationTokenSource();
        image.SetValue(CurrentLoadCtsProperty, cts);

        // 4. Cache Check (Fast Path)
        var cacheKey = decodeWidth.HasValue ? $"{url}_{decodeWidth}" : url;
        Bitmap? cachedBitmap = GetFromCache(cacheKey);
        
        if (cachedBitmap != null)
        {
            image.Source = cachedBitmap;
            return;
        }

        try
        {
            var token = cts.Token;

            // 5. Load & Decode (Heavy work on ThreadPool)
            var loadedBitmap = await Task.Run(async () => 
            {
                if (token.IsCancellationRequested) return null;

                try 
                {
                    if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        var data = await HttpClient.GetByteArrayAsync(url, token);
                        using var stream = new MemoryStream(data);
                        return DecodeBitmap(stream, decodeWidth);
                    }
                    else
                    {
                        if (!File.Exists(url)) return null;
                        // File.OpenRead ensures we don't lock the file exclusively
                        using var stream = File.OpenRead(url); 
                        return DecodeBitmap(stream, decodeWidth);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal behavior during fast scrolling
                    return null;
                }
                catch (Exception)
                {
                    // IO errors (file locked, 404, etc)
                    return null;
                }
            }, token);

            if (token.IsCancellationRequested || loadedBitmap == null) return;

            // 6. Add to Cache
            AddToCache(cacheKey, loadedBitmap);

            // 7. Update UI
            // Verify again that we are still the requested URL (redundant but safe)
            if (GetUrl(image) == url)
            {
                image.Source = loadedBitmap;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncImageHelper] Critical error: {ex.Message}");
        }
    }

    private static Bitmap DecodeBitmap(Stream stream, int? decodeWidth)
    {
        stream.Position = 0;
        if (decodeWidth.HasValue && decodeWidth.Value > 0)
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
            // Wir müssen alle Keys finden, die mit dieser URL starten (wegen decodeWidth Suffix)
            var keysToRemove = new List<string>();
            foreach (var key in Cache.Keys)
            {
                if (key == url || key.StartsWith(url + "_"))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                if (Cache.TryGetValue(key, out var entry))
                {
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
                         // Optional: explicitly dispose to free unmanaged memory immediately
                         // entry.Bitmap.Dispose(); 
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
        Bitmap? bitmap = GetFromCache(url);
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