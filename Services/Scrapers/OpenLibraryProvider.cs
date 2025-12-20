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
        // Do not mutate shared HttpClient.DefaultRequestHeaders here (HttpClient is a singleton in DI).
    }

    public Task<bool> ConnectAsync()
    {
        // no authentication required
        return Task.FromResult(true);
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        try
        {
            // API: https://openlibrary.org/dev/docs/api/search
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = $"https://openlibrary.org/search.json?q={encodedQuery}&limit=20";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Retromind/1.0 (OpenSource Media Manager)");

            var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var doc = JsonNode.Parse(json);

            var docs = doc?["docs"]?.AsArray();
            var results = new List<ScraperSearchResult>();

            if (docs == null) return results;

            foreach (var book in docs)
            {
                var key = book?["key"]?.ToString() ?? ""; // "/works/OL12345W"
                var title = book?["title"]?.ToString() ?? "Unknown";
                var author = "";

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
                    Description = ""
                };

                if (int.TryParse(firstPublishYear, out int year))
                {
                    res.ReleaseDate = new DateTime(year, 1, 1);
                }

                var coverId = book?["cover_i"]?.ToString();
                if (!string.IsNullOrEmpty(coverId))
                {
                    res.CoverUrl = $"https://covers.openlibrary.org/b/id/{coverId}-L.jpg";
                }

                results.Add(res);
            }
            
            // We currently do not fetch detailed descriptions here.
            // For quick search results, title + author + cover are usually sufficient.
            // Optionally, you could load the Work details (including description) when the
            // user selects a result, but the Work API is rate-limited and slower.
            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"OpenLibrary Fehler: {ex.Message}", ex);
        }
    }
}