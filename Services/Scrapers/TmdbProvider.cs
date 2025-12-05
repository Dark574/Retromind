using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class TmdbProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original"; // Oder w500 für kleiner

    public TmdbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    // Neue Hilfsmethode, um den richtigen Key zu ermitteln
    private string GetApiKey()
    {
        // 1. Priorität: Der User hat seinen eigenen Key in den Settings eingetragen
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            return _config.ApiKey;
        }

        // 2. Priorität: Wir nutzen den mitgelieferten Key der App
        return ApiSecrets.TmdbApiKey;
    }
    
    public Task<bool> ConnectAsync()
    {
        // TMDB braucht nur den API Key
        return Task.FromResult(!string.IsNullOrEmpty(_config.ApiKey));
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        var apiKey = GetApiKey();

        // Sicherheitscheck: Wenn auch im Secret nichts steht, können wir nichts machen
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new System.Exception("Kein API-Key für TMDB gefunden (weder in Settings noch intern).");
        
        try
        {
            // Wir suchen nach Filmen (Movies). Für Serien müsste man "search/tv" nehmen.
            // Da wir "gemischt" nicht wissen, suchen wir beides oder priorisieren Filme.
            // "search/multi" sucht Filme, Serien und Personen. Das ist oft am besten.
            
            var encodedQuery = HttpUtility.UrlEncode(query);
            
            // Sprache aus Config nutzen (Fallback auf de-DE)
            var lang = string.IsNullOrEmpty(_config.Language) ? "en-US" : _config.Language;
            
            var url = $"{BaseUrl}/search/multi?api_key={apiKey}&query={encodedQuery}&language={lang}";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
            
            var items = doc?["results"]?.AsArray();
            var results = new List<ScraperSearchResult>();

            if (items == null) return results;

            foreach (var item in items)
            {
                var mediaType = item?["media_type"]?.ToString(); // "movie" oder "tv"
                if (mediaType != "movie" && mediaType != "tv") continue; // Personen ignorieren

                var id = item?["id"]?.ToString() ?? "";
                var title = mediaType == "movie" ? item?["title"]?.ToString() : item?["name"]?.ToString();
                var release = mediaType == "movie" ? item?["release_date"]?.ToString() : item?["first_air_date"]?.ToString();
                var overview = item?["overview"]?.ToString() ?? "";
                var rating = item?["vote_average"]?.GetValue<double>() * 10; // TMDB ist 0-10, wir wollen 0-100? Oder wir lassen es. 
                // Retromind Rating scheint double zu sein, Skala unbekannt. Machen wir mal x10 für Prozent.

                var posterPath = item?["poster_path"]?.ToString();
                var backdropPath = item?["backdrop_path"]?.ToString();

                var res = new ScraperSearchResult
                {
                    Source = "TMDB",
                    Id = id,
                    Title = title ?? "Unknown",
                    Description = overview,
                    Rating = rating
                };

                if (DateTime.TryParse(release, out var date))
                    res.ReleaseDate = date;

                if (!string.IsNullOrEmpty(posterPath))
                    res.CoverUrl = ImageBaseUrl + posterPath;

                if (!string.IsNullOrEmpty(backdropPath))
                    res.WallpaperUrl = ImageBaseUrl + backdropPath;
                
                // Logo hat TMDB nicht direkt in der Suche/Standard-Info. Dafür bräuchte man separate "Images" Query.
                // Wir lassen Logo erstmal weg für Speed.

                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"TMDB Fehler: {ex.Message}");
        }
    }
}