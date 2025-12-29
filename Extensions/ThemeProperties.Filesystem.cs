namespace Retromind.Extensions;

/// <summary>
/// Helpers for resolving portable theme-local file paths.
/// </summary>
public partial class ThemeProperties
{
    /// <summary>
    /// Gets or sets the absolute base directory of the currently active theme.
    /// Example: "/home/user/Retromind/Themes/Arcade".
    /// This is set by the host (ThemeLoader) when a theme is loaded.
    /// </summary>
    public static string? ThemeBasePath { get; set; }

    /// <summary>
    /// Combines the current ThemeBasePath with a theme-relative path.
    /// If ThemeBasePath is not set or the relative path is empty, returns null.
    /// This method uses Path.Combine semantics and is intended for use in converters
    /// or code-behind, not directly from XAML.
    /// </summary>
    /// <param name="relativePath">
    /// Path relative to the theme directory, e.g. "Images/cabinet.png" or "sounds/navigate.wav".
    /// </param>
    public static string? GetThemeFilePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(ThemeBasePath))
            return null;

        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        // Use System.IO.Path.Combine to keep it portable across platforms.
        return System.IO.Path.Combine(ThemeBasePath, relativePath);
    }

    /// <summary>
    /// Utility method for theme authors to build a portable path for images.
    /// Equivalent to GetThemeFilePath("Images/" + fileName).
    /// </summary>
    /// <param name="fileName">File name inside the "Images" subfolder, e.g. "cabinet.png".</param>
    public static string? GetThemeImagePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return GetThemeFilePath(System.IO.Path.Combine("Images", fileName));
    }
}