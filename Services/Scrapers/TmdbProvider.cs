using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
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
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";

    public TmdbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    // Helper to resolve the effective API key (user setting or bundled secret).
    private string GetApiKey()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new Exception("TMDB requires an API key. Please enter it in the scraper settings.");

        return _config.ApiKey;
    }
    
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Will throw if no valid key is configured.
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

        // Safety check: if no key is available at all, we cannot query TMDB.
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new System.Exception("Kein API-Key für TMDB gefunden (weder in Settings noch intern).");
        
        try
        {
            // We search for movies and TV shows.
            // "search/movie" would restrict to movies only, "search/tv" to series.
            // To cover both without prior knowledge, we use "search/multi".
            // "search/multi" returns movies, TV shows and persons – we will ignore persons.
            
            var encodedQuery = HttpUtility.UrlEncode(query);
            
            // Use the configured language, fallback to en-US.
            var lang = string.IsNullOrEmpty(_config.Language) ? "en-US" : _config.Language;

            var url = $"{BaseUrl}/search/multi?api_key={apiKey}&query={encodedQuery}&language={lang}";

            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var doc = JsonNode.Parse(json);

            var items = doc?["results"]?.AsArray();
            var results = new List<ScraperSearchResult>();

            if (items == null) return results;

            foreach (var item in items)
            {
                var mediaType = item?["media_type"]?.ToString(); // "movie" or "tv"
                if (mediaType != "movie" && mediaType != "tv") continue;

                var id = item?["id"]?.ToString() ?? "";
                var title = mediaType == "movie" ? item?["title"]?.ToString() : item?["name"]?.ToString();
                var release = mediaType == "movie" ? item?["release_date"]?.ToString() : item?["first_air_date"]?.ToString();
                var overview = item?["overview"]?.ToString() ?? "";
                // TMDB rating is 0–10, Retromind uses 0–100 – convert to percentage.
                var rating = item?["vote_average"]?.GetValue<double>() * 10;

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
                
                // TMDB does not expose logo images in the basic search result.
                // For logos you'd need a separate "images" request per item,
                // which would significantly increase API traffic and latency.
                // For now we skip logos for performance reasons.

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
            throw new Exception($"TMDB Error: {ex.Message}", ex);
        }
    }
}
