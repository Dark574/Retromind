using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class OpenLibraryProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;

    public OpenLibraryProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        // Netterweise User-Agent setzen (Good Practice)
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Retromind/1.0 (OpenSource Media Manager)");
    }

    public Task<bool> ConnectAsync()
    {
        // Keine Authentifizierung nötig
        return Task.FromResult(true);
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        try
        {
            // API: https://openlibrary.org/dev/docs/api/search
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = $"https://openlibrary.org/search.json?q={encodedQuery}&limit=20";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
            
            var docs = doc?["docs"]?.AsArray();
            var results = new List<ScraperSearchResult>();

            if (docs == null) return results;

            foreach (var book in docs)
            {
                var key = book?["key"]?.ToString() ?? ""; // "/works/OL12345W"
                var title = book?["title"]?.ToString() ?? "Unknown";
                var author = "";
                
                // Autoren sind ein Array
                var authors = book?["author_name"]?.AsArray();
                if (authors != null && authors.Count > 0)
                {
                    author = authors[0]?.ToString() ?? "";
                }

                var firstPublishYear = book?["first_publish_year"]?.ToString();
                
                var res = new ScraperSearchResult
                {
                    Source = "OpenLibrary",
                    Id = key,
                    Title = string.IsNullOrEmpty(author) ? title : $"{title} ({author})",
                    Description = "" // OpenLibrary Suche liefert KEINE Beschreibung, die muss man separat holen
                };

                if (int.TryParse(firstPublishYear, out int year))
                {
                    res.ReleaseDate = new DateTime(year, 1, 1);
                }

                // Cover ID suchen
                var coverId = book?["cover_i"]?.ToString();
                if (!string.IsNullOrEmpty(coverId))
                {
                    res.CoverUrl = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg";
                }

                // Da wir keine Description haben, könnten wir sie nachladen (FetchWork),
                // aber für die Suche reicht Titel + Autor + Cover oft aus.
                // Optional: Wir laden die Description, wenn der User draufklickt?
                // Wir machen es hier einfachheitshalber NICHT, um die Suche schnell zu halten.
                // (Die Work API hat Raten-Limits).

                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"OpenLibrary Fehler: {ex.Message}");
        }
    }
}