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
    // Simple cache to store loaded Bitmaps and avoid repeated IO (key: full path)
    private static readonly Dictionary<string, Bitmap> BitmapCache = new();
    
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
                if (BitmapCache.TryGetValue(fullPath, out var cachedBitmap))
                {
                    return cachedBitmap;
                }
                
                // Handle web links (unsupported in sync converter)
                if (fullPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return PlaceholderBitmap;
                }

                // Load from local file
                if (System.IO.File.Exists(fullPath))
                {
                    var bitmap = new Bitmap(fullPath);
                    BitmapCache[fullPath] = bitmap; // Cache for future use
                    return bitmap;
                }

                // Load from asset URI
                if (fullPath.StartsWith("avares://"))
                {
                    var uri = new Uri(fullPath);
                    var bitmap = new Bitmap(AssetLoader.Open(uri));
                    BitmapCache[fullPath] = bitmap;
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