using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web; // Oder System.Net.WebUtility
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class EmuMoviesProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private string? _sessionId;

    public EmuMoviesProvider(ScraperConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
        // Timeout setzen, damit wir nicht ewig warten
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<bool> ConnectAsync()
    {
        if (!string.IsNullOrEmpty(_sessionId)) return true;

        try
        {
            // WICHTIG: HTTPS verwenden!
            var user = HttpUtility.UrlEncode(_config.Username);
            var pass = HttpUtility.UrlEncode(_config.Password);
            
            var url = $"https://api.emumovies.com/api/login?username={user}&password={pass}";
            
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
            
            var resultInfo = doc?["Result"]?.ToString();
            if (resultInfo != "Success")
            {
                // Fehler im Log ausgeben, damit User es sieht (via StatusMessage im VM)
                Console.WriteLine($"EmuMovies Login Failed: {resultInfo}");
                return false;
            }

            _sessionId = doc?["Session"]?.ToString();
            return !string.IsNullOrEmpty(_sessionId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EmuMovies Connection Error: {ex.Message}");
            // Werfe Exception weiter, damit sie im Dialog angezeigt wird!
            throw; 
        }
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        // Hier KEIN Fallback auf ApiSecrets, da User-spezifisch
        if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password))
        {
            throw new System.Exception("Für EmuMovies werden Benutzername und Passwort benötigt. Bitte in den Einstellungen hinterlegen.");
        }

        // Verbindungsversuch
        if (string.IsNullOrEmpty(_sessionId))
        {
            if (!await ConnectAsync()) 
                throw new Exception("Login bei EmuMovies fehlgeschlagen. Zugangsdaten prüfen.");
        }

        try
        {
            var term = HttpUtility.UrlEncode(query);
            // Auch hier HTTPS
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
                
                // Medien laden (optional, verlangsamt Suche)
                // Wir können es hier drin lassen oder erst bei Detail-Klick laden
                // Um die Liste schnell zu füllen, lassen wir es vielleicht erstmal weg 
                // ODER wir holen nur Basis-Medien.
                await EnrichWithMedia(res, id, system);
                
                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            // Fehler weitergeben
            throw new Exception($"EmuMovies Suche fehlgeschlagen: {ex.Message}");
        }
    }

    private async Task EnrichWithMedia(ScraperSearchResult item, string gameId, string system)
    {
        try 
        {
            // HTTPS
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
                
                // Mapping
                if (type == "Box 2D" || type == "Box 3D") 
                {
                    if (string.IsNullOrEmpty(item.CoverUrl) || type == "Box 2D") 
                        item.CoverUrl = mediaUrl;
                }
                else if (type == "Background" || type == "Fanart")
                {
                    item.WallpaperUrl = mediaUrl;
                }
                else if (type == "Logo" || type == "Clear Logo")
                {
                    item.LogoUrl = mediaUrl;
                }
            }
        }
        catch
        {
            // Silent fail
        }
    }
}