using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
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

    private const string TwitchTokenUrl = "https://id.twitch.tv/oauth2/token";
    private const string IgdbApiUrl = "https://api.igdb.com/v4/games";
    private const int TokenExpiryBufferSeconds = 60; // Buffer to avoid edge-case expirations
    private const int MaxRetries = 3;
    
    public IgdbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Retrieves effective credentials, preferring config over secrets.
    /// Throws if credentials are invalid or missing.
    /// </summary>
    private (string ClientId, string ClientSecret) GetCredentials()
    {
        var clientId = !string.IsNullOrWhiteSpace(_config.ClientId) ? _config.ClientId : ApiSecrets.IgdbClientId;
        var clientSecret = !string.IsNullOrWhiteSpace(_config.ClientSecret) ? _config.ClientSecret : ApiSecrets.IgdbClientSecret;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || clientId == "INSERT_KEY_HERE")
        {
            throw new Exception("IGDB requires Client ID and Client Secret. Please enter in Settings.");
        }

        return (clientId, clientSecret);
    }
    
    /// <summary>
    /// Authenticates with IGDB via Twitch API if not already connected.
    /// Returns true if connected successfully.
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        // If we still have a valid token, we are good
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.Now < _tokenExpiry)
            return true;

        try
        {
            var creds = GetCredentials();
            
            // Build token request URL
            var url = $"{TwitchTokenUrl}?client_id={creds.ClientId}&client_secret={creds.ClientSecret}&grant_type=client_credentials";
            var response = await _httpClient.PostAsync(url, null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);

            _accessToken = doc?["access_token"]?.ToString() ?? "";
            var seconds = doc?["expires_in"]?.GetValue<int>() ?? 3600;
            _tokenExpiry = DateTime.Now.AddSeconds(seconds - TokenExpiryBufferSeconds);

            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"IGDB login failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Searches IGDB for games matching the query.
    /// Returns up to 20 results with metadata.
    /// </summary>
    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        // Ensure we are logged in
        await ConnectAsync();
        var creds = GetCredentials();

        try
        {
            // Query string with fields for metadata
            var igdbQuery =
                $"search \"{query}\"; fields name, summary, first_release_date, total_rating, cover.url, artworks.url, screenshots.url, genres.name, involved_companies.company.name; limit 20;";

            // Send request with retry logic
            var response = await SendWithRetryAsync(async () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, IgdbApiUrl);
                request.Headers.Add("Client-ID", creds.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Content = new StringContent(igdbQuery, Encoding.UTF8, "text/plain");
                return await _httpClient.SendAsync(request);
            });

            if (response == null) throw new Exception("No response from IGDB.");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
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
                    Rating = (double?)node["total_rating"]
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

                // Get first developer
                if (node["involved_companies"] is JsonArray companiesArr && companiesArr.Count > 0)
                {
                    res.Developer = companiesArr[0]?["company"]?["name"]?.ToString();
                }

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

                if (!string.IsNullOrEmpty(wallpaperRaw))
                {
                    res.WallpaperUrl = FixIgdbImageUrl(wallpaperRaw, "t_1080p");
                }

                results.Add(res);
            }
            
            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"IGDB search error: {ex.Message}");
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
    private async Task<HttpResponseMessage?> SendWithRetryAsync(Func<Task<HttpResponseMessage>> sendAction)
    {
        HttpResponseMessage? response = null;
        for (int retry = 0; retry <= MaxRetries; retry++)
        {
            response = await sendAction();

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(1000 * (retry + 1)); // Exponential backoff
                continue;
            }

            break;
        }
        return response;
    }
}