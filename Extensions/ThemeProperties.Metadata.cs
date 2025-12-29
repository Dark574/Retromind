using Avalonia;
using Avalonia.Media;

namespace Retromind.Extensions;

/// <summary>
/// Theme metadata and simple styling hints.
/// </summary>
public partial class ThemeProperties
{
    public static readonly AttachedProperty<string?> NameProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("Name");

    public static string? GetName(AvaloniaObject element) =>
        element.GetValue(NameProperty);

    public static void SetName(AvaloniaObject element, string? value) =>
        element.SetValue(NameProperty, value);

    public static readonly AttachedProperty<string?> AuthorProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("Author");

    public static string? GetAuthor(AvaloniaObject element) =>
        element.GetValue(AuthorProperty);

    public static void SetAuthor(AvaloniaObject element, string? value) =>
        element.SetValue(AuthorProperty, value);

    public static readonly AttachedProperty<string?> VersionProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("Version");

    public static string? GetVersion(AvaloniaObject element) =>
        element.GetValue(VersionProperty);

    public static void SetVersion(AvaloniaObject element, string? value) =>
        element.SetValue(VersionProperty, value);

    public static readonly AttachedProperty<string?> WebsiteUrlProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("WebsiteUrl");

    public static string? GetWebsiteUrl(AvaloniaObject element) =>
        element.GetValue(WebsiteUrlProperty);

    public static void SetWebsiteUrl(AvaloniaObject element, string? value) =>
        element.SetValue(WebsiteUrlProperty, value);

    // Optional visual hint for accent color
    public static readonly AttachedProperty<Color?> AccentColorProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, Color?>("AccentColor");

    public static Color? GetAccentColor(AvaloniaObject element) =>
        element.GetValue(AccentColorProperty);

    public static void SetAccentColor(AvaloniaObject element, Color? value) =>
        element.SetValue(AccentColorProperty, value);
}