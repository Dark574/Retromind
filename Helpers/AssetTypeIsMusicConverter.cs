using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Returns true if the given asset type is Music, otherwise false.
/// Used to toggle music-specific UI elements (e.g. a note icon)
/// in the generic asset template.
/// </summary>
public sealed class AssetTypeIsMusicConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is AssetType type && type == AssetType.Music;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}