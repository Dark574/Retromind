using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Artwork-focused metadata provider for the SteamGridDB API v2.
/// The search endpoint supplies game identities; artwork is loaded from the
/// dedicated grid, hero and logo endpoints.
/// </summary>
public sealed class SteamGridDbProvider :
    IMetadataProvider,
    IMetadataResultEnricher,
    IMetadataSearchPreviewEnricher
{
    private const string BaseUrl = "https://www.steamgriddb.com/api/v2";
    private const int MaxSearchResults = 20;
    private const int MaxConcurrentRequests = 4;
    private const int MaxRateLimitRetries = 2;

    private const string SafeStaticAssetQuery =
        "types=static&nsfw=false&humor=false&epilepsy=false&limit=1";

    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);

    public SteamGridDbProvider(ScraperConfig config, HttpClient httpClient)
    {
        _config = config;
        _httpClient = httpClient;
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!string.IsNullOrWhiteSpace(_config.ApiKey));
    }

    public async Task<List<ScraperSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<ScraperSearchResult>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query.Trim());
            var root = await GetJsonAsync(
                    $"/search/autocomplete/{encodedQuery}",
                    notFoundIsEmpty: true,
                    cancellationToken)
                .ConfigureAwait(false);

            var items = root?["data"] as JsonArray;
            if (items == null || items.Count == 0)
                return new List<ScraperSearchResult>();

            var results = new List<ScraperSearchResult>(Math.Min(items.Count, MaxSearchResults));
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in items)
            {
                var id = item?["id"]?.ToString();
                var title = item?["name"]?.ToString();

                if (string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(title) ||
                    !seenIds.Add(id))
                {
                    continue;
                }

                results.Add(new ScraperSearchResult
                {
                    Source = "SteamGridDB",
                    Id = id,
                    Title = title
                });

                if (results.Count >= MaxSearchResults)
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
            throw new Exception($"SteamGridDB error: {ex.Message}", ex);
        }
    }

    public async Task EnrichPreviewsAsync(
        IReadOnlyList<ScraperSearchResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        // Every displayed row needs a cover. Otherwise the dialog's reserved
        // cover area looks like a failed/white image even though no URL was loaded.
        await Task.WhenAll(results.Select(result => EnrichCoverAsync(result, cancellationToken)))
            .ConfigureAwait(false);

        // All additional artwork is loaded generically when a result is selected.
    }

    public async Task EnrichAsync(
        ScraperSearchResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (string.IsNullOrWhiteSpace(result.Id))
            return;

        var gameId = Uri.EscapeDataString(result.Id);
        var coverTask = string.IsNullOrWhiteSpace(result.CoverUrl)
            ? TryGetFirstAssetUrlAsync(
                $"/grids/game/{gameId}?dimensions=600x900,342x482,660x930,512x512,1024x1024&{SafeStaticAssetQuery}",
                cancellationToken)
            : Task.FromResult<string?>(null);
        var wallpaperTask = string.IsNullOrWhiteSpace(result.WallpaperUrl)
            ? TryGetFirstAssetUrlAsync(
                $"/heroes/game/{gameId}?{SafeStaticAssetQuery}",
                cancellationToken)
            : Task.FromResult<string?>(null);
        var logoTask = string.IsNullOrWhiteSpace(result.LogoUrl)
            ? TryGetFirstAssetUrlAsync(
                $"/logos/game/{gameId}?{SafeStaticAssetQuery}",
                cancellationToken)
            : Task.FromResult<string?>(null);

        await Task.WhenAll(coverTask, wallpaperTask, logoTask).ConfigureAwait(false);

        result.CoverUrl ??= await coverTask.ConfigureAwait(false);
        result.WallpaperUrl ??= await wallpaperTask.ConfigureAwait(false);
        result.LogoUrl ??= await logoTask.ConfigureAwait(false);
    }

    private async Task EnrichCoverAsync(
        ScraperSearchResult result,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(result.CoverUrl) || string.IsNullOrWhiteSpace(result.Id))
            return;

        var gameId = Uri.EscapeDataString(result.Id);
        result.CoverUrl = await TryGetFirstAssetUrlAsync(
                $"/grids/game/{gameId}?dimensions=600x900,342x482,660x930,512x512,1024x1024&{SafeStaticAssetQuery}",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string?> TryGetFirstAssetUrlAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = await GetJsonAsync(relativeUrl, notFoundIsEmpty: true, cancellationToken)
                .ConfigureAwait(false);
            var assets = root?["data"] as JsonArray;
            if (assets == null)
                return null;

            return assets
                .Where(asset => asset != null)
                .OrderByDescending(asset => ReadInt(asset?["score"]))
                .Select(asset => asset?["url"]?.ToString())
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Artwork endpoints are independent. A missing or temporarily failing
            // asset category must not discard the other usable artwork.
            Debug.WriteLine($"[SteamGridDB] Artwork request failed ({relativeUrl}): {ex.Message}");
            return null;
        }
    }

    private async Task<JsonNode?> GetJsonAsync(
        string relativeUrl,
        bool notFoundIsEmpty,
        CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();

        for (var attempt = 0; attempt <= MaxRateLimitRetries; attempt++)
        {
            TimeSpan? retryDelay = null;

            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + relativeUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (notFoundIsEmpty && response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(
                        "The SteamGridDB API key was rejected. Please check the scraper settings.");

                if ((int)response.StatusCode == 429 && attempt < MaxRateLimitRetries)
                {
                    retryDelay = GetRetryDelay(response, attempt);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);
                }
            }
            finally
            {
                _requestGate.Release();
            }

            if (retryDelay.HasValue)
                await Task.Delay(retryDelay.Value, cancellationToken).ConfigureAwait(false);
        }

        throw new HttpRequestException("SteamGridDB rate limit remained active after retries.");
    }

    private string GetApiKey()
    {
        var apiKey = _config.ApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "SteamGridDB requires an API key. Please enter it in the scraper settings.");
        }

        return apiKey;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
                return remaining;
        }

        return TimeSpan.FromMilliseconds(500 * (attempt + 1));
    }

    private static int ReadInt(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<int>(out var number))
            return number;

        return int.TryParse(node?.ToString(), out var parsed) ? parsed : 0;
    }
}
