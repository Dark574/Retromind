using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Retromind.Models;

namespace Retromind.Helpers;

/// <summary>
/// Generic object/value converters used in XAML bindings.
/// These are intentionally small and reusable across themes.
/// </summary>
public static class ObjectConverters
{
    /// <summary>
    /// Returns true if the input value is not null.
    /// Useful for IsVisible bindings with nullable properties (e.g. dates, years).
    /// </summary>
    public static readonly IValueConverter IsNotNull =
        new FuncValueConverter<object?, bool>(value => value is not null);

    /// <summary>
    /// Returns true if the input value is null.
    /// </summary>
    public static readonly IValueConverter IsNull =
        new FuncValueConverter<object?, bool>(value => value is null);

    /// <summary>
    /// Returns a background brush for a media tile based on selection + hover state.
    /// Bindings order: current item, selected item, IsPointerOver.
    /// </summary>
    public static readonly IMultiValueConverter TileBackground =
        new FuncMultiValueConverter<object?, IBrush?>(values =>
        {
            if (!TryReadSelection(values, out var isSelected, out var isPointerOver))
                return TransparentBrush;

            if (isSelected)
                return SelectedBrush;

            return isPointerOver ? HoverBrush : TransparentBrush;
        });

    /// <summary>
    /// Returns a cover glow shadow for selected items.
    /// Bindings order: IsSelected, SelectedGlowOpacity, SelectedGlowRadius, AccentColor.
    /// </summary>
    public static readonly IMultiValueConverter SelectedCoverGlow =
        new FuncMultiValueConverter<object?, BoxShadows?>(values =>
        {
            var entries = values?.Select(UnwrapValue).ToArray() ?? Array.Empty<object?>();
            if (entries.Length == 0 || entries[0] is not bool isSelected || !isSelected)
                return BoxShadows.Parse("0 0 0 0 #00000000");

            var strength = Math.Clamp(ToDouble(entries, 1, 0.35), 0.0, 1.0);
            if (strength <= 0.0001)
                return BoxShadows.Parse("0 0 0 0 #00000000");

            var radius = Math.Clamp(ToDouble(entries, 2, 26.0), 0.0, 120.0);
            var accent = ToColor(entries, 3, Color.Parse("#4BA3FF"));

            var blueAlpha = (byte)Math.Clamp(Math.Round(180 + (75 * strength)), 0, 255);
            var darkAlpha = (byte)Math.Clamp(Math.Round(120 + (56 * strength)), 0, 255);

            var innerBlur = Math.Max(1.0, radius);
            var innerSpread = Math.Max(0.0, radius * 0.31);
            var outerBlur = Math.Max(2.0, radius * 2.75);
            var outerSpread = Math.Max(0.0, radius * 0.92);

            var blueColor = Color.FromArgb(blueAlpha, accent.R, accent.G, accent.B);
            var darkColor = Color.FromArgb(darkAlpha, 0, 0, 0);

            var shadow = string.Create(CultureInfo.InvariantCulture,
                $"0 0 {innerBlur:0.##} {innerSpread:0.##} {blueColor}, 0 0 {outerBlur:0.##} {outerSpread:0.##} {darkColor}");

            return BoxShadows.Parse(shadow);
        });


    private static readonly IBrush SelectedBrush =
        new SolidColorBrush(Color.FromArgb(0xDD, 0x2C, 0x76, 0x9A));

    private static readonly IBrush HoverBrush =
        new SolidColorBrush(Color.FromArgb(0x66, 0x4A, 0x4A, 0x4A));

    private static readonly IBrush TransparentBrush =
        new SolidColorBrush(Colors.Transparent);

    private static bool TryReadSelection(IEnumerable<object?>? values, out bool isSelected, out bool isPointerOver)
    {
        isSelected = false;
        isPointerOver = false;

        if (values == null)
            return false;

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
            return false;
        var first = UnwrapValue(enumerator.Current);
        if (!enumerator.MoveNext())
            return false;
        var second = UnwrapValue(enumerator.Current);

        isSelected = AreSameItem(first, second);

        if (!enumerator.MoveNext())
            return true;

        isPointerOver = UnwrapValue(enumerator.Current) is bool b && b;
        return true;
    }

    private static bool AreSameItem(object? current, object? selected)
    {
        if (ReferenceEquals(current, selected))
            return true;

        if (current is MediaItem currentItem && selected is MediaItem selectedItem)
            return string.Equals(currentItem.Id, selectedItem.Id, StringComparison.Ordinal);

        return false;
    }

    private static object? UnwrapValue(object? value)
    {
        if (value == null)
            return null;

        var extracted = BindingNotification.ExtractValue(value);
        return ReferenceEquals(extracted, AvaloniaProperty.UnsetValue) ? null : extracted;
    }

    private static double ToDouble(IReadOnlyList<object?> values, int index, double fallback)
    {
        if (index < 0 || index >= values.Count)
            return fallback;

        return values[index] switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback
        };
    }

    private static Color ToColor(IReadOnlyList<object?> values, int index, Color fallback)
    {
        if (index < 0 || index >= values.Count)
            return fallback;

        var value = values[index];
        if (value is Color c)
            return c;

        if (value is string s && !string.IsNullOrWhiteSpace(s))
            return Color.TryParse(s, out var parsed) ? parsed : fallback;

        return fallback;
    }

    
}
