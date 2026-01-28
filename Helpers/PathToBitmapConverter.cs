using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Retromind.Helpers;

/// <summary>
/// Converts a file path or Avalonia asset URI into a Bitmap.
/// Supports:
/// - local file paths
/// - asset URIs (avares://)
/// Automatically uses a cache to avoid repeated file access,
/// which improves performance when displaying large ROM lists.
/// Note: Web URLs (http) are not supported synchronously; returns a placeholder.
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    private const int MaxCacheSize = 200;

    // Cache to store loaded Bitmaps and avoid repeated IO (key: full path).
    // Uses weak references so images can be collected when no longer in use.
    private sealed class CacheEntry
    {
        public CacheEntry(WeakReference<Bitmap> bitmapRef, LinkedListNode<string> node)
        {
            BitmapRef = bitmapRef;
            Node = node;
        }

        public WeakReference<Bitmap> BitmapRef { get; }
        public LinkedListNode<string> Node { get; }
    }

    private static readonly Dictionary<string, CacheEntry> BitmapCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();
    
    // Placeholder for unsupported cases (e.g., web URLs)
    private static readonly Bitmap? PlaceholderBitmap = null; // Load a default image if needed, e.g., new Bitmap("avares://Assets/placeholder.png");
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string inputPath && !string.IsNullOrEmpty(inputPath))
        {
            try
            {
                // Combine with parameter if provided (e.g., directory + filename)
                string fullPath = CombinePathWithParameter(inputPath, parameter);
                
                if (string.IsNullOrEmpty(fullPath)) return PlaceholderBitmap;
                
                // Check cache first for performance
                var cachedBitmap = GetFromCache(fullPath);
                if (cachedBitmap != null)
                    return cachedBitmap;
                
                // Handle web links (unsupported in sync converter)
                if (fullPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return PlaceholderBitmap;
                }

                // Load from local file
                if (System.IO.File.Exists(fullPath))
                {
                    var bitmap = new Bitmap(fullPath);
                    AddToCache(fullPath, bitmap);
                    return bitmap;
                }

                // Load from asset URI
                if (fullPath.StartsWith("avares://"))
                {
                    var uri = new Uri(fullPath);
                    using var stream = AssetLoader.Open(uri);
                    var bitmap = new Bitmap(stream);
                    AddToCache(fullPath, bitmap);
                    return bitmap;
                }
                
                // Fallback if path invalid
                return PlaceholderBitmap;
            }
            catch (FileNotFoundException ex)
            {
                // Specific handling for missing files
                Debug.WriteLine($"[PathToBitmapConverter] File not found: {ex.Message}");
                return PlaceholderBitmap;
            }
            catch (Exception ex)
            {
                // General error (e.g., invalid URI)
                Debug.WriteLine($"[PathToBitmapConverter] Conversion error: {ex.Message}");
                return PlaceholderBitmap;
            }
        }
        return PlaceholderBitmap;
    }

    private static Bitmap? GetFromCache(string key)
    {
        lock (CacheLock)
        {
            if (!BitmapCache.TryGetValue(key, out var entry))
                return null;

            if (!entry.BitmapRef.TryGetTarget(out var bitmap))
            {
                BitmapCache.Remove(key);
                LruList.Remove(entry.Node);
                return null;
            }

            // Refresh LRU position
            LruList.Remove(entry.Node);
            LruList.AddLast(entry.Node);
            return bitmap;
        }
    }

    private static void AddToCache(string key, Bitmap bitmap)
    {
        lock (CacheLock)
        {
            if (BitmapCache.ContainsKey(key))
                return;

            TrimCacheIfNeeded();

            var node = LruList.AddLast(key);
            BitmapCache[key] = new CacheEntry(new WeakReference<Bitmap>(bitmap), node);
        }
    }

    private static void TrimCacheIfNeeded()
    {
        if (BitmapCache.Count < MaxCacheSize)
            return;

        // Drop any dead entries first.
        var node = LruList.First;
        while (node != null)
        {
            var next = node.Next;
            if (BitmapCache.TryGetValue(node.Value, out var entry) &&
                !entry.BitmapRef.TryGetTarget(out _))
            {
                BitmapCache.Remove(node.Value);
                LruList.Remove(node);
            }
            node = next;
        }

        while (BitmapCache.Count >= MaxCacheSize && LruList.First != null)
        {
            var oldestKey = LruList.First.Value;
            BitmapCache.Remove(oldestKey);
            LruList.RemoveFirst();
        }
    }

    /// <summary>
    /// Helper to combine input path with optional parameter (e.g., filename).
    /// </summary>
    private static string CombinePathWithParameter(string inputPath, object? parameter)
    {
        if (parameter is string fileName && !string.IsNullOrEmpty(fileName))
        {
            return System.IO.Path.Combine(inputPath, fileName);
        }
        return inputPath;
    }
    
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
