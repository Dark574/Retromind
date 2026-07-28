namespace Retromind.Helpers;

public static class LanguageCodeHelper
{
    public static string NormalizePrimaryCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";

        var dashIndex = language.IndexOf('-');
        if (dashIndex > 0)
            language = language[..dashIndex];

        return language.Trim().ToLowerInvariant();
    }
}
