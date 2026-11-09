using Avalonia;

namespace Retromind.Extensions;

/// <summary>Presentation only: all modes use the same library navigation session.</summary>
public enum NavigationPresentation
{
    LegacySeparate,
    MixedEntries,
    MediaItemsOnly,
    NodeBrowser
}

public partial class ThemeProperties
{
    public static readonly AttachedProperty<NavigationPresentation> NavigationPresentationProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, NavigationPresentation>(
            "NavigationPresentation", NavigationPresentation.LegacySeparate);

    public static NavigationPresentation GetNavigationPresentation(AvaloniaObject element) =>
        element.GetValue(NavigationPresentationProperty);

    public static void SetNavigationPresentation(AvaloniaObject element, NavigationPresentation value) =>
        element.SetValue(NavigationPresentationProperty, value);
}
