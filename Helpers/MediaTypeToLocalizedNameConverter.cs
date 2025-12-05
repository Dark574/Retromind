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
        {
            return type switch
            {
                // Falls du Resource-Strings hast (z.B. Strings.TypeNative), nutze diese stattdessen.
                MediaType.Native => "Native Anwendung / Skript",
                MediaType.Emulator => "Emulator (via Profil)",
                
                // Der Text für den Command-Typ
                MediaType.Command => "Externer Launcher (Steam, GOG, URL)",
                
                _ => type.ToString()
            };
        }

        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}