using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Helpers;

public class MediaTypeToLocalizedNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaType type)
            return type switch
            {
                MediaType.Native => Strings.ModeNative,
                MediaType.Emulator => Strings.ModeEmulator,
                _ => type.ToString()
            };
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}