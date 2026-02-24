using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class ComicVineProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;

    public ComicVineProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
        // Do not mutate shared HttpClient.DefaultRequestHeaders here (HttpClient is a singleton in DI).
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(_config.ApiKey));
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var apiKey = _config.ApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("ComicVine requires an API key. Please enter it in the scraper settings.");

            // ComicVine Search API
            // https://comicvine.gamespot.com/api/search/?api_key={key}&format=json&query={query}&resources=volume
            // resources=volume searches for series (e.g. "Amazing Spider-Man"), resources=issue for individual issues.
            // Since folders often represent "series" and files represent "issues", this mapping is tricky.
            // Here we first search for "volumes" (series/collections), as this is usually what users expect.
            // If you primarily have single issue files, "issue" would be more appropriate. We use "volume,issue".
            
            var builder = new UriBuilder("https://comicvine.gamespot.com/api/search/");
            var qs = HttpUtility.ParseQueryString(string.Empty);
            qs["api_key"] = apiKey;
            qs["format"] = "json";
            qs["query"] = query;
            qs["resources"] = "volume,issue";
            qs["limit"] = "20";
            builder.Query = qs.ToString();
            var url = builder.Uri;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");

            // ComicVine blocks requests without a clear User-Agent.
            if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
                request.Headers.UserAgent.ParseAdd("Retromind/1.0 (Linux Portable Media Manager)");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = ExtractErrorDetail(json);
                throw new Exception($"ComicVine API error {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
            }

            var doc = JsonNode.Parse(json);

            var results = new List<ScraperSearchResult>();
            var items = doc?["results"]?.AsArray();

            if (items == null) return results;

            foreach (var item in items)
            {
                var id = item?["id"]?.ToString() ?? "";
                var resType = item?["resource_type"]?.ToString(); // volume or issue

                var name = item?["name"]?.ToString();
                var volumeName = item?["volume"]?["name"]?.ToString();
                var issueNumber = item?["issue_number"]?.ToString();

                var title = "Unknown";
                if (resType == "issue" && !string.IsNullOrEmpty(volumeName))
                {
                    title = $"{volumeName} #{issueNumber}";
                    if (!string.IsNullOrEmpty(name)) title += $" - {name}";
                }
                else
                {
                    title = name ?? "Unknown";
                    var startYear = item?["start_year"]?.ToString();
                    if (!string.IsNullOrEmpty(startYear)) title += $" ({startYear})";
                }

                var desc = item?["description"]?.ToString() ?? item?["deck"]?.ToString() ?? "";

                var res = new ScraperSearchResult
                {
                    Source = "ComicVine",
                    Id = id,
                    Title = title,
                    Description = StripHtml(desc)
                };

                var image = item?["image"];
                if (image != null)
                {
                    res.CoverUrl = image["medium_url"]?.ToString();
                    res.WallpaperUrl = image["screen_url"]?.ToString();
                }

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
            throw new Exception($"ComicVine Fehler: {ex.Message}", ex);
        }
    }

    private static string ExtractErrorDetail(string? jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
            return "No response body.";

        try
        {
            var doc = JsonNode.Parse(jsonOrText);
            var error = doc?["error"]?.ToString();
            var status = doc?["status_code"]?.ToString();
            if (!string.IsNullOrWhiteSpace(error) || !string.IsNullOrWhiteSpace(status))
                return $"error={error ?? "unknown"}, status_code={status ?? "unknown"}";
        }
        catch
        {
            // Not JSON; fall back to trimmed text.
        }

        var trimmed = jsonOrText.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200) + "...";
    }

    // Simple HTML stripper. A Regex or dedicated HTML parser would be more robust,
    // but this is sufficient for short text previews.
    private string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", String.Empty);
    }
}
