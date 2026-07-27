using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web; 
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Metadata provider for EmuMovies.com API.
/// Handles authentication and fetching of metadata and media assets.
/// </summary>
public class EmuMoviesProvider : IMetadataProvider, IMetadataResultEnricher
{
    private readonly ScraperConfig _config;
    // Use a shared static HttpClient to prevent socket exhaustion.
    // Ideally provided via IHttpClientFactory in a full DI setup.
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, string> _systemByGameId = new(StringComparer.Ordinal);
    private string? _sessionId;

    public EmuMoviesProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Helper to create a properly configured request message.
    /// EmuMovies REQUIRES a User-Agent, otherwise it may reject the connection hard.
    /// </summary>
    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Retromind/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }
    
    /// <summary>
    /// Authenticates with EmuMovies using the credentials from settings.
    /// Stores the Session ID for subsequent requests.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_sessionId)) return true;

        try
        {
            var user = HttpUtility.UrlEncode(_config.Username);
            var pass = HttpUtility.UrlEncode(_config.Password);
        
            // Note: Always use HTTPS for credential transmission
            var url = $"https://api.emumovies.com/api/login?username={user}&password={pass}";
        
            using var request = CreateRequest(url);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
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

    public async Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        // Validate config
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password))
        {
            throw new InvalidOperationException("EmuMovies credentials missing. Please configure username and password.");
        }

        // Ensure connection
        if (string.IsNullOrEmpty(_sessionId))
        {
            if (!await ConnectAsync(cancellationToken)) 
                throw new Exception("EmuMovies Login failed. Please check your credentials.");
        }

        try
        {
            var term = HttpUtility.UrlEncode(query);
            var url = $"https://api.emumovies.com/api/search-games?term={term}&session={_sessionId}";

            // Retry Logic (simple)
            HttpResponseMessage? response = null;
            for (int i = 0; i < 3; i++)
            {
                try 
                {
                    using var request = CreateRequest(url);
                    var candidate = await _httpClient.SendAsync(request, cancellationToken);
                    if (candidate.IsSuccessStatusCode || i == 2)
                    {
                        response = candidate;
                        break;
                    }

                    candidate.Dispose();
                }
                catch (HttpRequestException) 
                {
                    if (i == 2) throw; // throw at the last try
                    await Task.Delay(1000, cancellationToken); // wait for a short time
                }
            }
            
            if (response == null) throw new Exception("EmuMovies Connection failed.");
            using (response)
            {
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonNode.Parse(json);
        
                var games = doc?["Games"]?.AsArray();
                var results = new List<ScraperSearchResult>();

                if (games == null) return results;

                foreach (var game in games)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = ProcessGame(game);
                    if (result != null)
                        results.Add(result);
                }

                return results;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"EmuMovies search failed: {ex.Message}", ex);
        }
    }

    private ScraperSearchResult? ProcessGame(JsonNode? game)
    {
        try
        {
            var id = game?["ID"]?.ToString() ?? "";
            var system = game?["System"]?.ToString() ?? "";
            var title = game?["Name"]?.ToString() ?? "Unknown";

            if (!string.IsNullOrWhiteSpace(id))
                _systemByGameId[id] = system;

            var res = new ScraperSearchResult
            {
                Source = "EmuMovies",
                Id = id,
                Title = $"{title} ({system})",
                Description = game?["Description"]?.ToString() ?? "",
                MaxPlayers = game?["Players"]?.ToString(),
                Platform = system
            };

            return res;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EmuMovies] Error processing game result: {ex.Message}");
            return null;
        }
    }

    public async Task EnrichAsync(
        ScraperSearchResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrWhiteSpace(result.Id))
            return;

        if (string.IsNullOrEmpty(_sessionId) && !await ConnectAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("EmuMovies login failed. Please check your credentials.");

        var system = _systemByGameId.TryGetValue(result.Id, out var mappedSystem)
            ? mappedSystem
            : result.Platform ?? string.Empty;

        await EnrichWithMedia(result, result.Id, system, cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Fetches specific media URLs (Cover, Wallpaper, Logo) for a game result.
    /// </summary>
    private async Task EnrichWithMedia(ScraperSearchResult item, string gameId, string system, CancellationToken cancellationToken)
    {
        try 
        {
            var url = $"https://api.emumovies.com/api/get-game-media?session={_sessionId}&id={gameId}&system={HttpUtility.UrlEncode(system)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return;
        
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var doc = JsonNode.Parse(json);
        
            var medias = doc?["Medias"]?.AsArray();
            if (medias == null) return;
        
            foreach (var media in medias)
            {
                var type = media?["MediaType"]?.ToString();
                var mediaUrl = media?["Url"]?.ToString();
            
                if (string.IsNullOrEmpty(mediaUrl)) continue;
            
                // Map EmuMovies types to our model.
                // NOTE: MediaType strings are based on EmuMovies documentation and may need
                // adjustments if the API changes (e.g. "Marquee", "Cabinet", "Control Panel").
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

                    case "Screenshot":
                    case "Screen Shot":
                    case "ScreenShot":
                    case "Gameplay":
                    case "Ingame":
                    case "In-Game":
                        if (string.IsNullOrEmpty(item.ScreenshotUrl))
                            item.ScreenshotUrl = mediaUrl;
                        break;
                        
                    case "Logo":
                    case "Clear Logo":
                        item.LogoUrl = mediaUrl;
                        break;
                    
                    case "Marquee":
                        item.MarqueeUrl = mediaUrl;
                        break;

                    case "Bezel":
                    case "Bezels":
                    case "Cabinet Bezel":
                        item.BezelUrl = mediaUrl;
                        break;

                    case "Control Panel":
                    case "ControlPanel":
                        item.ControlPanelUrl = mediaUrl;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log error but don't break the whole search for a missing image
            Debug.WriteLine($"[EmuMovies] Failed to enrich media for {gameId}: {ex.Message}");
        }
    }
}
