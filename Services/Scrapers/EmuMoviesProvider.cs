using System;
using System.Collections.Generic;
using System.Diagnostics; // For Debug.WriteLine
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web; 
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Metadata provider for EmuMovies.com API.
/// Handles authentication and fetching of metadata and media assets.
/// </summary>
public class EmuMoviesProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private string? _sessionId;

    public EmuMoviesProvider(ScraperConfig config)
    {
        _config = config;
        // Ideally, HttpClient should be injected via IHttpClientFactory to manage lifecycle/sockets properly.
        // Assuming this Service is used as a Singleton, keeping one instance is acceptable.
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>
    /// Authenticates with EmuMovies using the credentials from settings.
    /// Stores the Session ID for subsequent requests.
    /// </summary>
    public async Task<bool> ConnectAsync()
    {
        if (!string.IsNullOrEmpty(_sessionId)) return true;

        try
        {
            var user = HttpUtility.UrlEncode(_config.Username);
            var pass = HttpUtility.UrlEncode(_config.Password);
        
            // Note: Always use HTTPS for credential transmission
            var url = $"https://api.emumovies.com/api/login?username={user}&password={pass}";
        
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
        
            var resultInfo = doc?["Result"]?.ToString();
            if (resultInfo != "Success")
            {
                Debug.WriteLine($"[EmuMovies] Login Failed: {resultInfo}");
                return false;
            }

            _sessionId = doc?["Session"]?.ToString();
            return !string.IsNullOrEmpty(_sessionId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EmuMovies] Connection Error: {ex.Message}");
            throw; // Rethrow to notify UI/User
        }
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        // Validate config
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password))
        {
            // TODO: Use localized string here (e.g. Strings.ErrorMissingCredentials)
            throw new InvalidOperationException("EmuMovies credentials missing. Please configure username and password.");
        }

        // Ensure connection
        if (string.IsNullOrEmpty(_sessionId))
        {
            if (!await ConnectAsync()) 
                throw new Exception("EmuMovies Login failed. Please check your credentials.");
        }

        try
        {
            var term = HttpUtility.UrlEncode(query);
            var url = $"https://api.emumovies.com/api/search-games?term={term}&session={_sessionId}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
        
            var games = doc?["Games"]?.AsArray();
            var results = new List<ScraperSearchResult>();

            if (games == null) return results;

            foreach (var game in games)
            {
                var id = game?["ID"]?.ToString() ?? "";
                var system = game?["System"]?.ToString() ?? "";
                var title = game?["Name"]?.ToString() ?? "Unknown";

                var res = new ScraperSearchResult
                {
                    Source = "EmuMovies",
                    Id = id,
                    Title = $"{title} ({system})", 
                    Description = game?["Description"]?.ToString() ?? "",
                };
            
                // Optional: Fetch media URLs immediately. 
                // This slows down the search list but provides images instantly.
                await EnrichWithMedia(res, id, system);
            
                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"EmuMovies search failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fetches specific media URLs (Cover, Wallpaper, Logo) for a game result.
    /// </summary>
    private async Task EnrichWithMedia(ScraperSearchResult item, string gameId, string system)
    {
        try 
        {
            var url = $"https://api.emumovies.com/api/get-game-media?session={_sessionId}&id={gameId}&system={HttpUtility.UrlEncode(system)}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return;
        
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
        
            var medias = doc?["Medias"]?.AsArray();
            if (medias == null) return;
        
            foreach (var media in medias)
            {
                var type = media?["MediaType"]?.ToString();
                var mediaUrl = media?["Url"]?.ToString();
            
                if (string.IsNullOrEmpty(mediaUrl)) continue;
            
                // Map EmuMovies types to our model
                switch (type)
                {
                    case "Box 2D":
                    case "Box 3D":
                        // Prefer 2D, fallback to 3D if empty
                        if (string.IsNullOrEmpty(item.CoverUrl) || type == "Box 2D") 
                            item.CoverUrl = mediaUrl;
                        break;
                        
                    case "Background":
                    case "Fanart":
                        item.WallpaperUrl = mediaUrl;
                        break;
                        
                    case "Logo":
                    case "Clear Logo":
                        item.LogoUrl = mediaUrl;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't break the whole search for a missing image
            Debug.WriteLine($"[EmuMovies] Failed to enrich media for {gameId}: {ex.Message}");
        }
    }
}