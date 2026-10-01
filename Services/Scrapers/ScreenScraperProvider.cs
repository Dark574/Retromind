using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Metadata provider for the ScreenScraper Web API v2.
/// This first integration stage supplies regular title search; ROM/hash
/// identification remains a separate file-aware stage.
/// </summary>
public sealed class ScreenScraperProvider : IMetadataProvider
{
    private const string BaseUrl = "https://api.screenscraper.fr/api2";
    private const string SoftwareName = "Retromind";
    private const int MaxSearchResults = 30;

    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ScreenScraperApplicationCredentials? _applicationCredentials;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private int _isConnected;

    public ScreenScraperProvider(ScraperConfig config, HttpClient httpClient)
        : this(config, httpClient, ScreenScraperApplicationCredentials.Resolve())
    {
    }

    internal ScreenScraperProvider(
        ScraperConfig config,
        HttpClient httpClient,
        ScreenScraperApplicationCredentials? applicationCredentials)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _applicationCredentials = applicationCredentials;
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isConnected) != 0)
            return true;

        if (!HasValidCredentialShape())
            return false;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _isConnected) != 0)
                return true;

            try
            {
                var endpoint = string.IsNullOrWhiteSpace(_config.Username)
                    ? "ssinfraInfos.php"
                    : "ssuserInfos.php";
                var response = await GetJsonAsync(
                        endpoint,
                        Array.Empty<KeyValuePair<string, string>>(),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(_config.Username))
                    ValidateMemberConnection(response);

                Volatile.Write(ref _isConnected, 1);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<ScraperSearchResult>();

        if (!HasValidCredentialShape())
            throw new InvalidOperationException(
                "ScreenScraper application access is unavailable or the optional member credentials are incomplete.");

        var root = await GetJsonAsync(
                "jeuRecherche.php",
                new[] { new KeyValuePair<string, string>("recherche", query.Trim()) },
                cancellationToken)
            .ConfigureAwait(false);

        var games = FindNode(root, "jeux") as JsonArray;
        if (games == null || games.Count == 0)
            return new List<ScraperSearchResult>();

        var results = new List<ScraperSearchResult>(Math.Min(games.Count, MaxSearchResults));
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var language = LanguageCodeHelper.NormalizePrimaryCode(_config.Language);

        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (game is not JsonObject gameObject)
                continue;

            var id = ReadText(gameObject["id"]);
            var title = SelectLocalizedText(gameObject["noms"], language, isRegion: true)
                        ?? ReadText(gameObject["nom"]);
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(title) ||
                !seenIds.Add(id))
            {
                continue;
            }

            var result = new ScraperSearchResult
            {
                Source = "ScreenScraper",
                Id = id,
                Title = title,
                Description = SelectLocalizedText(gameObject["synopsis"], language, isRegion: false) ?? string.Empty,
                Developer = ReadText(gameObject["developpeur"]),
                Publisher = ReadText(gameObject["editeur"]),
                MaxPlayers = ReadText(gameObject["joueurs"]),
                Platform = ReadText(gameObject["systeme"]),
                Genre = SelectGenre(gameObject["genres"], language),
                ReleaseDate = SelectReleaseDate(gameObject["dates"], language),
                Rating = NormalizeRating(gameObject["note"]),
                CoverUrl = SelectMediaUrl(gameObject["medias"], language, "box-2D", "box-3D"),
                WallpaperUrl = SelectMediaUrl(gameObject["medias"], language, "fanart"),
                ScreenshotUrl = SelectMediaUrl(gameObject["medias"], language, "ss", "sstitle"),
                LogoUrl = SelectMediaUrl(gameObject["medias"], language, "wheel-hd", "wheel"),
                MarqueeUrl = SelectMediaUrl(gameObject["medias"], language, "marquee", "screenmarquee"),
                BezelUrl = SelectMediaUrl(gameObject["medias"], language, "bezel-16-9", "bezel-16-10", "bezel-4-3"),
                ControlPanelUrl = SelectMediaUrl(gameObject["medias"], language, "cpanel", "controlpanel")
            };

            results.Add(result);
            if (results.Count >= MaxSearchResults)
                break;
        }

        return results;
    }

    private bool HasValidCredentialShape()
    {
        if (_applicationCredentials == null)
            return false;

        var hasMemberName = !string.IsNullOrWhiteSpace(_config.Username);
        var hasMemberPassword = !string.IsNullOrWhiteSpace(_config.Password);
        return hasMemberName == hasMemberPassword;
    }

    private void ValidateMemberConnection(JsonNode root)
    {
        if (FindNode(root, "ssuser") is not JsonObject member)
            throw new InvalidOperationException("ScreenScraper did not confirm the configured member account.");

        var confirmedUsername = ReadText(member["id"]);
        var configuredUsername = _config.Username!.Trim();
        if (string.IsNullOrWhiteSpace(confirmedUsername) ||
            !string.Equals(confirmedUsername, configuredUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ScreenScraper returned a different member account than configured.");
        }
    }

    private async Task<JsonNode> GetJsonAsync(
        string endpoint,
        IEnumerable<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("devid", _applicationCredentials!.DeveloperId),
            new("devpassword", _applicationCredentials.DeveloperPassword),
            new("softname", SoftwareName),
            new("output", "json")
        };

        if (!string.IsNullOrWhiteSpace(_config.Username))
        {
            query.Add(new KeyValuePair<string, string>("ssid", _config.Username.Trim()));
            query.Add(new KeyValuePair<string, string>("sspassword", _config.Password!));
        }

        query.AddRange(parameters);
        var queryString = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .GetAsync($"{BaseUrl}/{endpoint}?{queryString}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Do not retain an exception containing a request URI with credentials.
            throw new InvalidOperationException("The ScreenScraper service could not be reached.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("ScreenScraper rejected the configured credentials.");

            if ((int)response.StatusCode == 429)
                throw new InvalidOperationException("The ScreenScraper request limit has been reached. Please try again later.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"ScreenScraper returned HTTP {(int)response.StatusCode}.");

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(payload);
            }
            catch
            {
                throw new InvalidOperationException("ScreenScraper returned an invalid response.");
            }

            if (root == null)
                throw new InvalidOperationException("ScreenScraper returned an empty response.");

            var apiError = ReadText(FindNode(root, "erreur")) ?? ReadText(FindNode(root, "error"));
            if (!string.IsNullOrWhiteSpace(apiError))
                throw new InvalidOperationException(SanitizeApiError(apiError));

            return root;
        }
    }

    private string SanitizeApiError(string message)
    {
        var sanitized = message;
        foreach (var secret in new[] { _applicationCredentials?.DeveloperPassword, _config.Password })
        {
            if (!string.IsNullOrEmpty(secret))
                sanitized = sanitized.Replace(secret, "***", StringComparison.Ordinal);
        }

        return $"ScreenScraper error: {sanitized}";
    }

    private static JsonNode? FindNode(JsonNode root, string name)
    {
        if (root is JsonObject rootObject && rootObject.TryGetPropertyValue(name, out var direct))
            return direct;

        if (root is JsonObject container &&
            container["response"] is JsonObject responseObject &&
            responseObject.TryGetPropertyValue(name, out var nested))
        {
            return nested;
        }

        return null;
    }

    private static string? ReadText(JsonNode? node)
    {
        if (node == null)
            return null;

        if (node is JsonValue)
            return node.ToString().Trim() is { Length: > 0 } value ? value : null;

        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "text", "nom", "name", "value" })
            {
                var value = ReadText(obj[key]);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string? SelectLocalizedText(JsonNode? node, string language, bool isRegion)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in BuildLocalizedObjectKeys(language, isRegion))
            {
                var value = ReadText(obj[key]);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return ReadText(obj);
        }

        if (node is not JsonArray entries)
            return ReadText(node);

        var preferredCodes = BuildPreferredCodes(language, isRegion);
        foreach (var code in preferredCodes)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                var entryCode = ReadText(entry[isRegion ? "region" : "langue"])
                                ?? ReadText(entry["language"]);
                if (string.Equals(entryCode, code, StringComparison.OrdinalIgnoreCase))
                    return ReadText(entry["text"]) ?? ReadText(entry);
            }
        }

        return entries.OfType<JsonObject>()
            .Select(entry => ReadText(entry["text"]) ?? ReadText(entry))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IReadOnlyList<string> BuildPreferredCodes(string language, bool isRegion)
    {
        if (!isRegion)
            return language == "de" ? new[] { "de", "en", "fr" } : new[] { "en", "de", "fr" };

        return language == "de"
            ? new[] { "de", "eu", "wor", "ss", "us" }
            : new[] { "wor", "us", "eu", "ss", "uk" };
    }

    private static IEnumerable<string> BuildLocalizedObjectKeys(string language, bool isRegion)
    {
        var prefix = isRegion ? "nom_" : "synopsis_";
        return BuildPreferredCodes(language, isRegion).Select(code => prefix + code);
    }

    private static string? SelectGenre(JsonNode? node, string language)
    {
        if (node is JsonObject genreObject)
        {
            var mainGenreIndex = FindMainGenreIndex(genreObject["genres_id"] as JsonArray);
            foreach (var code in BuildPreferredCodes(language, isRegion: false))
            {
                if (genreObject[$"genres_{code}"] is not JsonArray localizedGenres || localizedGenres.Count == 0)
                    continue;

                var selectedIndex = Math.Clamp(mainGenreIndex, 0, localizedGenres.Count - 1);
                var value = ReadText(localizedGenres[selectedIndex]?[$"genre_{code}"])
                            ?? ReadText(localizedGenres[selectedIndex]);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        if (node is not JsonArray genres)
            return SelectLocalizedText(node, language, isRegion: false);

        var genre = genres.OfType<JsonObject>()
                        .FirstOrDefault(item => ReadText(item["principale"]) == "1")
                    ?? genres.OfType<JsonObject>().FirstOrDefault();
        if (genre == null)
            return null;

        return SelectLocalizedText(genre["noms"], language, isRegion: false)
               ?? ReadText(genre["nom"])
               ?? ReadText(genre["nomcourt"]);
    }

    private static DateTime? SelectReleaseDate(JsonNode? node, string language)
    {
        string? value;
        if (node is JsonObject dates)
        {
            value = BuildPreferredCodes(language, isRegion: true)
                .Select(code => ReadText(dates[$"date_{code}"]))
                .FirstOrDefault(date => !string.IsNullOrWhiteSpace(date));
        }
        else
        {
            value = SelectLocalizedText(node, language, isRegion: true);
        }

        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static double? NormalizeRating(JsonNode? node)
    {
        var text = ReadText(node);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
            return null;

        return Math.Clamp(rating * 5.0, 0, 100);
    }

    private static string? SelectMediaUrl(JsonNode? node, string language, params string[] types)
    {
        if (node is JsonObject mediaObject)
            return SelectObjectMediaUrl(mediaObject, language, types);

        if (node is not JsonArray mediaEntries)
            return null;

        foreach (var type in types)
        {
            var candidates = mediaEntries
                .OfType<JsonObject>()
                .Where(media => string.Equals(ReadText(media["type"]), type, StringComparison.OrdinalIgnoreCase))
                .Select(media => new
                {
                    Url = ReadText(media["url"]),
                    Region = ReadText(media["region"])
                })
                .Where(media => !string.IsNullOrWhiteSpace(media.Url))
                .ToList();

            foreach (var region in BuildPreferredCodes(language, isRegion: true))
            {
                var preferred = candidates.FirstOrDefault(media =>
                    string.Equals(media.Region, region, StringComparison.OrdinalIgnoreCase));
                if (preferred != null)
                    return preferred.Url;
            }

            if (candidates.Count > 0)
                return candidates[0].Url;
        }

        return null;
    }

    private static int FindMainGenreIndex(JsonArray? genreIds)
    {
        if (genreIds == null)
            return 0;

        for (var index = 0; index < genreIds.Count; index++)
        {
            if (genreIds[index] is JsonObject genre && ReadText(genre["principale"]) == "1")
                return index;
        }

        return 0;
    }

    private static string? SelectObjectMediaUrl(JsonObject medias, string language, IReadOnlyList<string> types)
    {
        foreach (var type in types)
        {
            var value = type switch
            {
                "ss" or "sstitle" => ReadText(medias["media_screenshot"]),
                "fanart" => ReadText(medias["media_fanart"]),
                "marquee" => ReadText(medias["media_marquee"]),
                "screenmarquee" => ReadText(medias["media_screenmarquee"]),
                "wheel" or "wheel-hd" => SelectRegionalMedia(
                    medias["media_wheels"], "media_wheel_", language),
                "box-2D" => SelectRegionalMedia(
                    medias["media_boitiers"]?["media_boitiers_2d"], "media_boitier_2d_", language),
                "box-3D" => SelectRegionalMedia(
                    medias["media_boitiers"]?["media_boitiers_3d"], "media_boitier_3d_", language),
                "bezel-4-3" => SelectRegionalMedia(
                    medias["media_bezels"]?["media_bezel4-3"], "media_bezel4-3_", language),
                "bezel-16-9" => SelectRegionalMedia(
                    medias["media_bezels"]?["media_bezel16-9"], "media_bezel16-9_", language),
                "bezel-16-10" => SelectRegionalMedia(
                    medias["media_bezels"]?["media_bezel16-10"], "media_bezel16-10_", language),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? SelectRegionalMedia(JsonNode? node, string keyPrefix, string language)
    {
        if (node is not JsonObject media)
            return ReadText(node);

        foreach (var region in BuildPreferredCodes(language, isRegion: true))
        {
            var value = ReadText(media[keyPrefix + region]);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return media
            .Where(pair => pair.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(pair => ReadText(pair.Value))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
