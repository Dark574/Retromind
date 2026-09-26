using Avalonia;

namespace Retromind.Extensions;

/// <summary>
/// Optional BigMode home-screen capabilities.
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// Allows the host-owned Home screen for this root theme. Themes that do
    /// not set this keep the classic category/item navigation unchanged and
    /// do not need to provide any Home-specific markup.
    /// </summary>
    public static readonly AttachedProperty<bool> SupportsHomeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, bool>(
            "SupportsHome",
            defaultValue: false);

    public static bool GetSupportsHome(AvaloniaObject element) =>
        element.GetValue(SupportsHomeProperty);

    public static void SetSupportsHome(AvaloniaObject element, bool value) =>
        element.SetValue(SupportsHomeProperty, value);
}
