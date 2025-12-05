namespace Retromind.Helpers;

// HINWEIS FÜR ENTWICKLER:
// Dies ist nur eine Vorlage. Bitte diese Datei NICHT direkt verwenden.
// 1. Kopiere diese Datei oder benenne sie um in "ApiSecrets.cs"
// 2. Benenne die Klasse unten von "ApiSecretsTemplate" in "ApiSecrets" um
// 3. Trage deinen API Keys ein

public static class ApiSecretsTemplate
{
    // TMDB
    public static string TmdbApiKey { get; } = "DEIN_TMDB_KEY";

    // IGDB (Twitch Developer Console)
    public static string IgdbClientId { get; } = "DEINE_CLIENT_ID";
    public static string IgdbClientSecret { get; } = "DEIN_CLIENT_SECRET";

    // Google Books (Optional, erhöht Limits)
    public static string GoogleBooksApiKey { get; } = ""; 

    // ComicVine (Optional)
    public static string ComicVineApiKey { get; } = "";
}