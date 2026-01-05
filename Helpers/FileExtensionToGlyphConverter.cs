using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Retromind.Helpers;

/// <summary>
/// Maps a file extension (".pdf", ".txt", …) to a simple Unicode glyph that can
/// be used as a lightweight icon in the UI.
/// 
/// This keeps the XAML simple and avoids hard-coding logic in the view.
/// </summary>
public sealed class FileExtensionToGlyphConverter : IValueConverter
{
    /// <summary>
    /// Converts a relative or absolute file path into a glyph string.
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrWhiteSpace(path))
            return "📄"; // generic document

        string ext;
        try
        {
            ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        }
        catch
        {
            return "📄";
        }

        return ext switch
        {
            ".pdf" => "📕",   // PDF / book-like
            ".txt" => "📝",
            ".md"  => "📝",
            ".rtf" => "📝",
            ".html" or ".htm" => "🌐",
            ".doc" or ".docx" => "📘",
            _ => "📄"         // fallback for unknown types
        };
    }

    /// <summary>
    /// Not used; one-way binding only.
    /// </summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}