using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Video-related theme properties (screen overlays, stretch, etc.).
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// If false, the host will disable the video overlay even if a video slot exists.
    /// </summary>
    public static readonly AttachedProperty<bool> VideoEnabledProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "VideoEnabled",
            defaultValue: true);

    public static bool GetVideoEnabled(AvaloniaObject element) =>
        element.GetValue(VideoEnabledProperty);

    public static void SetVideoEnabled(AvaloniaObject element, bool value) =>
        element.SetValue(VideoEnabledProperty, value);

    /// <summary>
    /// Logical name of the video slot element in the theme XAML.
    /// </summary>
    public static readonly AttachedProperty<string> VideoSlotNameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "VideoSlotName",
            defaultValue: "VideoSlot");

    public static string GetVideoSlotName(AvaloniaObject element) =>
        element.GetValue(VideoSlotNameProperty);

    public static void SetVideoSlotName(AvaloniaObject element, string value) =>
        element.SetValue(VideoSlotNameProperty, value);

    /// <summary>
    /// Corner radius for the video overlay border. Default: 12 (matches tiles).
    /// </summary>
    public static readonly AttachedProperty<CornerRadius> VideoCornerRadiusProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, CornerRadius>(
            "VideoCornerRadius",
            defaultValue: new CornerRadius(12));

    public static CornerRadius GetVideoCornerRadius(AvaloniaObject element) =>
        element.GetValue(VideoCornerRadiusProperty);

    public static void SetVideoCornerRadius(AvaloniaObject element, CornerRadius value) =>
        element.SetValue(VideoCornerRadiusProperty, value);

    /// <summary>
    /// Border thickness for the video overlay. Default: 0 (no border).
    /// </summary>
    public static readonly AttachedProperty<Thickness> VideoBorderThicknessProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Thickness>(
            "VideoBorderThickness",
            defaultValue: new Thickness(0));

    public static Thickness GetVideoBorderThickness(AvaloniaObject element) =>
        element.GetValue(VideoBorderThicknessProperty);

    public static void SetVideoBorderThickness(AvaloniaObject element, Thickness value) =>
        element.SetValue(VideoBorderThicknessProperty, value);

    /// <summary>
    /// Controls how the video is scaled inside the slot.
    /// Supported values (convention, interpreted by host):
    /// "Fill" (default), "Uniform", "UniformToFill".
    /// </summary>
    public static readonly AttachedProperty<string> VideoStretchModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string>(
            "VideoStretchMode",
            defaultValue: "Fill");

    public static string GetVideoStretchMode(AvaloniaObject element) =>
        element.GetValue(VideoStretchModeProperty);

    public static void SetVideoStretchMode(AvaloniaObject element, string value) =>
        element.SetValue(VideoStretchModeProperty, value);

    /// <summary>
    /// Relative path to an optional secondary background video for the theme
    /// (e.g. "Videos/background_loop.mp4"). Resolved relative to the theme base folder.
    /// </summary>
    public static readonly AttachedProperty<string?> SecondaryBackgroundVideoPathProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "SecondaryBackgroundVideoPath");

    public static string? GetSecondaryBackgroundVideoPath(AvaloniaObject element) =>
        element.GetValue(SecondaryBackgroundVideoPathProperty);

    public static void SetSecondaryBackgroundVideoPath(AvaloniaObject element, string? value) =>
        element.SetValue(SecondaryBackgroundVideoPathProperty, value);
}