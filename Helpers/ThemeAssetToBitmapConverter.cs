using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Retromind.Extensions;

namespace Retromind.Helpers;

/// <summary>
/// Converts a theme-relative asset path (e.g. "Images/cabinet.png")
/// into a Bitmap, using ThemeProperties.ThemeBasePath as root.
/// Intended for use in theme XAML to load images that live next to the AppImage.
/// </summary>
public sealed class ThemeAssetToBitmapConverter : IValueConverter
{
    // Simple cache to avoid repeated IO for the same theme asset.
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Prefer an explicit converter parameter (e.g. ConverterParameter="Images/cabinet.png").
        // Fall back to the bound value if no parameter is provided.
        var relativePath = parameter as string ?? value as string;
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        try
        {
            var fullPath = ThemeProperties.GetThemeFilePath(relativePath);
            if (string.IsNullOrWhiteSpace(fullPath))
                return null;

            if (Cache.TryGetValue(fullPath, out var cached))
                return cached;

            if (!File.Exists(fullPath))
                return null;

            var bitmap = new Bitmap(fullPath);
            Cache[fullPath] = bitmap;
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
}