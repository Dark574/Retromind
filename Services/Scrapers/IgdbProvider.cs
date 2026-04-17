using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Helpers;

namespace Retromind.Services.Scrapers;

/// <summary>
/// IGDB metadata provider implementing IMetadataProvider.
/// Handles authentication and search for game metadata.
/// </summary>
public class IgdbProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    // Use a shared static HttpClient to prevent socket exhaustion
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    // Serialize token acquisition to avoid concurrent login races.
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private const string TwitchTokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string IgdbApiUrl = "https://api.igdb.com/v4/games";
    private const int TokenExpiryBufferSeconds = 60; // Buffer to avoid edge-case expirations
    private const int MaxRetries = 3;
    private const int MaxSearchResults = 40;

    private static readonly string[] IgdbExtendedFields =
    {
        "name",
        "slug",
        "category",
        "summary",
        "first_release_date",
        "total_rating",
        "cover.url",
        "artworks.url",
        "screenshots.url",
        "genres.name",
        "collection.name",
        "franchises.name",
        "game_modes.name",
        "involved_companies.developer",
        "involved_companies.publisher",
        "involved_companies.company.name",
        "platforms.name"
    };

    public IgdbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Retrieves effective credentials from the scraper configuration only.
    /// Throws if credentials are invalid or missing.
    /// </summary>
    private (string ClientId, string ClientSecret) GetCredentials()
    {
        var clientId = _config.ClientId;
        var clientSecret = _config.ClientSecret;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new Exception("IGDB requires Client ID and Client Secret. Please enter them in the scraper settings.");

        return (clientId, clientSecret);
    }

    /// <summary>
    /// Authenticates with IGDB via Twitch API if not already connected.
    /// Returns true if connected successfully.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: token still valid.
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.Now < _tokenExpiry)
            return true;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate (another caller may have refreshed already).
            if (!string.IsNullOrEmpty(_accessToken) && DateTime.Now < _tokenExpiry)
                return true;

            var creds = GetCredentials();

            using var tokenRequestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", creds.ClientId),
                new KeyValuePair<string, string>("client_secret", creds.ClientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            });
            using var response = await _httpClient.PostAsync(TwitchTokenUrl, tokenRequestContent, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var doc = JsonNode.Parse(json);

            _accessToken = doc?["access_token"]?.ToString() ?? "";
            var seconds = doc?["expires_in"]?.GetValue<int>() ?? 3600;
            _tokenExpiry = DateTime.Now.AddSeconds(seconds - TokenExpiryBufferSeconds);

            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Preserve the original exception as InnerException for easier debugging.
            throw new Exception($"IGDB login failed: {ex.Message}", ex);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Searches IGDB for games matching the query.
    /// Returns up to <see cref="MaxSearchResults"/> results with metadata.
    /// </summary>
    public async Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        // Ensure we are logged in
        var connected = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected)
            throw new Exception("IGDB login failed.");

        var creds = GetCredentials();

        try
        {
            var escapedQuery = EscapeIgdbSearchQuery(query);
            var igdbQuery = BuildIgdbQuery(escapedQuery, IgdbExtendedFields);
            using var response = await SendIgdbQueryAsync(igdbQuery, creds.ClientId, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var items = JsonNode.Parse(json)?.AsArray();

            var results = new List<ScraperSearchResult>();

            if (items == null) return results;

            foreach (var node in items)
            {
                if (node == null) continue;

                var res = new ScraperSearchResult
                {
                    Source = "IGDB",
                    Id = node["id"]?.ToString() ?? "",
                    Title = node["name"]?.ToString() ?? "Unknown",
                    Description = node["summary"]?.ToString() ?? "",
                    Rating = (double?)node["total_rating"],
                    SortTitle = node["name"]?.ToString(),
                    ReleaseType = MapIgdbCategory(node["category"]),
                    Series = FirstNonEmpty(
                        node["collection"]?["name"]?.ToString(),
                        node["franchise"]?["name"]?.ToString(),
                        node["franchises"]?[0]?["name"]?.ToString())
                };

                // Parse release date
                if (node["first_release_date"] is JsonNode dateNode && dateNode.GetValue<long>() is long unixTime)
                {
                    res.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                }

                // Flatten genres
                if (node["genres"] is JsonArray genresArr)
                {
                    res.Genre = string.Join(", ", genresArr.Select(n => n?["name"]?.ToString()).Where(s => !string.IsNullOrEmpty(s)));
                }

                if (node["involved_companies"] is JsonArray companiesArr && companiesArr.Count > 0)
                {
                    var developers = new List<string>();
                    var publishers = new List<string>();

                    foreach (var companyNode in companiesArr)
                    {
                        if (companyNode == null) continue;

                        var companyName = companyNode["company"]?["name"]?.ToString();
                        if (string.IsNullOrWhiteSpace(companyName))
                            continue;

                        if (IsTruthy(companyNode["developer"]))
                            developers.Add(companyName);

                        if (IsTruthy(companyNode["publisher"]))
                            publishers.Add(companyName);
                    }

                    if (developers.Count > 0)
                        res.Developer = string.Join(", ", developers.Distinct(StringComparer.OrdinalIgnoreCase));
                    else
                        res.Developer = companiesArr[0]?["company"]?["name"]?.ToString();

                    if (publishers.Count > 0)
                        res.Publisher = string.Join(", ", publishers.Distinct(StringComparer.OrdinalIgnoreCase));
                }

                // Platforms (comma-separated)
                if (node["platforms"] is JsonArray platformsArr && platformsArr.Count > 0)
                {
                    res.Platform = string.Join(", ",
                        platformsArr
                            .Select(p => p?["name"]?.ToString())
                            .Where(n => !string.IsNullOrWhiteSpace(n)));
                }

                if (node["game_modes"] is JsonArray modesArr && modesArr.Count > 0)
                {
                    var modes = modesArr
                        .Select(m => m?["name"]?.ToString())
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (modes.Count > 0)
                        res.PlayMode = string.Join(", ", modes!);
                }

                var slug = node["slug"]?.ToString();
                if (!string.IsNullOrWhiteSpace(slug))
                    res.CustomFields["IGDB.Slug"] = slug;
                
                // Cover image
                if (node["cover"]?["url"]?.ToString() is string coverUrl && !string.IsNullOrEmpty(coverUrl))
                {
                    res.CoverUrl = FixIgdbImageUrl(coverUrl, "t_cover_big");
                }

                // Wallpaper (prefer artwork, fallback to screenshot)
                string? wallpaperRaw = null;
                if (node["artworks"] is JsonArray artworkArr && artworkArr.Count > 0)
                {
                    wallpaperRaw = artworkArr[0]?["url"]?.ToString();
                }
                else if (node["screenshots"] is JsonArray screenshotArr && screenshotArr.Count > 0)
                {
                    wallpaperRaw = screenshotArr[0]?["url"]?.ToString();
                }

                if (node["screenshots"] is JsonArray screenshotAssets && screenshotAssets.Count > 0)
                {
                    var screenshotRaw = screenshotAssets[0]?["url"]?.ToString();
                    if (!string.IsNullOrEmpty(screenshotRaw))
                        res.ScreenshotUrl = FixIgdbImageUrl(screenshotRaw, "t_1080p");
                }

                if (!string.IsNullOrEmpty(wallpaperRaw))
                {
                    res.WallpaperUrl = FixIgdbImageUrl(wallpaperRaw, "t_1080p");
                }

                results.Add(res);
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"IGDB search error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Helper for fixing IGDB image URLs to high-res with protocol.
    /// </summary>
    private string FixIgdbImageUrl(string url, string size)
    {
        if (url.StartsWith("//")) url = "https:" + url;
        return url.Replace("t_thumb", size);
    }

    /// <summary>
    /// Sends an HTTP request with retry logic for rate limits.
    /// </summary>
    private async Task<HttpResponseMessage?> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> sendAction,
        CancellationToken cancellationToken)
    {
        for (int retry = 0; retry <= MaxRetries; retry++)
        {
            var response = await sendAction(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests &&
                retry < MaxRetries)
            {
                response.Dispose();
                await Task.Delay(1000 * (retry + 1), cancellationToken).ConfigureAwait(false); // Exponential backoff
                continue;
            }

            return response;
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendIgdbQueryAsync(string body, string clientId, CancellationToken cancellationToken)
    {
        var response = await SendWithRetryAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, IgdbApiUrl);
            request.Headers.Add("Client-ID", clientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = new StringContent(body, Encoding.UTF8, "text/plain");
            return await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return response ?? throw new Exception("No response from IGDB.");
    }

    private static string BuildIgdbQuery(string escapedQuery, IEnumerable<string> fields)
    {
        var fieldList = string.Join(", ", fields);
        return $"search \"{escapedQuery}\"; fields {fieldList}; limit {MaxSearchResults};";
    }

    private static string EscapeIgdbSearchQuery(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static bool IsTruthy(JsonNode? node)
    {
        if (node == null) return false;

        if (node is JsonValue value)
        {
            try
            {
                if (value.TryGetValue<bool>(out var boolValue))
                    return boolValue;
            }
            catch
            {
                // ignored
            }

            try
            {
                if (value.TryGetValue<int>(out var intValue))
                    return intValue != 0;
            }
            catch
            {
                // ignored
            }
        }

        return string.Equals(node.ToString(), "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(node.ToString(), "1", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MapIgdbCategory(JsonNode? categoryNode)
    {
        if (categoryNode == null || !int.TryParse(categoryNode.ToString(), out var category))
            return null;

        return category switch
        {
            0 => "Main Game",
            1 => "DLC / Addon",
            2 => "Expansion",
            3 => "Bundle",
            4 => "Standalone Expansion",
            5 => "Mod",
            6 => "Episode",
            7 => "Season",
            8 => "Remake",
            9 => "Remaster",
            10 => "Expanded Game",
            11 => "Port",
            12 => "Fork",
            13 => "Pack",
            14 => "Update",
            _ => null
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
