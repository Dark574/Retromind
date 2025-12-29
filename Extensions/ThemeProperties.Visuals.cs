using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Visual slots (primary/secondary/background) and animation settings.
/// </summary>
public partial class ThemeProperties
{
    // Animation timings (milliseconds; easier for theme authors)
    public static readonly AttachedProperty<int> FadeDurationMsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>(
            "FadeDurationMs",
            defaultValue: 200);

    public static int GetFadeDurationMs(AvaloniaObject element) =>
        element.GetValue(FadeDurationMsProperty);

    public static void SetFadeDurationMs(AvaloniaObject element, int value) =>
        element.SetValue(FadeDurationMsProperty, value);

    public static readonly AttachedProperty<int> MoveDurationMsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>(
            "MoveDurationMs",
            defaultValue: 160);

    public static int GetMoveDurationMs(AvaloniaObject element) =>
        element.GetValue(MoveDurationMsProperty);

    public static void SetMoveDurationMs(AvaloniaObject element, int value) =>
        element.SetValue(MoveDurationMsProperty, value);

    // --- PRIMARY VISUAL ---

    /// <summary>
    /// Name of the primary visual element in the theme
    /// (e.g. cover, main logo, cabinet screen).
    /// The host can locate and animate this element by name.
    /// </summary>
    public static readonly AttachedProperty<string> PrimaryVisualElementNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "PrimaryVisualElementName",
            defaultValue: "PrimaryVisual");

    public static string GetPrimaryVisualElementName(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualElementNameProperty);

    public static void SetPrimaryVisualElementName(AvaloniaObject element, string value) =>
        element.SetValue(PrimaryVisualElementNameProperty, value);

    /// <summary>
    /// Simple hint how the primary visual should enter when selection changes.
    /// Supported values (convention, interpreted by host):
    /// "None", "Fade", "SlideFromLeft", "SlideFromRight", "SlideFromTop", "SlideFromBottom".
    /// </summary>
    public static readonly AttachedProperty<string> PrimaryVisualEnterModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "PrimaryVisualEnterMode",
            defaultValue: "None");

    public static string GetPrimaryVisualEnterMode(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualEnterModeProperty);

    public static void SetPrimaryVisualEnterMode(AvaloniaObject element, string value) =>
        element.SetValue(PrimaryVisualEnterModeProperty, value);

    /// <summary>
    /// Horizontal start offset for the primary visual (in pixels).
    /// Negative = from left, positive = from right. Used only for slide modes.
    /// </summary>
    public static readonly AttachedProperty<double> PrimaryVisualEnterOffsetXProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "PrimaryVisualEnterOffsetX",
            defaultValue: 0);

    public static double GetPrimaryVisualEnterOffsetX(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualEnterOffsetXProperty);

    public static void SetPrimaryVisualEnterOffsetX(AvaloniaObject element, double value) =>
        element.SetValue(PrimaryVisualEnterOffsetXProperty, value);

    /// <summary>
    /// Vertical start offset for the primary visual (in pixels).
    /// Negative = from top, positive = from bottom. Used only for slide modes.
    /// </summary>
    public static readonly AttachedProperty<double> PrimaryVisualEnterOffsetYProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "PrimaryVisualEnterOffsetY",
            defaultValue: 0);

    public static double GetPrimaryVisualEnterOffsetY(AvaloniaObject element) =>
        element.GetValue(PrimaryVisualEnterOffsetYProperty);

    public static void SetPrimaryVisualEnterOffsetY(AvaloniaObject element, double value) =>
        element.SetValue(PrimaryVisualEnterOffsetYProperty, value);

    // --- SECONDARY VISUAL ---

    /// <summary>
    /// Name of the secondary visual element (e.g. logo, title text area).
    /// </summary>
    public static readonly AttachedProperty<string> SecondaryVisualElementNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "SecondaryVisualElementName",
            defaultValue: "SecondaryVisual");

    public static string GetSecondaryVisualElementName(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualElementNameProperty);

    public static void SetSecondaryVisualElementName(AvaloniaObject element, string value) =>
        element.SetValue(SecondaryVisualElementNameProperty, value);

    /// <summary>
    /// Enter mode for the secondary visual. Same values as PrimaryVisualEnterMode.
    /// </summary>
    public static readonly AttachedProperty<string> SecondaryVisualEnterModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "SecondaryVisualEnterMode",
            defaultValue: "None");

    public static string GetSecondaryVisualEnterMode(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualEnterModeProperty);

    public static void SetSecondaryVisualEnterMode(AvaloniaObject element, string value) =>
        element.SetValue(SecondaryVisualEnterModeProperty, value);

    public static readonly AttachedProperty<double> SecondaryVisualEnterOffsetXProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "SecondaryVisualEnterOffsetX",
            defaultValue: 0);

    public static double GetSecondaryVisualEnterOffsetX(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualEnterOffsetXProperty);

    public static void SetSecondaryVisualEnterOffsetX(AvaloniaObject element, double value) =>
        element.SetValue(SecondaryVisualEnterOffsetXProperty, value);

    public static readonly AttachedProperty<double> SecondaryVisualEnterOffsetYProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "SecondaryVisualEnterOffsetY",
            defaultValue: 0);

    public static double GetSecondaryVisualEnterOffsetY(AvaloniaObject element) =>
        element.GetValue(SecondaryVisualEnterOffsetYProperty);

    public static void SetSecondaryVisualEnterOffsetY(AvaloniaObject element, double value) =>
        element.SetValue(SecondaryVisualEnterOffsetYProperty, value);

    // --- BACKGROUND VISUAL ---

    /// <summary>
    /// Name of the background visual that should be animated when selection changes
    /// (e.g. wallpaper image, large background container).
    /// </summary>
    public static readonly AttachedProperty<string> BackgroundVisualElementNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "BackgroundVisualElementName",
            defaultValue: "BackgroundVisual");

    public static string GetBackgroundVisualElementName(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualElementNameProperty);

    public static void SetBackgroundVisualElementName(AvaloniaObject element, string value) =>
        element.SetValue(BackgroundVisualElementNameProperty, value);

    /// <summary>
    /// Enter mode for the background visual (Fade/Slide...).
    /// </summary>
    public static readonly AttachedProperty<string> BackgroundVisualEnterModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "BackgroundVisualEnterMode",
            defaultValue: "None");

    public static string GetBackgroundVisualEnterMode(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualEnterModeProperty);

    public static void SetBackgroundVisualEnterMode(AvaloniaObject element, string value) =>
        element.SetValue(BackgroundVisualEnterModeProperty, value);

    public static readonly AttachedProperty<double> BackgroundVisualEnterOffsetXProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "BackgroundVisualEnterOffsetX",
            defaultValue: 0);

    public static double GetBackgroundVisualEnterOffsetX(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualEnterOffsetXProperty);

    public static void SetBackgroundVisualEnterOffsetX(AvaloniaObject element, double value) =>
        element.SetValue(BackgroundVisualEnterOffsetXProperty, value);

    public static readonly AttachedProperty<double> BackgroundVisualEnterOffsetYProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "BackgroundVisualEnterOffsetY",
            defaultValue: 0);

    public static double GetBackgroundVisualEnterOffsetY(AvaloniaObject element) =>
        element.GetValue(BackgroundVisualEnterOffsetYProperty);

    public static void SetBackgroundVisualEnterOffsetY(AvaloniaObject element, double value) =>
        element.SetValue(BackgroundVisualEnterOffsetYProperty, value);

    /// <summary>
    /// Internal helper: stores the original margin of a visual slot so that
    /// repeated enter-animations can always return to the same base position.
    /// </summary>
    public static readonly AttachedProperty<Thickness> VisualBaseMarginProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>(
            "VisualBaseMargin",
            defaultValue: new Thickness(0));

    public static Thickness GetVisualBaseMargin(AvaloniaObject element) =>
        element.GetValue(VisualBaseMarginProperty);

    public static void SetVisualBaseMargin(AvaloniaObject element, Thickness value) =>
        element.SetValue(VisualBaseMarginProperty, value);

    /// <summary>
    /// Internal helper: indicates whether VisualBaseMargin has been initialized
    /// for a given control.
    /// </summary>
    public static readonly AttachedProperty<bool> VisualBaseMarginInitializedProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "VisualBaseMarginInitialized",
            defaultValue: false);

    public static bool GetVisualBaseMarginInitialized(AvaloniaObject element) =>
        element.GetValue(VisualBaseMarginInitializedProperty);

    public static void SetVisualBaseMarginInitialized(AvaloniaObject element, bool value) =>
        element.SetValue(VisualBaseMarginInitializedProperty, value);
}