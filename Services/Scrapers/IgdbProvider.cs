using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Retromind.Models;
using Retromind.Helpers;

namespace Retromind.Services.Scrapers;

public class IgdbProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry;

    public IgdbProvider(ScraperConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
    }

    // Helper um die effektiven Credentials zu holen
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
        // Wenn wir noch ein gültiges Token haben, alles gut
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.Now < _tokenExpiry)
            return true;

        try
        {
            var creds = GetCredentials(); // Holt User-Daten ODER Secrets
            
            // 1. Token von Twitch holen
            // POST https://id.twitch.tv/oauth2/token
            var url = $"https://id.twitch.tv/oauth2/token?client_id={creds.ClientId}&client_secret={creds.ClientSecret}&grant_type=client_credentials";
            var response = await _httpClient.PostAsync(url, null);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);

            _accessToken = doc?["access_token"]?.ToString() ?? "";
            var seconds = doc?["expires_in"]?.GetValue<int>() ?? 3600;
            _tokenExpiry = DateTime.Now.AddSeconds(seconds - 60); // 1 Minute Puffer

            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"IGDB Login fehlgeschlagen: {ex.Message}");
        }
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        // Sicherstellen, dass wir eingeloggt sind
        await ConnectAsync();
        var creds = GetCredentials();

        try
        {
            // Query erweitert um: involved_companies (Developer) und genres
            var igdbQuery =
                $"search \"{query}\"; fields name, summary, first_release_date, total_rating, cover.url, artworks.url, screenshots.url, genres.name, involved_companies.company.name; limit 20;";

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games");
            // WICHTIG: Client-ID aus den Credentials nehmen, nicht aus _config (wegen Fallback)
            request.Headers.Add("Client-ID", creds.ClientId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            request.Content = new StringContent(igdbQuery, Encoding.UTF8, "text/plain");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var items = JsonNode.Parse(json)?.AsArray(); // JsonNode ist oft flexibler als JsonSerializer

            var results = new List<ScraperSearchResult>();

            if (items == null) return results;

            foreach (var item in items)
            {
                var res = new ScraperSearchResult
                {
                    Source = "IGDB",
                    Id = item?["id"]?.ToString() ?? "",
                    Title = item?["name"]?.ToString() ?? "Unknown",
                    Description = item?["summary"]?.ToString() ?? "",
                    Rating = item?["total_rating"]?.GetValue<double>()
                };

                // Datum
                if (item?["first_release_date"] != null)
                {
                    var unixTime = item["first_release_date"].GetValue<long>();
                    res.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
                }

                // Genre (Array flachklopfen: "Action, Adventure")
                var genresArr = item?["genres"]?.AsArray();
                if (genresArr != null)
                {
                    var genreList = genresArr.Select(node => node?["name"]?.ToString()).Where(s => s != null);
                    res.Genre = string.Join(", ", genreList);
                }

                // Developer (Involved Companies -> Company -> Name)
                // Wir nehmen einfach den ersten Eintrag, IGDB sortiert meistens gut
                var companiesArr = item?["involved_companies"]?.AsArray();
                if (companiesArr != null && companiesArr.Count > 0)
                {
                    res.Developer = companiesArr[0]?["company"]?["name"]?.ToString();
                }

                // Bilder
                var coverUrl = item?["cover"]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(coverUrl))
                    res.CoverUrl = FixIgdbImageUrl(coverUrl, "t_cover_big");

                // Wallpaper
                var artworkArr = item?["artworks"]?.AsArray();
                var screenshotArr = item?["screenshots"]?.AsArray();

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
            // Exception weiterwerfen, damit ViewModel sie anzeigen kann
            throw new Exception($"IGDB Fehler: {ex.Message}");
        }
    }

    // Helper: IGDB URLs sind oft "//images..." und "t_thumb". Wir wollen "https://" und HighRes.
    private string FixIgdbImageUrl(string url, string size)
    {
        if (url.StartsWith("//")) url = "https:" + url;
        return url.Replace("t_thumb", size);
    }
}