using Avalonia;

namespace Retromind.Extensions;

public class ThemeProperties : AvaloniaObject
{
    // Sound for navigation actions
    public static readonly AttachedProperty<string?> NavigateSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("NavigateSound");

    public static string? GetNavigateSound(AvaloniaObject element) => element.GetValue(NavigateSoundProperty);
    public static void SetNavigateSound(AvaloniaObject element, string? value) => element.SetValue(NavigateSoundProperty, value);

    // Sound for confirmation actions
    public static readonly AttachedProperty<string?> ConfirmSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("ConfirmSound");
    
    public static string? GetConfirmSound(AvaloniaObject element) => element.GetValue(ConfirmSoundProperty);
    public static void SetConfirmSound(AvaloniaObject element, string? value) => element.SetValue(ConfirmSoundProperty, value);

    // Sound for cancel/back actions
    public static readonly AttachedProperty<string?> CancelSoundProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, string?>("CancelSound");
    
    public static string? GetCancelSound(AvaloniaObject element) => element.GetValue(CancelSoundProperty);
    public static void SetCancelSound(AvaloniaObject element, string? value) => element.SetValue(CancelSoundProperty, value);
}