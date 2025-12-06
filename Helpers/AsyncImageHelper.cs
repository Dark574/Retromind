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

public class AsyncImageHelper : AvaloniaObject
{
    // Cache, to prevent loading the same image 10 times
    private static readonly Dictionary<string, Bitmap> Cache = new();
    
    // Maximum number of images to keep in RAM.
    // 100 images * ~5MB (uncompressed bitmap) = ~500MB RAM usage.
    private const int MaxCacheSize = 100; 
    
    private static readonly HttpClient HttpClient = new();

    // Attached Property: Url
    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<AsyncImageHelper, Image, string?>("Url");

    public static string? GetUrl(Image element)
    {
        return element.GetValue(UrlProperty);
    }

    public static void SetUrl(Image element, string? value)
    {
        element.SetValue(UrlProperty, value);
    }

    // Method to save an image from the cache to disk.
    public static async Task<bool> SaveCachedImageAsync(string url, string destinationPath)
    {
        if (Cache.TryGetValue(url, out var bitmap))
        {
            return await Task.Run(() =>
            {
                try
                {
                    bitmap.Save(destinationPath); // Avalonia Bitmap Save
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error saving from cache: {ex.Message}");
                    return false;
                }
            });
        }
        return false; // Not in cache
    }
    
    static AsyncImageHelper()
    {
        // IMPORTANT: Set User-Agent!
        // Many servers (ComicVine, Wikipedia, etc.) block requests without a User-Agent.
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "Retromind-MediaManager/1.0");

        // Listener: When Url changes, load the image.
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            var url = args.NewValue as string;
            LoadImageAsync(image, url);
        });
    }

    private static async void LoadImageAsync(Image image, string? url)
    {
        // Clear image first (or set placeholder)
        image.Source = null;

        if (string.IsNullOrEmpty(url)) return;

        // 1. Cache Check
        if (Cache.TryGetValue(url, out var cachedBitmap))
        {
            image.Source = cachedBitmap;
            return;
        }

        try
        {
            // Fresh client for every request to ensure clean headers
            using var client = new HttpClient();
            // Set headers to avoid 403 Forbidden errors
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // 2. Download
            var data = await HttpClient.GetByteArrayAsync(url);

            // 3. Create Bitmap (must be safe on UI thread).
            // Dispatching to UI thread just to be safe, although Bitmap ctor is usually fine.
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var stream = new MemoryStream(data); 
                    stream.Position = 0;
                    var bitmap = new Bitmap(stream);
                    
                    // --- SIMPLE HOUSEKEEPING ---
                    // If cache is full, clear it completely.
                    // A "Least Recently Used" (LRU) logic would be better but more complex.
                    // This prevents the application from crashing due to OutOfMemory exceptions.
                    if (Cache.Count >= MaxCacheSize)
                    {
                        Debug.WriteLine("Cache limit reached. Clearing to free memory...");
                        Cache.Clear();
                    }
                    
                    // Add to Cache
                    Cache[url] = bitmap;
                    
                    // Only set source if the URL is still the same (in case user scrolled fast)
                    if (GetUrl(image) == url)
                    {
                        image.Source = bitmap;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Image creation error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Download error for {url}: {ex.Message}");
        }
    }
}