using System;
using System.Collections.Generic;
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

    
}
