using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Retromind.Helpers;

/// <summary>
/// Helper class to load images asynchronously from URLs or local paths.
/// Features an LRU (Least Recently Used) Cache and supports Downsampling to save memory.
/// </summary>
public class AsyncImageHelper : AvaloniaObject
{
    // --- Configuration ---
    
    // Maximum number of images to keep in RAM.
    // 200 images * ~5MB (uncompressed bitmap) = ~1000MB RAM usage.
    private const int MaxCacheSize = 200; 
    
    // Shared HttpClient to prevent socket exhaustion
    private static readonly HttpClient HttpClient = new();

    // --- Cache Implementation (LRU) ---
    // Using a simple combination of Dictionary (fast lookup) and LinkedList (order).
    // Access must be locked because UI might trigger multiple loads in parallel.
    private static readonly Dictionary<string, (Bitmap Bitmap, LinkedListNode<string> Node)> Cache = new();
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();
    
    static AsyncImageHelper()
    {
        // Set a proper User-Agent to avoid being blocked by some CDNs/APIs
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Retromind/1.0 (OpenSource Media Manager)");
        
        // Wire up the PropertyChanged handler
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

    /// <summary>
    /// Optional: Define a width to decode the image to. 
    /// Drastically reduces memory usage for thumbnails (e.g. set to 200 or 400).
    /// </summary>
    public static readonly AttachedProperty<int?> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, int?>("DecodeWidth");

    public static int? GetDecodeWidth(Image element) => element.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image element, int? value) => element.SetValue(DecodeWidthProperty, value);

    // --- Core Logic ---
    
    private static async void LoadImageAsync(Image image, string? url, int? decodeWidth)
    {
        // 1. Reset Source to avoid showing wrong image while loading
        // (Optional: Set a placeholder/loading image here if desired)
        image.Source = null;

        if (string.IsNullOrEmpty(url)) return;

        // Cache Key needs to include decodeWidth, because a thumbnail != full 4k image
        var cacheKey = decodeWidth.HasValue ? $"{url}_{decodeWidth}" : url;

        // 2. Cache Check (Fast Path)
        Bitmap? cachedBitmap = GetFromCache(cacheKey);
        if (cachedBitmap != null)
        {
            image.Source = cachedBitmap;
            return;
        }

        try
        {
            // 3. Load & Decode (Heavy work on ThreadPool)
            var loadedBitmap = await Task.Run(async () => 
            {
                // Is it a web URL?
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var data = await HttpClient.GetByteArrayAsync(url);
                    using var stream = new MemoryStream(data);
                    return DecodeBitmap(stream, decodeWidth);
                }
                // Is it a local file?
                else
                {
                    if (!File.Exists(url)) return null;
                    using var stream = File.OpenRead(url);
                    return DecodeBitmap(stream, decodeWidth);
                }
            });

            if (loadedBitmap == null) return;

            // 4. Add to Cache
            AddToCache(cacheKey, loadedBitmap);

            // 5. Update UI (Check for race condition: did the URL change while we were loading?)
            if (GetUrl(image) == url)
            {
                // Bitmap is already created on background thread, 
                // assignment to Source is lightweight but must happen on UI Thread.
                // However, 'await Task.Run' returns to the context (UI Thread) automatically in async void/Task.
                image.Source = loadedBitmap;
            }
        }
        catch (Exception ex)
        {
            // Fail silently in UI, but log for developer
            Debug.WriteLine($"[AsyncImageHelper] Failed to load '{url}': {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes the stream into a Bitmap, optionally resizing it during decode to save RAM.
    /// </summary>
    private static Bitmap DecodeBitmap(Stream stream, int? decodeWidth)
    {
        stream.Position = 0;
        
        // If decodeWidth is set, we use Avalonia's optimized decoding
        if (decodeWidth.HasValue && decodeWidth.Value > 0)
        {
            return Bitmap.DecodeToWidth(stream, decodeWidth.Value);
        }
        
        return new Bitmap(stream);
    }
    
    // --- Cache Helpers ---
    
    private static Bitmap? GetFromCache(string key)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                // LRU: Move accessed item to the end of the list (most recently used)
                LruList.Remove(entry.Node);
                LruList.AddLast(entry.Node);
                return entry.Bitmap;
            }
        }
        return null;
    }

    private static void AddToCache(string key, Bitmap bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(key)) return; // Already added by another thread

            // Enforce Capacity
            if (Cache.Count >= MaxCacheSize)
            {
                // Remove the first item (Least Recently Used)
                var lruKey = LruList.First?.Value;
                if (lruKey != null)
                {
                    Cache.Remove(lruKey);
                    LruList.RemoveFirst();
                    // Note: We could dispose the bitmap here if we are sure it's not used anymore.
                    // But in WPF/Avalonia, images might still be rendered. 
                    // GC usually handles this fine once the View releases the reference.
                }
            }

            // Add new item
            var node = LruList.AddLast(key);
            Cache[key] = (bitmap, node);
        }
    }

    /// <summary>
    /// Saves a cached image to disk (e.g. for downloading covers).
    /// </summary>
    public static async Task<bool> SaveCachedImageAsync(string url, string destinationPath)
    {
        // Try to get from cache first (without decoding width suffix logic for now, usually full size)
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
                Debug.WriteLine($"[AsyncImageHelper] Error saving: {ex.Message}");
                return false;
            }
        });
    }
}