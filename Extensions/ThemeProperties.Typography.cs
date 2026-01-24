using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Typography-related theme properties (font families).
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// Font family spec for title text (system name or theme-relative font file).
    /// Example: "Fonts/Fraunces-SemiBold.ttf"
    /// </summary>
    public static readonly AttachedProperty<string?> TitleFontFamilyProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "TitleFontFamily");

    public static string? GetTitleFontFamily(AvaloniaObject element) =>
        element.GetValue(TitleFontFamilyProperty);

    public static void SetTitleFontFamily(AvaloniaObject element, string? value) =>
        element.SetValue(TitleFontFamilyProperty, value);

    /// <summary>
    /// Font family spec for body text (system name or theme-relative font file).
    /// Example: "Fonts/IBMPlexSans-Regular.ttf"
    /// </summary>
    public static readonly AttachedProperty<string?> BodyFontFamilyProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "BodyFontFamily");

    public static string? GetBodyFontFamily(AvaloniaObject element) =>
        element.GetValue(BodyFontFamilyProperty);

    public static void SetBodyFontFamily(AvaloniaObject element, string? value) =>
        element.SetValue(BodyFontFamilyProperty, value);

    /// <summary>
    /// Font family spec for caption text (system name or theme-relative font file).
    /// Example: "Fonts/IBMPlexSans-SemiBold.ttf"
    /// </summary>
    public static readonly AttachedProperty<string?> CaptionFontFamilyProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "CaptionFontFamily");

    public static string? GetCaptionFontFamily(AvaloniaObject element) =>
        element.GetValue(CaptionFontFamilyProperty);

    public static void SetCaptionFontFamily(AvaloniaObject element, string? value) =>
        element.SetValue(CaptionFontFamilyProperty, value);

    /// <summary>
    /// Font family spec for mono text (system name or theme-relative font file).
    /// Example: "Fonts/IBMPlexMono-Regular.ttf"
    /// </summary>
    public static readonly AttachedProperty<string?> MonoFontFamilyProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "MonoFontFamily");

    public static string? GetMonoFontFamily(AvaloniaObject element) =>
        element.GetValue(MonoFontFamilyProperty);

    public static void SetMonoFontFamily(AvaloniaObject element, string? value) =>
        element.SetValue(MonoFontFamilyProperty, value);
}
