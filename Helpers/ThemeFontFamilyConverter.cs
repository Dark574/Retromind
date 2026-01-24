using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Retromind.Extensions;

namespace Retromind.Helpers;

/// <summary>
/// Resolves a theme-relative font path (e.g. "Fonts/MyFont.ttf#Family")
/// into a FontFamily using ThemeProperties.ThemeBasePath as the root.
/// </summary>
public sealed class ThemeFontFamilyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var spec = parameter as string ?? value as string;
        return ResolveFontFamily(spec, fallback: new FontFamily("Arial"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("ThemeFontFamilyConverter does not support ConvertBack.");

    public static FontFamily? ResolveFontFamily(string? spec, FontFamily? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return fallback;

        var hashIndex = spec.IndexOf('#', StringComparison.Ordinal);
        var pathOrName = hashIndex >= 0 ? spec[..hashIndex] : spec;
        var family = hashIndex >= 0 ? spec[(hashIndex + 1)..] : null;

        // If it doesn't look like a path, treat it as a system font family name.
        if (!LooksLikePath(pathOrName))
            return new FontFamily(spec);

        var fullPath = Path.IsPathRooted(pathOrName)
            ? pathOrName
            : ThemeProperties.GetThemeFilePath(pathOrName);

        if (string.IsNullOrWhiteSpace(fullPath))
            return fallback;

        if (!File.Exists(fullPath))
            return fallback;

        string fontUri;
        try
        {
            // Avalonia expects an absolute URI for file-based fonts.
            fontUri = new Uri(fullPath, UriKind.Absolute).AbsoluteUri;
        }
        catch
        {
            fontUri = "file://" + fullPath;
        }

        var fontSpec = string.IsNullOrWhiteSpace(family)
            ? fontUri
            : $"{fontUri}#{family}";

        return new FontFamily(fontSpec);
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal))
            return true;

        var ext = Path.GetExtension(value);
        if (string.IsNullOrWhiteSpace(ext))
            return false;

        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }
}
