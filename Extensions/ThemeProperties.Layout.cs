using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Layout-related attached properties for tiles and panels.
/// </summary>
public partial class ThemeProperties
{
    // Spacing / sizing
    public static readonly AttachedProperty<double> TileSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "TileSize",
            defaultValue: 220);

    public static double GetTileSize(AvaloniaObject element) =>
        element.GetValue(TileSizeProperty);

    public static void SetTileSize(AvaloniaObject element, double value) =>
        element.SetValue(TileSizeProperty, value);

    public static readonly AttachedProperty<double> TileSpacingProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "TileSpacing",
            defaultValue: 12);

    public static double GetTileSpacing(AvaloniaObject element) =>
        element.GetValue(TileSpacingProperty);

    public static void SetTileSpacing(AvaloniaObject element, double value) =>
        element.SetValue(TileSpacingProperty, value);

    public static readonly AttachedProperty<Thickness> PanelPaddingProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>(
            "PanelPadding",
            defaultValue: new Thickness(20));

    public static Thickness GetPanelPadding(AvaloniaObject element) =>
        element.GetValue(PanelPaddingProperty);

    public static void SetPanelPadding(AvaloniaObject element, Thickness value) =>
        element.SetValue(PanelPaddingProperty, value);

    public static readonly AttachedProperty<double> HeaderSpacingProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "HeaderSpacing",
            defaultValue: 10);

    public static double GetHeaderSpacing(AvaloniaObject element) =>
        element.GetValue(HeaderSpacingProperty);

    public static void SetHeaderSpacing(AvaloniaObject element, double value) =>
        element.SetValue(HeaderSpacingProperty, value);

    // Shape / borders
    public static readonly AttachedProperty<CornerRadius> TileCornerRadiusProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, CornerRadius>(
            "TileCornerRadius",
            defaultValue: new CornerRadius(12));

    public static CornerRadius GetTileCornerRadius(AvaloniaObject element) =>
        element.GetValue(TileCornerRadiusProperty);

    public static void SetTileCornerRadius(AvaloniaObject element, CornerRadius value) =>
        element.SetValue(TileCornerRadiusProperty, value);

    public static readonly AttachedProperty<Thickness> TileBorderThicknessProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>(
            "TileBorderThickness",
            defaultValue: new Thickness(0));

    public static Thickness GetTileBorderThickness(AvaloniaObject element) =>
        element.GetValue(TileBorderThicknessProperty);

    public static void SetTileBorderThickness(AvaloniaObject element, Thickness value) =>
        element.SetValue(TileBorderThicknessProperty, value);

    // Typography (defaults tuned for “TV distance”)
    public static readonly AttachedProperty<double> TitleFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "TitleFontSize",
            defaultValue: 34);

    public static double GetTitleFontSize(AvaloniaObject element) =>
        element.GetValue(TitleFontSizeProperty);

    public static void SetTitleFontSize(AvaloniaObject element, double value) =>
        element.SetValue(TitleFontSizeProperty, value);

    public static readonly AttachedProperty<double> BodyFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "BodyFontSize",
            defaultValue: 18);

    public static double GetBodyFontSize(AvaloniaObject element) =>
        element.GetValue(BodyFontSizeProperty);

    public static void SetBodyFontSize(AvaloniaObject element, double value) =>
        element.SetValue(BodyFontSizeProperty, value);

    public static readonly AttachedProperty<double> CaptionFontSizeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "CaptionFontSize",
            defaultValue: 14);

    public static double GetCaptionFontSize(AvaloniaObject element) =>
        element.GetValue(CaptionFontSizeProperty);

    public static void SetCaptionFontSize(AvaloniaObject element, double value) =>
        element.SetValue(CaptionFontSizeProperty, value);

    // Overlays / background
    public static readonly AttachedProperty<double> BackgroundDimOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "BackgroundDimOpacity",
            defaultValue: 0.35);

    public static double GetBackgroundDimOpacity(AvaloniaObject element) =>
        element.GetValue(BackgroundDimOpacityProperty);

    public static void SetBackgroundDimOpacity(AvaloniaObject element, double value) =>
        element.SetValue(BackgroundDimOpacityProperty, value);

    public static readonly AttachedProperty<double> PanelBackgroundOpacityProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, double>(
            "PanelBackgroundOpacity",
            defaultValue: 0.18);

    public static double GetPanelBackgroundOpacity(AvaloniaObject element) =>
        element.GetValue(PanelBackgroundOpacityProperty);

    public static void SetPanelBackgroundOpacity(AvaloniaObject element, double value) =>
        element.SetValue(PanelBackgroundOpacityProperty, value);
}