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

public class IgdbProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    // Use a shared static HttpClient to prevent socket exhaustion
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public IgdbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    // Helper to get effective credentials
    private (string ClientId, string ClientSecret) GetCredentials()
    {
        var clientId = !string.IsNullOrWhiteSpace(_config.ClientId) ? _config.ClientId : ApiSecrets.IgdbClientId;
        var clientSecret = !string.IsNullOrWhiteSpace(_config.ClientSecret) ? _config.ClientSecret : ApiSecrets.IgdbClientSecret;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || clientId == "INSERT_KEY_HERE")
        {
            throw new Exception("IGDB benötigt Client ID und Client Secret. Bitte in Settings eintragen.");
        }

        return (clientId, clientSecret);
    }
    
    public async Task<bool> ConnectAsync()
    {
        // If we still have a valid token, we are good
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.Now < _tokenExpiry)
            return true;

        try
        {
            var creds = GetCredentials(); // Holt User-Daten ODER Secrets
            
            // 1. Get Token from Twitch
            // POST https://id.twitch.tv/oauth2/token
            var url = $"https://id.twitch.tv/oauth2/token?client_id={creds.ClientId}&client_secret={creds.ClientSecret}&grant_type=client_credentials";
            var response = await _httpClient.PostAsync(url, null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);

            _accessToken = doc?["access_token"]?.ToString() ?? "";
            var seconds = doc?["expires_in"]?.GetValue<int>() ?? 3600;
            _tokenExpiry = DateTime.Now.AddSeconds(seconds - 60); // 1 minute buffer

            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"IGDB Login fehlgeschlagen: {ex.Message}");
        }
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        // Ensure we are logged in
        await ConnectAsync();
        var creds = GetCredentials();

        try
        {
            // Query extended by: involved_companies (Developer) and genres
            var igdbQuery =
                $"search \"{query}\"; fields name, summary, first_release_date, total_rating, cover.url, artworks.url, screenshots.url, genres.name, involved_companies.company.name; limit 20;";

            // --- RETRY LOGIC START ---
            int maxRetries = 3;
            int currentRetry = 0;
            HttpResponseMessage? response = null;
            
            while (currentRetry <= maxRetries)
            {
                // Important: HttpRequestMessage cannot be used again, 
                // it needs to be redone in the loop
                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
                request.Headers.Add("Client-ID", creds.ClientId);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Content = new StringContent(igdbQuery, Encoding.UTF8, "text/plain");

                response = await _httpClient.SendAsync(request);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Backoff: on the first try wait 1 second, on the second 2 seconds...
                    currentRetry++;
                    if (currentRetry > maxRetries) break; // give up

                    await Task.Delay(1000 * currentRetry);
                    continue; // next try
                }

                // if there are other errors or success: get out of the loop
                break;
            }
            // --- RETRY LOGIC END ---
            
            // if the response is null or if it still fails -> Exception werfen
            if (response == null) throw new Exception("No response from IGDB.");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var items = JsonNode.Parse(json)?.AsArray(); // JsonNode is often more flexibel than JsonSerializer

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

                var dateNode = node["first_release_date"];
                
                // Date
                if (dateNode != null)
                {
                    long unixTime = dateNode.GetValue<long>();
                    res.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                }

                // Genre (Flatten array: "Action, Adventure")
                var genresArr = node?["genres"]?.AsArray();
                if (genresArr != null)
                {
                    var genreList = genresArr.Select(n => n?["name"]?.ToString()).Where(s => s != null);
                    res.Genre = string.Join(", ", genreList);
                }

                // Developer (Involved Companies -> Company -> Name)
                // We just take the first entry, IGDB usually sorts well
                if (node?["involved_companies"] is JsonArray companiesArr && companiesArr.Count > 0)
                {
                    var firstComp = companiesArr[0];
                    res.Developer = firstComp?["company"]?["name"]?.ToString();
                }

                // Images
                var coverUrl = node?["cover"]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(coverUrl))
                    res.CoverUrl = FixIgdbImageUrl(coverUrl, "t_cover_big");

                // Wallpaper
                var artworkArr = node?["artworks"]?.AsArray();
                var screenshotArr = node?["screenshots"]?.AsArray();

                string? wallpaperRaw = null;
                if (artworkArr != null && artworkArr.Count > 0)
                    wallpaperRaw = artworkArr[0]?["url"]?.ToString();
                else if (screenshotArr != null && screenshotArr.Count > 0)
                    wallpaperRaw = screenshotArr[0]?["url"]?.ToString();

                if (!string.IsNullOrEmpty(wallpaperRaw))
                    res.WallpaperUrl = FixIgdbImageUrl(wallpaperRaw, "t_1080p");

                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            // Rethrow exception so ViewModel can display it
            throw new Exception($"IGDB Fehler: {ex.Message}");
        }
    }

    // Helper: IGDB URLs are often "//images..." and "t_thumb". We want "https://" and HighRes.
    private string FixIgdbImageUrl(string url, string size)
    {
        if (url.StartsWith("//")) url = "https:" + url;
        return url.Replace("t_thumb", size);
    }
}