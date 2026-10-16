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
    /// Resolves a file below the scoped ThemeBasePath.
    /// If ThemeBasePath is not set or the relative path is empty, returns null.
    /// Rooted paths, parent traversal, symbolic links, and missing files are rejected.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the theme directory, e.g. "Images/cabinet.png" or "sounds/navigate.wav".
    /// </param>
    public static string? GetThemeFilePath(string? relativePath, Avalonia.AvaloniaObject scope)
    {
        System.ArgumentNullException.ThrowIfNull(scope);
        var basePath = GetThemeBasePath(scope);

        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        return Retromind.Helpers.ThemePathResolver.TryResolveThemeAssetPath(
            basePath,
            relativePath,
            out var fullPath)
            ? fullPath
            : null;
    }
}
