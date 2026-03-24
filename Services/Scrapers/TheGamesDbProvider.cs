using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Metadata provider for TheGamesDB API (v1).
/// </summary>
public class TheGamesDbProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;

    private const string BaseUrl = "https://api.thegamesdb.net/v1";
    private const int MaxSearchResults = 40;
    private const int MaxPages = 5;

    public TheGamesDbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    private string GetApiKey()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new Exception("TheGamesDB requires an API key. Please enter it in the scraper settings.");

        return _config.ApiKey;
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = GetApiKey();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(query))
            return new List<ScraperSearchResult>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var language = NormalizeLanguage(_config.Language);
            var results = new List<ScraperSearchResult>(MaxSearchResults);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var page = 1; page <= MaxPages && results.Count < MaxSearchResults; page++)
            {
                var url =
                    $"{BaseUrl}/Games/ByGameName?apikey={Uri.EscapeDataString(apiKey)}&name={encodedQuery}" +
                    "&fields=overview,genres,publishers,players,platform,rating" +
                    "&include=boxart,platform" +
                    $"&filter%5Blanguage%5D={Uri.EscapeDataString(language)}" +
                    $"&page={page}";

                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var root = JsonNode.Parse(json);

                var games = root?["data"]?["games"]?.AsArray();
                if (games == null || games.Count == 0)
                    break;

                var platformData = root?["include"]?["platform"]?["data"];
                var boxartData = root?["include"]?["boxart"]?["data"] as JsonObject;
                var boxartBaseUrl = ResolveBoxartBaseUrl(root);
                var platformNameById = await BuildPlatformNameMapAsync(apiKey, games, platformData, cancellationToken)
                    .ConfigureAwait(false);

                var countBeforePage = results.Count;

                foreach (var game in games)
                {
                    if (game == null)
                        continue;

                    var id = game["id"]?.ToString() ?? string.Empty;
                    var title = game["game_title"]?.ToString() ?? "Unknown";
                    var dedupeKey = string.IsNullOrWhiteSpace(id) ? title : id;
                    if (!seen.Add(dedupeKey))
                        continue;

                    var result = new ScraperSearchResult
                    {
                        Source = "TheGamesDB",
                        Id = id,
                        Title = title,
                        Description = game["overview"]?.ToString() ?? string.Empty,
                        Developer = FirstStringFromArray(game["developers"] as JsonArray)
                                    ?? FirstStringFromArray(game["publishers"] as JsonArray),
                        Genre = JoinArrayValues(game["genres"] as JsonArray)
                    };

                    var rawRatingText = game["rating"]?.ToString();
                    if (TryParseRating(rawRatingText, out var rawRating))
                    {
                        result.Rating = rawRating;
                    }

                    if (DateTime.TryParse(game["release_date"]?.ToString(), out var releaseDate))
                    {
                        result.ReleaseDate = releaseDate;
                    }

                    result.Platform = ResolvePlatform(game, platformNameById);

                    if (!string.IsNullOrWhiteSpace(id) && boxartData != null && boxartData[id] is JsonArray artArray)
                    {
                        result.CoverUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => string.Equals(a["type"]?.ToString(), "boxart", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(a["side"]?.ToString(), "front", StringComparison.OrdinalIgnoreCase))
                                         ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                             a => string.Equals(a["type"]?.ToString(), "boxart", StringComparison.OrdinalIgnoreCase))
                                         ?? SelectBoxartUrl(artArray, boxartBaseUrl, _ => true);

                        result.WallpaperUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => string.Equals(a["type"]?.ToString(), "fanart", StringComparison.OrdinalIgnoreCase))
                                             ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                 a => string.Equals(a["type"]?.ToString(), "screenshot", StringComparison.OrdinalIgnoreCase))
                                             ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                 a => string.Equals(a["type"]?.ToString(), "banner", StringComparison.OrdinalIgnoreCase));

                        // TheGamesDB artwork naming can vary by entry (clearlogo/logo variants).
                        result.LogoUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => TypeEqualsOrContains(a["type"]?.ToString(), "clearlogo"))
                                         ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                             a => TypeEqualsOrContains(a["type"]?.ToString(), "logo"));

                        // For arcade systems (e.g. MAME), marquee can appear as "marquee" or banner-like artwork.
                        result.MarqueeUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => TypeEqualsOrContains(a["type"]?.ToString(), "marquee"))
                                            ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                a => TypeEqualsOrContains(a["type"]?.ToString(), "wheel"))
                                            ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                a => TypeEqualsOrContains(a["type"]?.ToString(), "banner"));
                    }

                    results.Add(result);
                    if (results.Count >= MaxSearchResults)
                        break;
                }

                // If pagination is ignored by the API and we keep receiving the same page, stop early.
                if (results.Count == countBeforePage)
                    break;
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"TheGamesDB error: {ex.Message}", ex);
        }
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";

        var dashIndex = language.IndexOf('-');
        if (dashIndex > 0)
            language = language[..dashIndex];

        return language.Trim().ToLowerInvariant();
    }

    private static string ResolveBoxartBaseUrl(JsonNode? root)
    {
        var boxartBase = root?["include"]?["boxart"]?["base_url"];
        var preferred = boxartBase?["original"]?.ToString()
                        ?? boxartBase?["large"]?.ToString()
                        ?? boxartBase?["medium"]?.ToString()
                        ?? string.Empty;

        return preferred?.TrimEnd('/') ?? string.Empty;
    }

    private static string? SelectBoxartUrl(JsonArray array, string baseUrl, Func<JsonNode, bool> predicate)
    {
        var match = array.FirstOrDefault(node => node != null && predicate(node));
        if (match == null)
            return null;

        var fileName = match["filename"]?.ToString();
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (fileName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        if (string.IsNullOrEmpty(baseUrl))
            return null;

        return $"{baseUrl}/{fileName.TrimStart('/')}";
    }

    private static string? FirstStringFromArray(JsonArray? array)
    {
        if (array == null || array.Count == 0)
            return null;

        return array
            .Select(v => v?.ToString())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string? JoinArrayValues(JsonArray? array)
    {
        if (array == null || array.Count == 0)
            return null;

        var values = array
            .Select(v => v?.ToString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();

        return values.Length == 0 ? null : string.Join(", ", values);
    }

    private static bool TypeEqualsOrContains(string? actualType, string expected)
    {
        if (string.IsNullOrWhiteSpace(actualType))
            return false;

        return actualType.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               actualType.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRating(string? raw, out double rating)
    {
        rating = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var cleaned = raw.Trim();
        var m = Regex.Match(cleaned, @"(?<num>\d+(?:[.,]\d+)?)\s*(?:/\s*(?<den>\d+(?:[.,]\d+)?))?");
        if (!m.Success)
            return false;

        if (!TryParseFlexibleNumber(m.Groups["num"].Value, out var numerator))
            return false;

        if (m.Groups["den"].Success && TryParseFlexibleNumber(m.Groups["den"].Value, out var denominator) && denominator > 0)
        {
            rating = (numerator / denominator) * 100.0;
            rating = Math.Clamp(rating, 0, 100);
            return true;
        }

        if (cleaned.Contains('%'))
        {
            rating = Math.Clamp(numerator, 0, 100);
            return true;
        }

        rating = numerator <= 10 ? numerator * 10 : numerator;
        rating = Math.Clamp(rating, 0, 100);
        return true;
    }

    private static bool TryParseFlexibleNumber(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private async Task<Dictionary<string, string>> BuildPlatformNameMapAsync(
        string apiKey,
        JsonArray games,
        JsonNode? includePlatformData,
        CancellationToken cancellationToken)
    {
        var map = ExtractPlatformNameMapFromInclude(includePlatformData);

        var neededIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            if (game == null)
                continue;

            foreach (var id in ExtractPlatformIds(game))
            {
                if (!map.ContainsKey(id))
                    neededIds.Add(id);
            }
        }

        if (neededIds.Count == 0)
            return map;

        // Fallback query: some Games/ByGameName responses do not return include.platform mappings.
        try
        {
            var idsCsv = string.Join(",", neededIds);
            var url = $"{BaseUrl}/Platforms/ByPlatformID?apikey={Uri.EscapeDataString(apiKey)}&id={Uri.EscapeDataString(idsCsv)}";

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return map;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            var platformsNode = root?["data"]?["platforms"];
            var resolved = ExtractPlatformNameMapFromPlatformsNode(platformsNode);
            foreach (var kv in resolved)
                map[kv.Key] = kv.Value;
        }
        catch
        {
            // best effort: keep names already resolved from include payload
        }

        return map;
    }

    private static string? ResolvePlatform(JsonNode game, IReadOnlyDictionary<string, string> platformNameById)
    {
        var names = new List<string>();
        foreach (var id in ExtractPlatformIds(game))
        {
            if (!platformNameById.TryGetValue(id, out var name) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names.Count == 0
            ? null
            : string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractPlatformIds(JsonNode game)
    {
        static IEnumerable<string> SplitIds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                    yield return part;
            }
        }

        foreach (var id in SplitIds(game["platform"]?.ToString()))
            yield return id;

        if (game["platforms"] is JsonArray arr)
        {
            foreach (var p in arr)
            {
                var value = p?["id"]?.ToString() ?? p?.ToString();
                foreach (var id in SplitIds(value))
                    yield return id;
            }
        }
    }

    private static Dictionary<string, string> ExtractPlatformNameMapFromInclude(JsonNode? platformData)
    {
        if (platformData == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return ExtractPlatformNameMapFromPlatformsNode(platformData);
    }

    private static Dictionary<string, string> ExtractPlatformNameMapFromPlatformsNode(JsonNode? platformsNode)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (platformsNode == null)
            return result;

        // Shape A: object keyed by platform id.
        if (platformsNode is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value == null)
                    continue;

                var id = kv.Key;
                var node = kv.Value;
                var embeddedId = node["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(embeddedId))
                    id = embeddedId;

                var name = node["name"]?.ToString() ?? node.ToString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    result[id] = name;
            }
        }

        // Shape B: array of platform objects.
        if (platformsNode is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node == null)
                    continue;

                var id = node["id"]?.ToString();
                var name = node["name"]?.ToString() ?? node.ToString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    result[id] = name;
            }
        }

        return result;
    }
}
