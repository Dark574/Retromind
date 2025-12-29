using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Attached properties for theme-related sound effects.
/// </summary>
public partial class ThemeProperties
{
    // Sound for navigation actions
    public static readonly AttachedProperty<string?> NavigateSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("NavigateSound");

    public static string? GetNavigateSound(AvaloniaObject element) =>
        element.GetValue(NavigateSoundProperty);

    public static void SetNavigateSound(AvaloniaObject element, string? value) =>
        element.SetValue(NavigateSoundProperty, value);

    // Sound for confirmation actions
    public static readonly AttachedProperty<string?> ConfirmSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("ConfirmSound");

    public static string? GetConfirmSound(AvaloniaObject element) =>
        element.GetValue(ConfirmSoundProperty);

    public static void SetConfirmSound(AvaloniaObject element, string? value) =>
        element.SetValue(ConfirmSoundProperty, value);

    // Sound for cancel/back actions
    public static readonly AttachedProperty<string?> CancelSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("CancelSound");

    public static string? GetCancelSound(AvaloniaObject element) =>
        element.GetValue(CancelSoundProperty);

    public static void SetCancelSound(AvaloniaObject element, string? value) =>
        element.SetValue(CancelSoundProperty, value);

    /// <summary>
    /// Optional theme-local sound that is played when Attract Mode kicks in
    /// (e.g. a short spin / "roulette" sound). Path is relative to the theme directory.
    /// </summary>
    public static readonly AttachedProperty<string?> AttractModeSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>(
            "AttractModeSound");

    public static string? GetAttractModeSound(AvaloniaObject element) =>
        element.GetValue(AttractModeSoundProperty);

    public static void SetAttractModeSound(AvaloniaObject element, string? value) =>
        element.SetValue(AttractModeSoundProperty, value);
}