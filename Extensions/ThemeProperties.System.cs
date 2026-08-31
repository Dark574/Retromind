using Avalonia;
using Retromind.Models;

namespace Retromind.Extensions;

/// <summary>
/// Presentation context assigned to individual system-subtheme instances.
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// The node represented by this system-subtheme instance. Unlike the live
    /// BigMode selection, this value remains stable while an old theme fades out.
    /// </summary>
    public static readonly AttachedProperty<MediaNode?> SystemPreviewNodeProperty =
        AvaloniaProperty.RegisterAttached<ThemeProperties, AvaloniaObject, MediaNode?>(
            "SystemPreviewNode");

    public static MediaNode? GetSystemPreviewNode(AvaloniaObject element) =>
        element.GetValue(SystemPreviewNodeProperty);

    public static void SetSystemPreviewNode(AvaloniaObject element, MediaNode? value) =>
        element.SetValue(SystemPreviewNodeProperty, value);
}
