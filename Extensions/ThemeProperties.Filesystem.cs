namespace Retromind.Extensions;

/// <summary>
/// Helpers for resolving portable theme-local file paths.
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// Per-theme base directory stored on the root view instance.
    /// This allows multiple themes to coexist without global state.
    /// </summary>
    public static readonly Avalonia.AttachedProperty<string?> ThemeBasePathProperty =
        Avalonia.AvaloniaProperty.RegisterAttached<ThemeProperties, Avalonia.AvaloniaObject, string?>(
            "ThemeBasePath");

    public static string? GetThemeBasePath(Avalonia.AvaloniaObject element) =>
        element.GetValue(ThemeBasePathProperty);

    public static void SetThemeBasePath(Avalonia.AvaloniaObject element, string? value) =>
        element.SetValue(ThemeBasePathProperty, value);

    /// <summary>
    /// Legacy/global fallback for theme base path.
    /// Prefer the per-view attached property when possible.
    /// </summary>
    public static string? GlobalThemeBasePath { get; set; }

    /// <summary>
    /// Combines the current ThemeBasePath with a theme-relative path.
    /// If ThemeBasePath is not set or the relative path is empty, returns null.
    /// This method uses Path.Combine semantics and is intended for use in converters
    /// or code-behind, not directly from XAML.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the theme directory, e.g. "Images/cabinet.png" or "sounds/navigate.wav".
    /// </param>
    public static string? GetThemeFilePath(string? relativePath, Avalonia.AvaloniaObject? scope = null)
    {
        var basePath = scope != null ? GetThemeBasePath(scope) : GlobalThemeBasePath;

        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        // Use System.IO.Path.Combine to keep it portable across platforms.
        return System.IO.Path.Combine(basePath, relativePath);
    }

    /// <summary>
    /// Utility method for theme authors to build a portable path for images.
    /// Equivalent to GetThemeFilePath("Images/" + fileName).
    /// </summary>
    /// <param name="fileName">File name inside the "Images" subfolder, e.g. "cabinet.png".</param>
    public static string? GetThemeImagePath(string? fileName, Avalonia.AvaloniaObject? scope = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return GetThemeFilePath(System.IO.Path.Combine("Images", fileName), scope);
    }
}
