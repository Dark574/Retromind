using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Attract mode behavior (automatic selection/scrolling after idle time).
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// Enables "Attract Mode" for this theme.
    /// When true, the host may automatically scroll/select random items
    /// after a period of user inactivity (see AttractModeIdleSeconds).
    /// </summary>
    public static readonly AttachedProperty<bool> AttractModeEnabledProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "AttractModeEnabled",
            defaultValue: false);

    public static bool GetAttractModeEnabled(AvaloniaObject element) =>
        element.GetValue(AttractModeEnabledProperty);

    public static void SetAttractModeEnabled(AvaloniaObject element, bool value) =>
        element.SetValue(AttractModeEnabledProperty, value);

    /// <summary>
    /// Idle time in seconds before Attract Mode performs the first random selection.
    /// Every additional multiple of this interval will trigger another random selection
    /// while the user remains inactive. A value of 0 disables the timer.
    /// </summary>
    public static readonly AttachedProperty<int> AttractModeIdleSecondsProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, int>(
            "AttractModeIdleSeconds",
            defaultValue: 0);

    public static int GetAttractModeIdleSeconds(AvaloniaObject element) =>
        element.GetValue(AttractModeIdleSecondsProperty);

    public static void SetAttractModeIdleSeconds(AvaloniaObject element, int value) =>
        element.SetValue(AttractModeIdleSecondsProperty, value);
}