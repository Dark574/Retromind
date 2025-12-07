using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Models;
using Retromind.Resources;

namespace Retromind.Helpers;

/// <summary>
/// Converts the MediaType enum to a localized, human-readable string.
/// Used in UI for comboboxes and labels.
/// </summary>
public class MediaTypeToLocalizedNameConverter : IValueConverter
{
    // Optional singleton for simpler code usage
    public static readonly MediaTypeToLocalizedNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MediaType type)
        {
            return type switch
            {
                // Use resource strings for internationalization (I18N)
                // Make sure these keys exist in your Strings.resx
                
                MediaType.Native => Strings.Type_Native,
                MediaType.Emulator => Strings.Type_Emulator,
                // Assuming you might add Command type or similar in future, handle it here
                MediaType.Command => Strings.Type_Command,
                
                _ => type.ToString()
            };
        }

        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException("Converting from localized string back to MediaType is not supported.");
    }
}