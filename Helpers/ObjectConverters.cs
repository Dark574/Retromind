using System;
using System.Globalization;
using Avalonia.Data.Converters;

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
}