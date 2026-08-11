namespace Retromind.Resources;

/// <summary>
/// Exposes localized text to themes that are loaded from XAML at runtime.
/// </summary>
public static class ThemeStrings
{
    public static string GameCountLabel =>
        Strings.ResourceManager.GetString("BigMode_GameCountLabel", Strings.Culture) ?? "Games";
}
