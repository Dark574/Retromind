using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Retromind.Helpers;

/// <summary>
/// Turns a theme color into a brush while applying a fixed opacity. Separate
/// instances let runtime themes derive full, soft, and faint brushes from the
/// same host-driven dynamic accent color.
/// </summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public double Opacity { get; set; } = 1;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
            return Brushes.Transparent;

        var alpha = (byte)Math.Clamp(Math.Round(255 * Opacity), 0, 255);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
