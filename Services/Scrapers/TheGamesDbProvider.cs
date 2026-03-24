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
    private const int MaxImageEnrichmentResults = 3;

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
                    "&include=boxart,platform,genre" +
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
                var genreData = root?["include"]?["genre"]?["data"]
                               ?? root?["include"]?["genres"]?["data"];
                var developerData = root?["include"]?["developer"]?["data"]
                                   ?? root?["include"]?["developers"]?["data"];
                var publisherData = root?["include"]?["publisher"]?["data"]
                                   ?? root?["include"]?["publishers"]?["data"];
                var boxartData = root?["include"]?["boxart"]?["data"] as JsonObject;
                var boxartBaseUrl = ResolveBoxartBaseUrl(root);
                var platformNameById = await BuildPlatformNameMapAsync(apiKey, games, platformData, cancellationToken)
                    .ConfigureAwait(false);
                var genreNameById = await BuildCompanyNameMapAsync(
                        apiKey,
                        games,
                        genreData,
                        "Genres/ByGenreID",
                        static g => ExtractCompanyIds(g?["genres"]),
                        cancellationToken,
                        "genres",
                        "genre")
                    .ConfigureAwait(false);
                var developerNameById = await BuildCompanyNameMapAsync(
                        apiKey,
                        games,
                        developerData,
                        "Developers/ByDeveloperID",
                        static g => ExtractCompanyIds(g?["developers"]),
                        cancellationToken,
                        "developers",
                        "developer",
                        "publishers",
                        "publisher")
                    .ConfigureAwait(false);
                var publisherNameById = await BuildCompanyNameMapAsync(
                        apiKey,
                        games,
                        publisherData,
                        "Publishers/ByPublisherID",
                        static g => ExtractCompanyIds(g?["publishers"]),
                        cancellationToken,
                        "publishers",
                        "publisher",
                        "developers",
                        "developer")
                    .ConfigureAwait(false);

                var countBeforePage = results.Count;
                var imageEnrichmentCount = 0;

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
                        Developer = ResolveCompanyName(game, developerNameById, publisherNameById),
                        Genre = ResolveGenres(game, genreNameById)
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

                    var missingVisuals = string.IsNullOrWhiteSpace(result.LogoUrl)
                                         || string.IsNullOrWhiteSpace(result.WallpaperUrl)
                                         || string.IsNullOrWhiteSpace(result.MarqueeUrl);

                    if (!string.IsNullOrWhiteSpace(id) && missingVisuals && imageEnrichmentCount < MaxImageEnrichmentResults)
                    {
                        await TryEnrichWithGameImagesAsync(apiKey, id, result, cancellationToken).ConfigureAwait(false);
                        imageEnrichmentCount++;
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

    private async Task TryEnrichWithGameImagesAsync(
        string apiKey,
        string gameId,
        ScraperSearchResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BaseUrl}/Games/Images?apikey={Uri.EscapeDataString(apiKey)}&games_id={Uri.EscapeDataString(gameId)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            var data = root?["data"];
            if (data == null)
                return;

            var baseUrl = data["base_url"]?["original"]?.ToString()
                          ?? data["base_url"]?["large"]?.ToString()
                          ?? data["base_url"]?["medium"]?.ToString()
                          ?? string.Empty;

            var candidates = new List<string>();
            CollectImageCandidates(data["images"] ?? data, candidates);
            if (candidates.Count == 0)
                return;

            var resolved = candidates
                .Select(c => ToAbsoluteImageUrl(baseUrl, c))
                .Where(IsLikelyImagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(result.LogoUrl))
            {
                result.LogoUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "clearlogo", "/logo/", "_logo", "logo/"));
            }

            if (string.IsNullOrWhiteSpace(result.CoverUrl))
            {
                result.CoverUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "boxart/front", "boxart") && !ContainsAny(p, "back"));
            }

            if (string.IsNullOrWhiteSpace(result.WallpaperUrl))
            {
                result.WallpaperUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "fanart", "screenshot", "background", "screenshots"));
            }

            if (string.IsNullOrWhiteSpace(result.MarqueeUrl))
            {
                result.MarqueeUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "marquee", "wheel", "banner"));
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private async Task<Dictionary<string, string>> BuildCompanyNameMapAsync(
        string apiKey,
        JsonArray games,
        JsonNode? includeData,
        string endpointPath,
        Func<JsonNode?, IEnumerable<string>> idsSelector,
        CancellationToken cancellationToken,
        params string[] collectionKeys)
    {
        var map = ExtractIdNameMap(includeData);

        var neededIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var game in games)
        {
            foreach (var id in idsSelector(game).Where(IsLikelyIdentifier))
            {
                if (!map.ContainsKey(id))
                    neededIds.Add(id);
            }
        }

        if (neededIds.Count == 0)
            return map;

        try
        {
            var idsCsv = string.Join(",", neededIds);
            var url = $"{BaseUrl}/{endpointPath}?apikey={Uri.EscapeDataString(apiKey)}&id={Uri.EscapeDataString(idsCsv)}";

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return map;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            var dataNode = root?["data"];
            if (dataNode is JsonObject dataObj)
            {
                // Try requested collection keys first, then fall back to common names.
                if (collectionKeys != null && collectionKeys.Length > 0)
                {
                    foreach (var key in collectionKeys)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        if (dataObj[key] != null)
                        {
                            dataNode = dataObj[key];
                            break;
                        }
                    }
                }

                if (ReferenceEquals(dataNode, root?["data"]))
                {
                    dataNode = dataObj["developers"]
                               ?? dataObj["publishers"]
                               ?? dataObj["developer"]
                               ?? dataObj["publisher"]
                               ?? dataObj["genres"]
                               ?? dataObj["genre"]
                               ?? dataObj;
                }
            }

            var resolved = ExtractIdNameMap(dataNode);
            foreach (var kv in resolved)
                map[kv.Key] = kv.Value;
        }
        catch
        {
            // Best effort: keep names already available via include payload.
        }

        return map;
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

    private static string? ResolveCompanyName(
        JsonNode game,
        IReadOnlyDictionary<string, string> developerNameById,
        IReadOnlyDictionary<string, string> publisherNameById)
    {
        var explicitDevName = FirstMeaningfulText(ExtractCompanyNames(game["developers"]));
        if (!string.IsNullOrWhiteSpace(explicitDevName))
            return explicitDevName;

        foreach (var id in ExtractCompanyIds(game["developers"]))
        {
            if (developerNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        var explicitPubName = FirstMeaningfulText(ExtractCompanyNames(game["publishers"]));
        if (!string.IsNullOrWhiteSpace(explicitPubName))
            return explicitPubName;

        foreach (var id in ExtractCompanyIds(game["publishers"]))
        {
            if (publisherNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static string? ResolveGenres(
        JsonNode game,
        IReadOnlyDictionary<string, string> genreNameById)
    {
        var explicitNames = ExtractCompanyNames(game["genres"])
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v) && !IsLikelyIdentifier(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (explicitNames.Count > 0)
            return string.Join(", ", explicitNames);

        var resolved = new List<string>();
        foreach (var id in ExtractCompanyIds(game["genres"]))
        {
            if (genreNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                resolved.Add(name);
        }

        resolved = resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return resolved.Count > 0
            ? string.Join(", ", resolved)
            : null;
    }

    private static string? FirstMeaningfulText(IEnumerable<string> values)
    {
        return values
            .Select(v => v?.Trim())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !IsLikelyIdentifier(v));
    }

    private static IEnumerable<string> ExtractCompanyNames(JsonNode? node)
    {
        if (node == null)
            yield break;

        if (node is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry == null)
                    continue;

                if (entry is JsonObject obj)
                {
                    var name = obj["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        yield return name;
                }
                else
                {
                    var raw = entry.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                        yield return raw;
                }
            }

            yield break;
        }

        if (node is JsonObject singleObj)
        {
            var name = singleObj["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
            yield break;
        }

        var single = node.ToString();
        if (!string.IsNullOrWhiteSpace(single))
            yield return single;
    }

    private static IEnumerable<string> ExtractCompanyIds(JsonNode? node)
    {
        if (node == null)
            yield break;

        static IEnumerable<string> SplitValues(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                    yield return part;
            }
        }

        if (node is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry == null)
                    continue;

                if (entry is JsonObject obj)
                {
                    foreach (var id in SplitValues(obj["id"]?.ToString()))
                        yield return id;
                }
                else
                {
                    foreach (var id in SplitValues(entry.ToString()))
                        yield return id;
                }
            }

            yield break;
        }

        if (node is JsonObject singleObj)
        {
            foreach (var id in SplitValues(singleObj["id"]?.ToString()))
                yield return id;
            yield break;
        }

        foreach (var id in SplitValues(node.ToString()))
            yield return id;
    }

    private static Dictionary<string, string> ExtractIdNameMap(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node == null)
            return result;

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value == null)
                    continue;

                if (kv.Value is JsonObject childObj)
                {
                    var id = childObj["id"]?.ToString();
                    var name = ExtractDisplayName(childObj);

                    if (string.IsNullOrWhiteSpace(id))
                        id = kv.Key;

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        result[id] = name;
                }
                else
                {
                    // Sometimes payloads are keyed by id with plain string values.
                    var valueText = kv.Value.ToString();
                    if (IsLikelyIdentifier(kv.Key) && !string.IsNullOrWhiteSpace(valueText))
                        result[kv.Key] = valueText;
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var id = item?["id"]?.ToString();
                var name = item is JsonObject itemObj ? ExtractDisplayName(itemObj) : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    result[id] = name;
            }
        }

        return result;
    }

    private static string? ExtractDisplayName(JsonObject obj)
    {
        return obj["name"]?.ToString()
               ?? obj["genre"]?.ToString()
               ?? obj["title"]?.ToString()
               ?? obj["developer"]?.ToString()
               ?? obj["publisher"]?.ToString()
               ?? obj["platform"]?.ToString()
               ?? obj["value"]?.ToString();
    }

    private static void CollectImageCandidates(JsonNode? node, List<string> sink)
    {
        if (node == null)
            return;

        if (node is JsonValue val)
        {
            var text = val.ToString();
            if (IsLikelyImagePath(text))
                sink.Add(text);
            return;
        }

        if (node is JsonArray arr)
        {
            foreach (var child in arr)
                CollectImageCandidates(child, sink);
            return;
        }

        if (node is JsonObject obj)
        {
            var filename = obj["filename"]?.ToString();
            if (IsLikelyImagePath(filename))
                sink.Add(filename!);

            var url = obj["url"]?.ToString();
            if (IsLikelyImagePath(url))
                sink.Add(url!);

            foreach (var kv in obj)
                CollectImageCandidates(kv.Value, sink);
        }
    }

    private static bool IsLikelyImagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var v = value.Trim();
        return v.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
               || v.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
               || v.Contains(".png", StringComparison.OrdinalIgnoreCase)
               || v.Contains(".webp", StringComparison.OrdinalIgnoreCase)
               || v.Contains("boxart", StringComparison.OrdinalIgnoreCase)
               || v.Contains("fanart", StringComparison.OrdinalIgnoreCase)
               || v.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
               || v.Contains("clearlogo", StringComparison.OrdinalIgnoreCase)
               || v.Contains("marquee", StringComparison.OrdinalIgnoreCase)
               || v.Contains("banner", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToAbsoluteImageUrl(string baseUrl, string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return pathOrUrl;

        return $"{baseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLikelyIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return long.TryParse(value, out _);
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
