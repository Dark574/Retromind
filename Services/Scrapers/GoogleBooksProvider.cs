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
            
            // Key Logik: User-Key > Secret-Key > Kein Key
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

                // Datum parsen
                if (!string.IsNullOrEmpty(publishedDate))
                {
                    if (DateTime.TryParse(publishedDate, out var date))
                        res.ReleaseDate = date;
                    else if (int.TryParse(publishedDate, out int year)) // Nur Jahr
                        res.ReleaseDate = new DateTime(year, 1, 1);
                }

                // Bilder
                // Google liefert "imageLinks" mit "thumbnail" und "smallThumbnail"
                var images = info["imageLinks"];
                var thumb = images?["thumbnail"]?.ToString();
                
                // Google Bilder sind oft http://, wir wollen https://
                if (!string.IsNullOrEmpty(thumb))
                {
                    res.CoverUrl = thumb.Replace("http://", "https://");
                    
                    // Trick für höhere Auflösung: &zoom=1 ist oft klein. &zoom=0 oder weglassen?
                    // Google API liefert oft nur kleine Thumbs in der Suche.
                    // Man kann versuchen, zoom=1 durch zoom=0 zu ersetzen, funktioniert aber nicht immer.
                    // Wir nehmen erstmal, was wir kriegen.
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