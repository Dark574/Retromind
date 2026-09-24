using Avalonia;
using Avalonia.Media;

namespace Retromind.Extensions;

/// <summary>
/// Opt-in dynamic accent colors derived from the artwork of the current BigMode
/// selection. Input settings and host-populated output colors live together so
/// runtime themes can consume the feature without a code-behind file.
/// </summary>
public partial class ThemeProperties
{
    public static readonly AttachedProperty<bool> DynamicAccentEnabledProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "DynamicAccentEnabled");

    public static bool GetDynamicAccentEnabled(AvaloniaObject element) =>
        element.GetValue(DynamicAccentEnabledProperty);

    public static void SetDynamicAccentEnabled(AvaloniaObject element, bool value) =>
        element.SetValue(DynamicAccentEnabledProperty, value);

    public static readonly AttachedProperty<int> DynamicAccentTransitionMsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>(
            "DynamicAccentTransitionMs",
            defaultValue: 400);

    public static int GetDynamicAccentTransitionMs(AvaloniaObject element) =>
        element.GetValue(DynamicAccentTransitionMsProperty);

    public static void SetDynamicAccentTransitionMs(AvaloniaObject element, int value) =>
        element.SetValue(DynamicAccentTransitionMsProperty, value);

    public static readonly AttachedProperty<Color> DynamicAccentColorProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Color>(
            "DynamicAccentColor",
            defaultValue: Color.Parse("#62E6FF"));

    public static Color GetDynamicAccentColor(AvaloniaObject element) =>
        element.GetValue(DynamicAccentColorProperty);

    public static void SetDynamicAccentColor(AvaloniaObject element, Color value) =>
        element.SetValue(DynamicAccentColorProperty, value);

    public static readonly AttachedProperty<Color> DynamicSecondaryAccentColorProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Color>(
            "DynamicSecondaryAccentColor",
            defaultValue: Color.Parse("#9B7BFF"));

    public static Color GetDynamicSecondaryAccentColor(AvaloniaObject element) =>
        element.GetValue(DynamicSecondaryAccentColorProperty);

    public static void SetDynamicSecondaryAccentColor(AvaloniaObject element, Color value) =>
        element.SetValue(DynamicSecondaryAccentColorProperty, value);
}
