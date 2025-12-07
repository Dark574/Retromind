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
            
            // More than an hour: "5h 30m"
            if (span.TotalHours >= 1)
            {
                // Use string interpolation with invariant formatting for 'h' and 'm' suffixes, 
                // or move the format string itself to resources if you want "5 Std 30 Min".
                // For now, compact "h/m" is universally understood in gaming context.
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