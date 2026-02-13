using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Retromind.Extensions;

namespace Retromind.Helpers;

/// <summary>
    /// Converts a theme-relative asset path (e.g. "Images/cabinet.png")
    /// into a Bitmap, using a per-view ThemeBasePath when available.
/// Intended for use in theme XAML to load images that live next to the AppImage.
/// </summary>
public sealed class ThemeAssetToBitmapConverter : IValueConverter
{
    private const int MaxCacheSize = 64;

    // Cache to avoid repeated IO for the same theme asset.
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

    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruList = new();
    private static readonly object CacheLock = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Prefer an explicit converter parameter (e.g. ConverterParameter="Images/cabinet.png").
        // Fall back to the bound value if no parameter is provided.
        var relativePath = parameter as string ?? value as string;
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        try
        {
            string? fullPath = null;
            if (Path.IsPathRooted(relativePath))
            {
                fullPath = relativePath;
            }
            else if (value is AvaloniaObject scope)
            {
                fullPath = ThemeProperties.GetThemeFilePath(relativePath, scope);
            }
            else
            {
                fullPath = ThemeProperties.GetThemeFilePath(relativePath);
            }

            if (string.IsNullOrWhiteSpace(fullPath))
                return null;

            var cached = GetFromCache(fullPath);
            if (cached != null)
                return cached;

            if (!File.Exists(fullPath))
                return null;

            var bitmap = new Bitmap(fullPath);
            AddToCache(fullPath, bitmap);
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeAssetToBitmapConverter] Failed to load theme asset '{relativePath}': {ex.Message}");
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("ThemeAssetToBitmapConverter does not support ConvertBack.");

    private static Bitmap? GetFromCache(string key)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var entry))
                return null;

            if (!entry.BitmapRef.TryGetTarget(out var bitmap))
            {
                Cache.Remove(key);
                LruList.Remove(entry.Node);
                return null;
            }

            LruList.Remove(entry.Node);
            LruList.AddLast(entry.Node);
            return bitmap;
        }
    }

    private static void AddToCache(string key, Bitmap bitmap)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(key))
                return;

            TrimCacheIfNeeded();

            var node = LruList.AddLast(key);
            Cache[key] = new CacheEntry(new WeakReference<Bitmap>(bitmap), node);
        }
    }

    private static void TrimCacheIfNeeded()
    {
        if (Cache.Count < MaxCacheSize)
            return;

        var node = LruList.First;
        while (node != null)
        {
            var next = node.Next;
            if (Cache.TryGetValue(node.Value, out var entry) &&
                !entry.BitmapRef.TryGetTarget(out _))
            {
                Cache.Remove(node.Value);
                LruList.Remove(node);
            }
            node = next;
        }

        while (Cache.Count >= MaxCacheSize && LruList.First != null)
        {
            var oldestKey = LruList.First.Value;
            Cache.Remove(oldestKey);
            LruList.RemoveFirst();
        }
    }
}
