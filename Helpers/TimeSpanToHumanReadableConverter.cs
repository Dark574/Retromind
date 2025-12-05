using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Retromind.Helpers;

public class TimeSpanToHumanReadableConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan span)
        {
            // Wirklich noch gar nichts (0 Sekunden)
            if (span.TotalSeconds == 0) return "Noch nicht gespielt";

            // Weniger als eine Minute (aber > 0)
            if (span.TotalMinutes < 1) return "< 1m";
            
            // Ab 1 Stunde: "5h 30m"
            if (span.TotalHours >= 1)
            {
                return $"{(int)span.TotalHours}h {span.Minutes}m";
            }
            
            // Sonst nur Minuten: "45m"
            return $"{span.Minutes}m";
        }
        return "-";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}