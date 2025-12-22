using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Retromind.Helpers;

public class BoolToScaleConverter : IValueConverter
{
    public double NormalScale { get; set; } = 1.0;
    public double SelectedScale { get; set; } = 1.4;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isSelected = value is bool b && b;
        return isSelected ? SelectedScale : NormalScale;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}