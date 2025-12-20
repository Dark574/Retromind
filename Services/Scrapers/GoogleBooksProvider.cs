using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class GoogleBooksProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;

    public GoogleBooksProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public Task<bool> ConnectAsync()
    {
        return Task.FromResult(true);
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        try
        {
            // API: https://www.googleapis.com/books/v1/volumes?q={query}
            // Optional: &key={apiKey}
            
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = $"https://www.googleapis.com/books/v1/volumes?q={encodedQuery}&maxResults=20";
            
            // API key priority: user-specified key > secret key > no key.
            string apiKey = !string.IsNullOrWhiteSpace(_config.ApiKey) ? _config.ApiKey : ApiSecrets.GoogleBooksApiKey;

            if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "INSERT_KEY_HERE")
            {
                url += $"&key={apiKey}";
            }

            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonNode.Parse(json);
            
            var items = doc?["items"]?.AsArray();
            var results = new List<ScraperSearchResult>();

            if (items == null) return results;

            foreach (var item in items)
            {
                var info = item?["volumeInfo"];
                if (info == null) continue;

                var id = item?["id"]?.ToString() ?? "";
                var title = info["title"]?.ToString() ?? "Unknown";
                var subtitle = info["subtitle"]?.ToString();
                if (!string.IsNullOrEmpty(subtitle)) title += $" - {subtitle}";

                var authors = info["authors"]?.AsArray();
                var authorStr = "";
                if (authors != null && authors.Count > 0)
                    authorStr = authors[0]?.ToString() ?? "";

                if (!string.IsNullOrEmpty(authorStr))
                    title += $" ({authorStr})";

                var desc = info["description"]?.ToString() ?? "";
                var publishedDate = info["publishedDate"]?.ToString(); // "YYYY-MM-DD" oder nur "YYYY"

                var res = new ScraperSearchResult
                {
                    Source = "GoogleBooks",
                    Id = id,
                    Title = title,
                    Description = desc
                };

                // Parse date.
                if (!string.IsNullOrEmpty(publishedDate))
                {
                    if (DateTime.TryParse(publishedDate, out var date))
                        res.ReleaseDate = date;
                    else if (int.TryParse(publishedDate, out int year)) // Nur Jahr
                        res.ReleaseDate = new DateTime(year, 1, 1);
                }

                // Google returns "imageLinks" with "thumbnail" and "smallThumbnail".
                var images = info["imageLinks"];
                var thumb = images?["thumbnail"]?.ToString();
                
                // Google thumbnails are often http://; prefer https:// for safety.
                if (!string.IsNullOrEmpty(thumb))
                {
                    res.CoverUrl = thumb.Replace("http://", "https://");
                    
                    // Higher resolution trick:
                    // Many Google thumbnails include a &zoom=1 parameter.
                    // In some cases &zoom=0 or removing the flag yields a bigger image,
                    // but behavior is not guaranteed, so we keep the original for now.
                }

                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"GoogleBooks Fehler: {ex.Message}", ex);
        }
    }
}