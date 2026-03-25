using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class GoogleBooksProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private const int MaxSearchResults = 40;
    private const int GooglePageSize = 40;

    public GoogleBooksProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            // API: https://www.googleapis.com/books/v1/volumes?q={query}
            // Optional: &key={apiKey}
            
            var encodedQuery = HttpUtility.UrlEncode(query);
            var apiKey = _config.ApiKey ?? string.Empty;
            var language = NormalizeLanguage(_config.Language);

            var results = new List<ScraperSearchResult>(MaxSearchResults);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            for (var startIndex = 0; results.Count < MaxSearchResults; startIndex += GooglePageSize)
            {
                var pageSize = Math.Min(GooglePageSize, MaxSearchResults - results.Count);
                var url = $"https://www.googleapis.com/books/v1/volumes?q={encodedQuery}&maxResults={pageSize}&startIndex={startIndex}";

                if (!string.IsNullOrWhiteSpace(language))
                    url += $"&langRestrict={HttpUtility.UrlEncode(language)}";

                // API key (optional): user-specified key only.
                if (!string.IsNullOrWhiteSpace(apiKey))
                    url += $"&key={apiKey}";

                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var doc = JsonNode.Parse(json);

                var totalItems = doc?["totalItems"]?.GetValue<int>() ?? 0;
                var items = doc?["items"]?.AsArray();
                if (items == null || items.Count == 0)
                    break;

                foreach (var item in items)
                {
                    var info = item?["volumeInfo"];
                    if (info == null) continue;

                    var id = item?["id"]?.ToString() ?? "";
                    if (!seenIds.Add(id))
                        continue;

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
                    var publishedDate = info["publishedDate"]?.ToString(); // "YYYY-MM-DD" or simple "YYYY"

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
                        else if (int.TryParse(publishedDate, out int year)) // only year
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
                    if (results.Count >= MaxSearchResults)
                        break;
                }

                if (totalItems > 0 && startIndex + pageSize >= totalItems)
                    break;
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"GoogleBooks Error: {ex.Message}", ex);
        }
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";

        var dashIndex = language.IndexOf('-');
        if (dashIndex > 0)
            language = language[..dashIndex];

        return language.Trim().ToLowerInvariant();
    }
}
