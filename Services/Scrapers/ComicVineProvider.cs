using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Web;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

public class ComicVineProvider : IMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;

    public ComicVineProvider(ScraperConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
        // ComicVine blockt Requests ohne eindeutigen User-Agent strikt!
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Retromind-MediaManager/1.0");
    }

    public Task<bool> ConnectAsync()
    {
        return Task.FromResult(!string.IsNullOrEmpty(_config.ApiKey));
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(string query)
    {
        try
        {
            // ComicVine Search API
            // https://comicvine.gamespot.com/api/search/?api_key={key}&format=json&query={query}&resources=volume
            // resources=volume sucht nach Serien (z.B. "Amazing Spider-Man"), resources=issue nach Heften.
            // Da wir Ordner oft als "Serie" und Dateien als "Issues" haben, ist die Suche tricky.
            // Wir suchen hier erstmal nach "Volumes" (Serien/Bände), da dies meist das ist, was man sucht.
            // Wenn du einzelne Heft-Dateien hast, wäre "issue" besser. Wir nehmen "volume,issue"
            
            var encodedQuery = HttpUtility.UrlEncode(query);
            var url = $"https://comicvine.gamespot.com/api/search/?api_key={_config.ApiKey}&format=json&query={encodedQuery}&resources=volume,issue&limit=20";

            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonNode.Parse(json);
            
            var results = new List<ScraperSearchResult>();
            var items = doc?["results"]?.AsArray();

            if (items == null) return results;

            foreach (var item in items)
            {
                var id = item?["id"]?.ToString() ?? "";
                var resType = item?["resource_type"]?.ToString(); // volume oder issue
                
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
                // ComicVine liefert oft HTML in Description. Wir müssten strippen, lassen es aber erstmal roh.

                var res = new ScraperSearchResult
                {
                    Source = "ComicVine",
                    Id = id,
                    Title = title,
                    Description = StripHtml(desc) // Kleiner Helper nötig
                };

                // Bilder
                var image = item?["image"];
                if (image != null)
                {
                    res.CoverUrl = image["medium_url"]?.ToString(); // oder super_url
                    res.WallpaperUrl = image["screen_url"]?.ToString(); // screen_url ist oft gut für Backgrounds
                }

                results.Add(res);
            }

            return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"ComicVine Fehler: {ex.Message}");
        }
    }

    // Simpler HTML Stripper (Regex wäre besser, aber für Textvorschau reicht das)
    private string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", String.Empty);
    }
}