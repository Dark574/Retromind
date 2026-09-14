using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Retromind.Helpers;

/// <summary>
/// Resolves a system font name or an absolute font file path into a FontFamily.
/// ThemeLoader normalizes theme-relative font paths before this converter runs.
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
        if (!LooksLikeFontPath(pathOrName))
            return new FontFamily(spec);

        if (!Path.IsPathRooted(pathOrName))
            return fallback;

        if (!File.Exists(pathOrName))
            return fallback;

        string fontUri;
        try
        {
            // Avalonia expects an absolute URI for file-based fonts.
            fontUri = new Uri(pathOrName, UriKind.Absolute).AbsoluteUri;
        }
        catch
        {
            fontUri = "file://" + pathOrName;
        }

        var fontSpec = string.IsNullOrWhiteSpace(family)
            ? fontUri
            : $"{fontUri}#{family}";

        return new FontFamily(fontSpec);
    }

    internal static bool LooksLikeFontPath(string value)
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
