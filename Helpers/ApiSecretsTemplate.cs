namespace Retromind.Helpers;

// IMPORTANT FOR DEVELOPERS:
// This is just a template. NEVER commit your actual API keys to version control!
// 1. Copy this file or rename it to "ApiSecrets.cs".
// 2. Rename the class below from "ApiSecretsTemplate" to "ApiSecrets".
// 3. Enter your personal API keys.
// 4. Ensure "ApiSecrets.cs" is listed in your .gitignore file.

// NOTE:
// The main Retromind application reads scraper API keys from its configuration
// (e.g. the scraper settings dialog). This template is only for custom tools
// or experiments and is not used by the built-in scrapers.

public static class ApiSecretsTemplate
{
    // The Movie Database (TMDB) API Key
    public static string TmdbApiKey { get; } = "YOUR_TMDB_KEY_HERE";

    // Internet Game Database (IGDB) Credentials (via Twitch Developer Console)
    public static string IgdbClientId { get; } = "YOUR_CLIENT_ID_HERE";
    public static string IgdbClientSecret { get; } = "YOUR_CLIENT_SECRET_HERE";
}