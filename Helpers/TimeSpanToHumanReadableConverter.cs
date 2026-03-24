using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Retromind.Resources;

namespace Retromind.Helpers;

/// <summary>
/// Converts a TimeSpan into a compact, human-readable string (e.g., "5h 30m" or "< 1m").
/// Used for displaying playtime.
/// </summary>
public class TimeSpanToHumanReadableConverter : IValueConverter
{
    public static readonly TimeSpanToHumanReadableConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TimeSpan span)
        {
            // Handle exact zero (never played)
            if (span.TotalSeconds == 0) 
                return Strings.TimePlayed_Never;

            // Less than a minute but played a bit
            if (span.TotalMinutes < 1) 
                return "< 1m";
            
            // More than an hour: "5h 30m" / "2d 3h 15m"
            if (span.TotalHours >= 1)
            {
                if (span.Days > 0)
                    return $"{span.Days}d {span.Hours}h {span.Minutes}m";

                return $"{(int)span.TotalHours}h {span.Minutes}m";
            }
            
            // Minutes only: "45m"
            return $"{span.Minutes}m";
        }
        return "-";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
