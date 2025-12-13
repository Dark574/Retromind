namespace Retromind.Models;

/// <summary>
/// Holds the sound file paths defined by a theme.
/// Paths are relative to the theme's root directory.
/// </summary>
public class ThemeSounds
{
    public string? Navigate { get; init; }
    public string? Confirm { get; init; }
    public string? Cancel { get; init; }
}