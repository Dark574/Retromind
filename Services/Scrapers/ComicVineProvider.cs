using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    private const int MaxSearchResults = 40;

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

            try
            {
                return await SearchResourceTypesAsync(
                    apiKey,
                    query,
                    "volume,issue",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ComicVineApiException ex) when (IsServerError(ex.StatusCode))
            {
                // ComicVine occasionally fails a query for one resource type while
                // the other still works. Retry them separately so a broken issue
                // search does not discard valid volume results (and vice versa).
                var partialResults = new List<IReadOnlyList<ScraperSearchResult>>();
                var completedRequest = false;
                List<ScraperSearchResult>? volumeResults = null;
                var issueSearchFailed = false;

                foreach (var resourceType in new[] { "volume", "issue" })
                {
                    try
                    {
                        var results = await SearchResourceTypesAsync(
                            apiKey,
                            query,
                            resourceType,
                            cancellationToken).ConfigureAwait(false);
                        partialResults.Add(results);
                        if (resourceType == "volume")
                            volumeResults = results;
                        completedRequest = true;
                    }
                    catch (ComicVineApiException partialEx) when (IsServerError(partialEx.StatusCode))
                    {
                        // Keep results from the resource endpoint that still works.
                        if (resourceType == "issue")
                            issueSearchFailed = true;
                    }
                }

                if (issueSearchFailed && volumeResults is { Count: > 0 })
                {
                    try
                    {
                        var recoveredIssues = await SearchIssuesForVolumesAsync(
                            apiKey,
                            volumeResults,
                            cancellationToken).ConfigureAwait(false);
                        ApplyIssueArtworkToVolumes(volumeResults, recoveredIssues);
                        partialResults.Add(recoveredIssues);
                    }
                    catch (ComicVineApiException partialEx) when (IsServerError(partialEx.StatusCode))
                    {
                        // The volume results are still useful even if issue recovery fails.
                    }
                }

                if (completedRequest)
                    return MergePartialResults(partialResults);

                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"ComicVine: {ex.Message}", ex);
        }
    }

    private async Task<List<ScraperSearchResult>> SearchResourceTypesAsync(
        string apiKey,
        string query,
        string resourceTypes,
        CancellationToken cancellationToken)
    {
        // ComicVine Search API
        // https://comicvine.gamespot.com/api/search/?api_key={key}&format=json&query={query}&resources=volume
        var builder = new UriBuilder("https://comicvine.gamespot.com/api/search/");
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["api_key"] = apiKey;
        qs["format"] = "json";
        qs["query"] = query;
        qs["resources"] = resourceTypes;
        qs["limit"] = MaxSearchResults.ToString();
        builder.Query = qs.ToString();

        return await SendRequestAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ScraperSearchResult>> SearchIssuesForVolumesAsync(
        string apiKey,
        IReadOnlyList<ScraperSearchResult> volumes,
        CancellationToken cancellationToken)
    {
        var volumeIds = volumes
            .Where(result => string.Equals(result.ReleaseType, "volume", StringComparison.Ordinal))
            .Select(result => result.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.All(char.IsAsciiDigit))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxSearchResults)
            .ToList();
        if (volumeIds.Count == 0)
            return [];

        // ComicVine's list endpoint remains usable for affected queries and accepts
        // multiple volume IDs separated by a pipe, keeping recovery to one request.
        var builder = new UriBuilder("https://comicvine.gamespot.com/api/issues/");
        var qs = HttpUtility.ParseQueryString(string.Empty);
        qs["api_key"] = apiKey;
        qs["format"] = "json";
        qs["filter"] = $"volume:{string.Join('|', volumeIds)}";
        qs["limit"] = MaxSearchResults.ToString();
        builder.Query = qs.ToString();

        return await SendRequestAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ScraperSearchResult>> SendRequestAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");

        // ComicVine blocks requests without a clear User-Agent.
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            request.Headers.UserAgent.ParseAdd("Retromind/1.0 (Linux Portable Media Manager)");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = ExtractErrorDetail(json);
            throw new ComicVineApiException(
                response.StatusCode,
                $"ComicVine API error {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }

        return ParseResults(json);
    }

    private List<ScraperSearchResult> ParseResults(string json)
    {
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
            var displayTitle = title;
            if (resType == "issue" && !string.IsNullOrEmpty(volumeName))
            {
                title = $"{volumeName} #{issueNumber}";
                displayTitle = title;
                if (!string.IsNullOrEmpty(name)) displayTitle += $" - {name}";
            }
            else
            {
                title = name ?? "Unknown";
                displayTitle = title;
                var startYear = item?["start_year"]?.ToString();
                if (!string.IsNullOrEmpty(startYear)) displayTitle += $" ({startYear})";
            }

            var desc = item?["description"]?.ToString() ?? item?["deck"]?.ToString() ?? "";

            var res = new ScraperSearchResult
            {
                Source = "ComicVine",
                Id = id,
                Title = title,
                DisplayTitle = displayTitle,
                Description = StripHtml(desc),
                Series = volumeName,
                SortTitle = name,
                ReleaseType = resType,
                Publisher = item?["publisher"]?["name"]?.ToString()
            };

            if (!string.IsNullOrWhiteSpace(issueNumber))
                res.CustomFields["IssueNumber"] = issueNumber;

            var volumeId = item?["volume"]?["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(volumeId))
                res.CustomFields["VolumeId"] = volumeId;

            var startYearValue = item?["start_year"]?.ToString();
            if (!string.IsNullOrWhiteSpace(startYearValue))
                res.CustomFields["StartYear"] = startYearValue;

            var image = item?["image"];
            if (image != null)
            {
                res.CoverUrl = image["medium_url"]?.ToString();
                res.WallpaperUrl = image["screen_url"]?.ToString();
                res.ScreenshotUrl = image["screen_url"]?.ToString();
            }

            results.Add(res);
        }

        return results;
    }

    private static void ApplyIssueArtworkToVolumes(
        IReadOnlyList<ScraperSearchResult> volumes,
        IReadOnlyList<ScraperSearchResult> issues)
    {
        var firstIssueByVolumeId = issues
            .Where(issue => issue.CustomFields.TryGetValue("VolumeId", out var volumeId) &&
                            !string.IsNullOrWhiteSpace(volumeId))
            .GroupBy(issue => issue.CustomFields["VolumeId"], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var volume in volumes)
        {
            if (!firstIssueByVolumeId.TryGetValue(volume.Id, out var issue))
                continue;

            if (IsMissingOrPlaceholderImage(volume.CoverUrl))
                volume.CoverUrl = issue.CoverUrl;
            if (IsMissingOrPlaceholderImage(volume.WallpaperUrl))
                volume.WallpaperUrl = issue.WallpaperUrl;
            if (IsMissingOrPlaceholderImage(volume.ScreenshotUrl))
                volume.ScreenshotUrl = issue.ScreenshotUrl;
        }
    }

    private static bool IsMissingOrPlaceholderImage(string? url)
        => string.IsNullOrWhiteSpace(url) ||
           url.Contains("-blank.", StringComparison.OrdinalIgnoreCase) ||
           url.EndsWith("/blank.png", StringComparison.OrdinalIgnoreCase);

    private static List<ScraperSearchResult> MergePartialResults(
        IReadOnlyList<IReadOnlyList<ScraperSearchResult>> partialResults)
    {
        var merged = new List<ScraperSearchResult>(MaxSearchResults);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maximumCount = partialResults.Count == 0 ? 0 : partialResults.Max(results => results.Count);

        for (var resultIndex = 0; resultIndex < maximumCount && merged.Count < MaxSearchResults; resultIndex++)
        {
            foreach (var results in partialResults)
            {
                if (resultIndex >= results.Count)
                    continue;

                var result = results[resultIndex];
                var identity = $"{result.ReleaseType}\0{result.Id}";
                if (seen.Add(identity))
                    merged.Add(result);

                if (merged.Count >= MaxSearchResults)
                    break;
            }
        }

        return merged;
    }

    private static bool IsServerError(HttpStatusCode statusCode)
        => (int)statusCode >= 500;

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
        if (trimmed.StartsWith('<'))
            return "The service returned an unexpected HTML error page.";

        return trimmed.Length <= 200 ? trimmed : trimmed.Substring(0, 200) + "...";
    }

    private sealed class ComicVineApiException : Exception
    {
        public ComicVineApiException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }

    // Simple HTML stripper. A Regex or dedicated HTML parser would be more robust,
    // but this is sufficient for short text previews.
    private string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        return System.Text.RegularExpressions.Regex.Replace(input, "<.*?>", String.Empty);
    }
}
