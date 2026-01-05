using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Returns true if the given asset type is Manual, otherwise false.
/// Used to toggle manual-specific UI elements (e.g. document icons)
/// in generic asset templates.
/// </summary>
public sealed class AssetTypeIsManualConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is AssetType type && type == AssetType.Manual;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}