using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Returns true when the bound AssetType matches <see cref="ExpectedType"/>.
/// </summary>
public sealed class AssetTypeEqualsConverter : IValueConverter
{
    public AssetType ExpectedType { get; set; } = AssetType.Unknown;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is AssetType type && type == ExpectedType;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
