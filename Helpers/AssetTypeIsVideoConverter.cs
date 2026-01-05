using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Returns true if the given asset type is Video, otherwise false.
/// Used to toggle video-specific UI elements (e.g. play icon)
/// in the generic asset template.
/// </summary>
public sealed class AssetTypeIsVideoConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is AssetType type && type == AssetType.Video;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}