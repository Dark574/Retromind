using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Selection and focus behavior (controller-friendly).
/// </summary>
public partial class ThemeProperties
{
    public static readonly AttachedProperty<double> SelectedScaleProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "SelectedScale",
            defaultValue: 1.06);

    public static double GetSelectedScale(AvaloniaObject element) =>
        element.GetValue(SelectedScaleProperty);

    public static void SetSelectedScale(AvaloniaObject element, double value) =>
        element.SetValue(SelectedScaleProperty, value);

    public static readonly AttachedProperty<double> UnselectedOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "UnselectedOpacity",
            defaultValue: 0.75);

    public static double GetUnselectedOpacity(AvaloniaObject element) =>
        element.GetValue(UnselectedOpacityProperty);

    public static void SetUnselectedOpacity(AvaloniaObject element, double value) =>
        element.SetValue(UnselectedOpacityProperty, value);

    public static readonly AttachedProperty<double> SelectedGlowOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "SelectedGlowOpacity",
            defaultValue: 0.35);

    public static double GetSelectedGlowOpacity(AvaloniaObject element) =>
        element.GetValue(SelectedGlowOpacityProperty);

    public static void SetSelectedGlowOpacity(AvaloniaObject element, double value) =>
        element.SetValue(SelectedGlowOpacityProperty, value);

    /// <summary>
    /// Controls whether the BigMode host is allowed to apply generic selection
    /// effects (zoom/opacity/glow) for this ListBox. Default: true.
    /// Themes that render their own effects per item can set this to false.
    /// </summary>
    public static readonly AttachedProperty<bool> UseHostSelectionEffectsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "UseHostSelectionEffects",
            defaultValue: true);

    public static bool GetUseHostSelectionEffects(AvaloniaObject element) =>
        element.GetValue(UseHostSelectionEffectsProperty);

    public static void SetUseHostSelectionEffects(AvaloniaObject element, bool value) =>
        element.SetValue(UseHostSelectionEffectsProperty, value);

    /// <summary>
    /// Controls the size of circular list windows used by themes that bind to
    /// BigModeViewModel.CircularItems. Values <= 0 will show the full list.
    /// </summary>
    public static readonly AttachedProperty<int> CircularWindowSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>(
            "CircularWindowSize",
            defaultValue: 9);

    public static int GetCircularWindowSize(AvaloniaObject element) =>
        element.GetValue(CircularWindowSizeProperty);

    public static void SetCircularWindowSize(AvaloniaObject element, int value) =>
        element.SetValue(CircularWindowSizeProperty, value);
}
