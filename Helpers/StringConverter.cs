using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace Retromind.Helpers;

/// <summary>
/// String-related value converters used in XAML bindings.
/// These helpers keep XAML expressions small and theme-author friendly.
/// </summary>
public static class StringConverters
{
    /// <summary>
    /// Returns true if the input is a non-null, non-empty string.
    /// Null, empty or whitespace-only values are treated as false.
    /// Typical usage: IsVisible bindings in themes.
    /// </summary>
    public static readonly IValueConverter IsNotNullOrEmpty =
        new FuncValueConverter<object?, bool>(value =>
        {
            if (value is not string s) return false;
            return !string.IsNullOrWhiteSpace(s);
        });

    /// <summary>
    /// Returns true if the input is null or an empty/whitespace-only string.
    /// </summary>
    public static readonly IValueConverter IsNullOrEmpty =
        new FuncValueConverter<object?, bool>(value =>
        {
            if (value is not string s) return true;
            return string.IsNullOrWhiteSpace(s);
        });

    /// <summary>
    /// Converts a theme path like "Default/theme.axaml" to "Default" for display.
    /// </summary>
    public static readonly IValueConverter ThemePathToFolderName =
        new FuncValueConverter<object?, string?>(value =>
        {
            if (value is not string s || string.IsNullOrWhiteSpace(s))
                return string.Empty;

            var trimmed = s.Trim();
            if (trimmed.EndsWith("theme.axaml", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(trimmed);
                return string.IsNullOrWhiteSpace(dir) ? trimmed : Path.GetFileName(dir);
            }

            var parent = Path.GetDirectoryName(trimmed);
            return string.IsNullOrWhiteSpace(parent) ? trimmed : Path.GetFileName(parent);
        });
}
