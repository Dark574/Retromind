using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Helpers;
using Retromind.Models;

namespace Retromind.Services.Scrapers;

/// <summary>
/// Metadata provider for TheGamesDB API (v1).
/// </summary>
public class TheGamesDbProvider : IMetadataProvider, IBulkMetadataProvider, IMetadataResultEnricher, IBulkGameSystemMetadataProvider
{
    private readonly ScraperConfig _config;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    private const string BaseUrl = "https://api.thegamesdb.net/v1";
    private const int MaxSearchResults = 40;
    private const int MaxManualPages = 5;
    private const int MaxBulkPages = 1;

    private readonly ConcurrentDictionary<string, string> _platformNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _genreNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _developerNames = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _publisherNames = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<ScraperSearchResult, PendingMetadata> _pendingMetadata = new();
    private readonly SemaphoreSlim _metadataLookupGate = new(1, 1);
    private readonly Lock _quotaLock = new();
    private bool _quotaExhausted;
    private string? _allowanceRefreshTimer;
    private DateTimeOffset? _allowanceRefreshAtUtc;
    private bool _allowanceRefreshProbeInProgress;

    public TheGamesDbProvider(ScraperConfig config, HttpClient httpClient)
        : this(config, httpClient, TimeProvider.System)
    {
    }

    internal TheGamesDbProvider(
        ScraperConfig config,
        HttpClient httpClient,
        TimeProvider timeProvider)
    {
        _config = config;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    private string GetApiKey()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new Exception("TheGamesDB requires an API key. Please enter it in the scraper settings.");

        return _config.ApiKey;
    }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = GetApiKey();
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public bool SupportsGameSystem(string? gameSystemId) =>
        TheGamesDbSystemCatalog.TryGetPlatformIds(gameSystemId, out _);

    public Task<List<ScraperSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        SearchCoreAsync(
            query,
            MaxManualPages,
            platformIds: null,
            cancellationToken);

    public Task<List<ScraperSearchResult>> SearchForBulkAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        SearchCoreAsync(
            query,
            MaxBulkPages,
            platformIds: null,
            cancellationToken);

    public Task<List<ScraperSearchResult>> SearchByGameSystemAsync(
        string query,
        string gameSystemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSystemId);

        if (!TheGamesDbSystemCatalog.TryGetPlatformIds(
                gameSystemId,
                out var platformIds))
        {
            throw new NotSupportedException(
                $"TheGamesDB does not support the game system '{gameSystemId}'.");
        }

        return SearchCoreAsync(
            query,
            MaxManualPages,
            platformIds,
            cancellationToken);
    }

    public Task<List<ScraperSearchResult>> SearchForBulkByGameSystemAsync(
        string query,
        string gameSystemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSystemId);

        if (!TheGamesDbSystemCatalog.TryGetPlatformIds(
                gameSystemId,
                out var platformIds))
        {
            throw new NotSupportedException(
                $"TheGamesDB does not support the game system '{gameSystemId}'.");
        }

        return SearchCoreAsync(
            query,
            MaxBulkPages,
            platformIds,
            cancellationToken);
    }

    private async Task<List<ScraperSearchResult>> SearchCoreAsync(
        string query,
        int maxPages,
        IReadOnlyList<int>? platformIds,
        CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(query))
            return new List<ScraperSearchResult>();

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var language = LanguageCodeHelper.NormalizePrimaryCode(_config.Language);
            var platformFilter = platformIds is { Count: > 0 }
                ? $"&filter%5Bplatform%5D={Uri.EscapeDataString(string.Join(",", platformIds))}"
                : string.Empty;
            var results = new List<ScraperSearchResult>(MaxSearchResults);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var page = 1; page <= maxPages && results.Count < MaxSearchResults; page++)
            {
                var url =
                    $"{BaseUrl}/Games/ByGameName?apikey={Uri.EscapeDataString(apiKey)}&name={encodedQuery}" +
                    "&fields=overview,genres,developers,publishers,players,platform,rating" +
                    "&include=boxart,platform" +
                    $"&filter%5Blanguage%5D={Uri.EscapeDataString(language)}" +
                    platformFilter +
                    $"&page={page}";

                var root = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);

                var games = root?["data"]?["games"]?.AsArray();
                if (games == null || games.Count == 0)
                    break;

                var platformData = root?["include"]?["platform"]?["data"];
                var genreData = root?["include"]?["genre"]?["data"]
                               ?? root?["include"]?["genres"]?["data"];
                var developerData = root?["include"]?["developer"]?["data"]
                                   ?? root?["include"]?["developers"]?["data"];
                var publisherData = root?["include"]?["publisher"]?["data"]
                                   ?? root?["include"]?["publishers"]?["data"];
                var boxartData = root?["include"]?["boxart"]?["data"] as JsonObject;
                var boxartBaseUrl = ResolveBoxartBaseUrl(root);
                CacheNames(_platformNames, ExtractPlatformNameMapFromInclude(platformData));
                CacheNames(_genreNames, ExtractIdNameMap(genreData));
                CacheNames(_developerNames, ExtractIdNameMap(developerData));
                CacheNames(_publisherNames, ExtractIdNameMap(publisherData));

                var countBeforePage = results.Count;

                foreach (var game in games)
                {
                    if (game == null)
                        continue;

                    var id = game["id"]?.ToString() ?? string.Empty;
                    var title = game["game_title"]?.ToString() ?? "Unknown";
                    var dedupeKey = string.IsNullOrWhiteSpace(id) ? title : id;
                    if (!seen.Add(dedupeKey))
                        continue;

                    var result = new ScraperSearchResult
                    {
                        Source = "TheGamesDB",
                        Id = id,
                        Title = title,
                        Description = game["overview"]?.ToString() ?? string.Empty,
                        Developer = ResolveCompanyName(game, _developerNames, _publisherNames),
                        Publisher = ResolvePublisher(game, _publisherNames),
                        Genre = ResolveGenres(game, _genreNames),
                        MaxPlayers = game["players"]?.ToString()
                    };

                    var rawRatingText = game["rating"]?.ToString();
                    if (TryParseRating(rawRatingText, out var rawRating))
                    {
                        result.Rating = rawRating;
                    }

                    if (DateTime.TryParse(game["release_date"]?.ToString(), out var releaseDate))
                    {
                        result.ReleaseDate = releaseDate;
                    }

                    result.Platform = ResolvePlatform(game, _platformNames);

                    _pendingMetadata.Add(
                        result,
                        new PendingMetadata(
                            ExtractPlatformIds(game).Distinct(StringComparer.Ordinal).ToArray(),
                            ExtractCompanyIds(FirstPresent(game, "genres", "genre"))
                                .Distinct(StringComparer.Ordinal).ToArray(),
                            ExtractCompanyIds(FirstPresent(game, "developers", "developer"))
                                .Distinct(StringComparer.Ordinal).ToArray(),
                            ExtractCompanyIds(FirstPresent(game, "publishers", "publisher"))
                                .Distinct(StringComparer.Ordinal).ToArray()));

                    if (!string.IsNullOrWhiteSpace(id) && boxartData != null && boxartData[id] is JsonArray artArray)
                    {
                        result.CoverUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => string.Equals(a["type"]?.ToString(), "boxart", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(a["side"]?.ToString(), "front", StringComparison.OrdinalIgnoreCase))
                                         ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                             a => string.Equals(a["type"]?.ToString(), "boxart", StringComparison.OrdinalIgnoreCase))
                                         ?? SelectBoxartUrl(artArray, boxartBaseUrl, _ => true);

                        result.WallpaperUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => string.Equals(a["type"]?.ToString(), "fanart", StringComparison.OrdinalIgnoreCase))
                                             ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                 a => string.Equals(a["type"]?.ToString(), "screenshot", StringComparison.OrdinalIgnoreCase))
                                             ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                 a => string.Equals(a["type"]?.ToString(), "banner", StringComparison.OrdinalIgnoreCase));

                        result.ScreenshotUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => string.Equals(a["type"]?.ToString(), "screenshot", StringComparison.OrdinalIgnoreCase))
                                              ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                  a => TypeEqualsOrContains(a["type"]?.ToString(), "screenshots"));

                        // TheGamesDB artwork naming can vary by entry (clearlogo/logo variants).
                        result.LogoUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => TypeEqualsOrContains(a["type"]?.ToString(), "clearlogo"))
                                         ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                             a => TypeEqualsOrContains(a["type"]?.ToString(), "logo"));

                        // For arcade systems (e.g. MAME), marquee can appear as "marquee" or banner-like artwork.
                        result.MarqueeUrl = SelectBoxartUrl(artArray, boxartBaseUrl,
                            a => TypeEqualsOrContains(a["type"]?.ToString(), "marquee"))
                                            ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                a => TypeEqualsOrContains(a["type"]?.ToString(), "wheel"))
                                            ?? SelectBoxartUrl(artArray, boxartBaseUrl,
                                                a => TypeEqualsOrContains(a["type"]?.ToString(), "banner"));
                    }

                    results.Add(result);
                    if (results.Count >= MaxSearchResults)
                        break;
                }

                // If pagination is ignored by the API and we keep receiving the same page, stop early.
                if (results.Count == countBeforePage || IsQuotaExhausted())
                    break;
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataQuotaExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"TheGamesDB error: {ex.Message}", ex);
        }
    }

    public Task EnrichAsync(
        ScraperSearchResult result,
        CancellationToken cancellationToken = default) =>
        EnrichAsync(result, MetadataEnrichmentRequest.All, cancellationToken);

    public async Task EnrichAsync(
        ScraperSearchResult result,
        MetadataEnrichmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(result.Id) || !request.HasAny)
            return;

        if (request.HasAnyMetadata)
            await EnrichMetadataNamesAsync(result, request, cancellationToken).ConfigureAwait(false);

        if (!NeedsArtworkEnrichment(result, request))
            return;

        await TryEnrichWithGameImagesAsync(
                GetApiKey(),
                result.Id,
                result,
                request,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool NeedsArtworkEnrichment(
        ScraperSearchResult result,
        MetadataEnrichmentRequest request) =>
        (request.Cover && string.IsNullOrWhiteSpace(result.CoverUrl)) ||
        (request.Wallpaper && string.IsNullOrWhiteSpace(result.WallpaperUrl)) ||
        (request.Screenshot && string.IsNullOrWhiteSpace(result.ScreenshotUrl)) ||
        (request.Logo && string.IsNullOrWhiteSpace(result.LogoUrl)) ||
        (request.Marquee && string.IsNullOrWhiteSpace(result.MarqueeUrl));

    private static string ResolveBoxartBaseUrl(JsonNode? root)
    {
        var boxartBase = root?["include"]?["boxart"]?["base_url"];
        var preferred = boxartBase?["original"]?.ToString()
                        ?? boxartBase?["large"]?.ToString()
                        ?? boxartBase?["medium"]?.ToString()
                        ?? string.Empty;

        return preferred.TrimEnd('/');
    }

    private static string? SelectBoxartUrl(JsonArray array, string baseUrl, Func<JsonNode, bool> predicate)
    {
        var match = array.FirstOrDefault(node => node != null && predicate(node));
        if (match == null)
            return null;

        var fileName = match["filename"]?.ToString();
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        if (fileName.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        if (string.IsNullOrEmpty(baseUrl))
            return null;

        return $"{baseUrl}/{fileName.TrimStart('/')}";
    }

    private static bool TypeEqualsOrContains(string? actualType, string expected)
    {
        if (string.IsNullOrWhiteSpace(actualType))
            return false;

        return actualType.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
               actualType.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRating(string? raw, out double rating)
    {
        rating = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var cleaned = raw.Trim();
        var m = Regex.Match(cleaned, @"(?<num>\d+(?:[.,]\d+)?)\s*(?:/\s*(?<den>\d+(?:[.,]\d+)?))?");
        if (!m.Success)
            return false;

        if (!TryParseFlexibleNumber(m.Groups["num"].Value, out var numerator))
            return false;

        if (m.Groups["den"].Success && TryParseFlexibleNumber(m.Groups["den"].Value, out var denominator) && denominator > 0)
        {
            rating = (numerator / denominator) * 100.0;
            rating = Math.Clamp(rating, 0, 100);
            return true;
        }

        if (cleaned.Contains('%'))
        {
            rating = Math.Clamp(numerator, 0, 100);
            return true;
        }

        rating = numerator <= 10 ? numerator * 10 : numerator;
        rating = Math.Clamp(rating, 0, 100);
        return true;
    }

    private static bool TryParseFlexibleNumber(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim().Replace(',', '.');
        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private async Task TryEnrichWithGameImagesAsync(
        string apiKey,
        string gameId,
        ScraperSearchResult result,
        MetadataEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        if (IsQuotaExhausted())
            return;

        try
        {
            var url = $"{BaseUrl}/Games/Images?apikey={Uri.EscapeDataString(apiKey)}&games_id={Uri.EscapeDataString(gameId)}";
            var root = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            var data = root?["data"];
            if (data == null)
                return;

            var baseUrl = data["base_url"]?["original"]?.ToString()
                          ?? data["base_url"]?["large"]?.ToString()
                          ?? data["base_url"]?["medium"]?.ToString()
                          ?? string.Empty;

            var candidates = new List<string>();
            CollectImageCandidates(data["images"] ?? data, candidates);
            if (candidates.Count == 0)
                return;

            var resolved = candidates
                .Select(c => ToAbsoluteImageUrl(baseUrl, c))
                .Where(IsLikelyImagePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (request.Logo && string.IsNullOrWhiteSpace(result.LogoUrl))
            {
                result.LogoUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "clearlogo", "/logo/", "_logo", "logo/"));
            }

            if (request.Cover && string.IsNullOrWhiteSpace(result.CoverUrl))
            {
                result.CoverUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "boxart/front", "boxart") && !ContainsAny(p, "back"));
            }

            if (request.Wallpaper && string.IsNullOrWhiteSpace(result.WallpaperUrl))
            {
                result.WallpaperUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "fanart", "screenshot", "background", "screenshots"));
            }

            if (request.Screenshot && string.IsNullOrWhiteSpace(result.ScreenshotUrl))
            {
                result.ScreenshotUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "screenshot", "screenshots", "screen"));
            }

            if (request.Marquee && string.IsNullOrWhiteSpace(result.MarqueeUrl))
            {
                result.MarqueeUrl = resolved.FirstOrDefault(p =>
                    ContainsAny(p, "marquee", "wheel", "banner"));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataQuotaExceededException)
        {
            throw;
        }
        catch
        {
            // Best effort only.
        }
    }

    private async Task EnrichMetadataNamesAsync(
        ScraperSearchResult result,
        MetadataEnrichmentRequest request,
        CancellationToken cancellationToken)
    {
        if (!_pendingMetadata.TryGetValue(result, out var pending))
            return;

        var apiKey = GetApiKey();
        if (request.Platform)
        {
            await EnsureNamesAsync(
                apiKey,
                "Platforms/ByPlatformID",
                pending.PlatformIds,
                _platformNames,
                cancellationToken,
                "platforms",
                "platform").ConfigureAwait(false);
            result.Platform ??= ResolveNames(pending.PlatformIds, _platformNames);
        }

        if (request.Genre)
        {
            await EnsureNamesAsync(
                apiKey,
                "Genres/ByGenreID",
                pending.GenreIds,
                _genreNames,
                cancellationToken,
                "genres",
                "genre").ConfigureAwait(false);
            result.Genre ??= ResolveNames(pending.GenreIds, _genreNames);
        }

        if (request.Developer)
        {
            await EnsureNamesAsync(
                apiKey,
                "Developers/ByDeveloperID",
                pending.DeveloperIds,
                _developerNames,
                cancellationToken,
                "developers",
                "developer").ConfigureAwait(false);
            await EnsureNamesAsync(
                apiKey,
                "Publishers/ByPublisherID",
                pending.PublisherIds,
                _publisherNames,
                cancellationToken,
                "publishers",
                "publisher").ConfigureAwait(false);
            result.Developer ??= ResolveNames(pending.DeveloperIds, _developerNames)
                                 ?? ResolveNames(pending.PublisherIds, _publisherNames);
        }

        if (request.Publisher)
        {
            await EnsureNamesAsync(
                apiKey,
                "Publishers/ByPublisherID",
                pending.PublisherIds,
                _publisherNames,
                cancellationToken,
                "publishers",
                "publisher").ConfigureAwait(false);
            result.Publisher ??= ResolveNames(pending.PublisherIds, _publisherNames);
        }
    }

    private async Task EnsureNamesAsync(
        string apiKey,
        string endpointPath,
        IReadOnlyCollection<string> ids,
        ConcurrentDictionary<string, string> cache,
        CancellationToken cancellationToken,
        params string[] collectionKeys)
    {
        if (IsQuotaExhausted())
            return;

        var missingIds = ids
            .Where(IsLikelyIdentifier)
            .Where(id => !cache.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missingIds.Length == 0)
            return;

        await _metadataLookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsQuotaExhausted())
                return;

            missingIds = missingIds.Where(id => !cache.ContainsKey(id)).ToArray();
            if (missingIds.Length == 0)
                return;

            var idsCsv = string.Join(",", missingIds);
            var url = $"{BaseUrl}/{endpointPath}?apikey={Uri.EscapeDataString(apiKey)}&id={Uri.EscapeDataString(idsCsv)}";
            var root = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            var dataNode = SelectCollection(root?["data"], collectionKeys);
            CacheNames(cache, ExtractIdNameMap(dataNode));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MetadataQuotaExceededException)
        {
            throw;
        }
        catch
        {
            // Optional names are best effort; retain values already cached.
        }
        finally
        {
            _metadataLookupGate.Release();
        }
    }

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var isAllowanceRefreshProbe = BeginRequestOrThrowIfQuotaExhausted();

        try
        {
            return await GetJsonCoreAsync(url, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteAllowanceRefreshProbe(isAllowanceRefreshProbe);
        }
    }

    private async Task<JsonNode?> GetJsonCoreAsync(string url, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("TheGamesDB could not be reached.");
        }

        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? root = null;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    root = JsonNode.Parse(payload);
                }
                catch when (response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("TheGamesDB returned an invalid response.");
                }
            }

            UpdateQuota(root);
            var apiCode = ReadInteger(root?["code"]);
            if (!response.IsSuccessStatusCode || apiCode is >= 400)
            {
                if (IsQuotaExhausted())
                    throw CreateQuotaException();

                if (response.StatusCode == HttpStatusCode.Forbidden || apiCode == 403)
                {
                    throw new MetadataQuotaExceededException(
                        "TheGamesDB rejected the API key or its request allowance is exhausted; bulk scraping was stopped.");
                }

                throw new InvalidOperationException($"TheGamesDB returned HTTP {(int)response.StatusCode}.");
            }

            return root;
        }
    }

    private bool BeginRequestOrThrowIfQuotaExhausted()
    {
        lock (_quotaLock)
        {
            if (!_quotaExhausted)
                return false;

            if (!_allowanceRefreshProbeInProgress &&
                _allowanceRefreshAtUtc is { } refreshAtUtc &&
                _timeProvider.GetUtcNow() >= refreshAtUtc)
            {
                _allowanceRefreshProbeInProgress = true;
                return true;
            }
        }

        throw CreateQuotaException();
    }

    private void CompleteAllowanceRefreshProbe(bool isAllowanceRefreshProbe)
    {
        if (!isAllowanceRefreshProbe)
            return;

        lock (_quotaLock)
            _allowanceRefreshProbeInProgress = false;
    }

    private bool IsQuotaExhausted()
    {
        lock (_quotaLock)
            return _quotaExhausted;
    }

    private MetadataQuotaExceededException CreateQuotaException()
    {
        string? refreshTimer;
        lock (_quotaLock)
            refreshTimer = _allowanceRefreshTimer;

        var suffix = string.IsNullOrWhiteSpace(refreshTimer) ||
                     (long.TryParse(refreshTimer, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                      seconds <= 0)
            ? string.Empty
            : $" Reset information: {refreshTimer}.";
        return new MetadataQuotaExceededException(
            $"TheGamesDB monthly request allowance is exhausted.{suffix}");
    }

    private void UpdateQuota(JsonNode? root)
    {
        var remaining = ReadInteger(root?["remaining_monthly_allowance"]);
        if (!remaining.HasValue)
            return;

        var extra = ReadInteger(root?["extra_allowance"]) ?? 0;
        var refreshTimer = root?["allowance_refresh_timer"]?.ToString();
        var refreshAtUtc = CalculateAllowanceRefreshAtUtc(refreshTimer);
        lock (_quotaLock)
        {
            _quotaExhausted = remaining.Value <= 0 && extra <= 0;
            _allowanceRefreshTimer = refreshTimer;
            _allowanceRefreshAtUtc = _quotaExhausted ? refreshAtUtc : null;
        }
    }

    private DateTimeOffset? CalculateAllowanceRefreshAtUtc(string? refreshTimer)
    {
        if (!long.TryParse(
                refreshTimer,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var refreshSeconds) ||
            refreshSeconds < 0)
        {
            return null;
        }

        try
        {
            return _timeProvider.GetUtcNow().AddSeconds(refreshSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int? ReadInteger(JsonNode? node)
    {
        return int.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static JsonNode? SelectCollection(JsonNode? dataNode, IReadOnlyList<string> collectionKeys)
    {
        if (dataNode is not JsonObject dataObject)
            return dataNode;

        foreach (var key in collectionKeys)
        {
            if (dataObject[key] is { } collection)
                return collection;
        }

        return dataNode;
    }

    private static void CacheNames(
        ConcurrentDictionary<string, string> cache,
        IReadOnlyDictionary<string, string> names)
    {
        foreach (var pair in names)
            cache.TryAdd(pair.Key, pair.Value);
    }

    private static string? ResolveNames(
        IEnumerable<string> ids,
        IReadOnlyDictionary<string, string> names)
    {
        var resolved = ids
            .Select(id => names.TryGetValue(id, out var name) ? name : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return resolved.Length == 0 ? null : string.Join(", ", resolved!);
    }

    private sealed record PendingMetadata(
        string[] PlatformIds,
        string[] GenreIds,
        string[] DeveloperIds,
        string[] PublisherIds);

    private static string? ResolvePlatform(JsonNode game, IReadOnlyDictionary<string, string> platformNameById)
    {
        var names = new List<string>();
        foreach (var id in ExtractPlatformIds(game))
        {
            if (!platformNameById.TryGetValue(id, out var name) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names.Count == 0
            ? null
            : string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ExtractPlatformIds(JsonNode game)
    {
        static IEnumerable<string> SplitIds(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                    yield return part;
            }
        }

        foreach (var id in SplitIds(game["platform"]?.ToString()))
            yield return id;

        if (game["platforms"] is JsonArray arr)
        {
            foreach (var p in arr)
            {
                var value = p?["id"]?.ToString() ?? p?.ToString();
                foreach (var id in SplitIds(value))
                    yield return id;
            }
        }
    }

    private static string? ResolveCompanyName(
        JsonNode game,
        IReadOnlyDictionary<string, string> developerNameById,
        IReadOnlyDictionary<string, string> publisherNameById)
    {
        var developersNode = FirstPresent(game, "developers", "developer");
        var publishersNode = FirstPresent(game, "publishers", "publisher");

        var explicitDevName = FirstMeaningfulText(ExtractCompanyNames(developersNode));
        if (!string.IsNullOrWhiteSpace(explicitDevName))
            return explicitDevName;

        foreach (var id in ExtractCompanyIds(developersNode))
        {
            if (developerNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        var explicitPubName = FirstMeaningfulText(ExtractCompanyNames(publishersNode));
        if (!string.IsNullOrWhiteSpace(explicitPubName))
            return explicitPubName;

        foreach (var id in ExtractCompanyIds(publishersNode))
        {
            if (publisherNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static string? ResolvePublisher(
        JsonNode game,
        IReadOnlyDictionary<string, string> publisherNameById)
    {
        var publishersNode = FirstPresent(game, "publishers", "publisher");

        var explicitPubName = FirstMeaningfulText(ExtractCompanyNames(publishersNode));
        if (!string.IsNullOrWhiteSpace(explicitPubName))
            return explicitPubName;

        foreach (var id in ExtractCompanyIds(publishersNode))
        {
            if (publisherNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static string? ResolveGenres(
        JsonNode game,
        IReadOnlyDictionary<string, string> genreNameById)
    {
        var genresNode = FirstPresent(game, "genres", "genre");

        var explicitNames = ExtractCompanyNames(genresNode)
            .Select(v => v.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v) && !IsLikelyIdentifier(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (explicitNames.Count > 0)
            return string.Join(", ", explicitNames);

        var resolved = new List<string>();
        foreach (var id in ExtractCompanyIds(genresNode))
        {
            if (genreNameById.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name))
                resolved.Add(name);
        }

        resolved = resolved
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return resolved.Count > 0
            ? string.Join(", ", resolved)
            : null;
    }

    private static string? FirstMeaningfulText(IEnumerable<string> values)
    {
        return values
            .Select(v => v?.Trim())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !IsLikelyIdentifier(v));
    }

    private static IEnumerable<string> ExtractCompanyNames(JsonNode? node)
    {
        if (node == null)
            yield break;

        if (node is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry == null)
                    continue;

                if (entry is JsonObject obj)
                {
                    var name = obj["name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        yield return name;
                }
                else
                {
                    var raw = entry.ToString();
                    if (!string.IsNullOrWhiteSpace(raw))
                        yield return raw;
                }
            }

            yield break;
        }

        if (node is JsonObject singleObj)
        {
            var name = singleObj["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                yield return name;
            yield break;
        }

        var single = node.ToString();
        if (!string.IsNullOrWhiteSpace(single))
            yield return single;
    }

    private static IEnumerable<string> ExtractCompanyIds(JsonNode? node)
    {
        if (node == null)
            yield break;

        static IEnumerable<string> SplitValues(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                yield break;

            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                    yield return part;
            }
        }

        if (node is JsonArray arr)
        {
            foreach (var entry in arr)
            {
                if (entry == null)
                    continue;

                if (entry is JsonObject obj)
                {
                    foreach (var id in SplitValues(obj["id"]?.ToString()))
                        yield return id;
                }
                else
                {
                    foreach (var id in SplitValues(entry.ToString()))
                        yield return id;
                }
            }

            yield break;
        }

        if (node is JsonObject singleObj)
        {
            foreach (var id in SplitValues(singleObj["id"]?.ToString()))
                yield return id;
            yield break;
        }

        foreach (var id in SplitValues(node.ToString()))
            yield return id;
    }

    private static JsonNode? FirstPresent(JsonNode? parent, params string[] keys)
    {
        if (parent == null || keys == null || keys.Length == 0)
            return null;

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            var candidate = parent[key];
            if (candidate != null)
                return candidate;
        }

        return null;
    }

    private static Dictionary<string, string> ExtractIdNameMap(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node == null)
            return result;

        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value == null)
                    continue;

                if (kv.Value is JsonObject childObj)
                {
                    var id = childObj["id"]?.ToString();
                    var name = ExtractDisplayName(childObj);

                    if (string.IsNullOrWhiteSpace(id))
                        id = kv.Key;

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        result[id] = name;
                }
                else
                {
                    // Sometimes payloads are keyed by id with plain string values.
                    var valueText = kv.Value.ToString();
                    if (IsLikelyIdentifier(kv.Key) && !string.IsNullOrWhiteSpace(valueText))
                        result[kv.Key] = valueText;
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                var id = item?["id"]?.ToString();
                var name = item is JsonObject itemObj ? ExtractDisplayName(itemObj) : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    result[id] = name;
            }
        }

        return result;
    }

    private static string? ExtractDisplayName(JsonObject obj)
    {
        return obj["name"]?.ToString()
               ?? obj["genre"]?.ToString()
               ?? obj["title"]?.ToString()
               ?? obj["developer"]?.ToString()
               ?? obj["publisher"]?.ToString()
               ?? obj["platform"]?.ToString()
               ?? obj["value"]?.ToString();
    }

    private static void CollectImageCandidates(JsonNode? node, List<string> sink)
    {
        if (node == null)
            return;

        if (node is JsonValue val)
        {
            var text = val.ToString();
            if (IsLikelyImagePath(text))
                sink.Add(text);
            return;
        }

        if (node is JsonArray arr)
        {
            foreach (var child in arr)
                CollectImageCandidates(child, sink);
            return;
        }

        if (node is JsonObject obj)
        {
            var filename = obj["filename"]?.ToString();
            if (IsLikelyImagePath(filename))
                sink.Add(filename!);

            var url = obj["url"]?.ToString();
            if (IsLikelyImagePath(url))
                sink.Add(url!);

            foreach (var kv in obj)
                CollectImageCandidates(kv.Value, sink);
        }
    }

    private static bool IsLikelyImagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var v = value.Trim();
        return v.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
               || v.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
               || v.Contains(".png", StringComparison.OrdinalIgnoreCase)
               || v.Contains(".webp", StringComparison.OrdinalIgnoreCase)
               || v.Contains("boxart", StringComparison.OrdinalIgnoreCase)
               || v.Contains("fanart", StringComparison.OrdinalIgnoreCase)
               || v.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
               || v.Contains("clearlogo", StringComparison.OrdinalIgnoreCase)
               || v.Contains("marquee", StringComparison.OrdinalIgnoreCase)
               || v.Contains("banner", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToAbsoluteImageUrl(string baseUrl, string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl;
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
            return pathOrUrl;

        return $"{baseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsLikelyIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return long.TryParse(value, out _);
    }

    private static Dictionary<string, string> ExtractPlatformNameMapFromInclude(JsonNode? platformData)
    {
        if (platformData == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return ExtractPlatformNameMapFromPlatformsNode(platformData);
    }

    private static Dictionary<string, string> ExtractPlatformNameMapFromPlatformsNode(JsonNode? platformsNode)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (platformsNode == null)
            return result;

        // Shape A: object keyed by platform id.
        if (platformsNode is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (kv.Value == null)
                    continue;

                var id = kv.Key;
                var node = kv.Value;
                var embeddedId = node["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(embeddedId))
                    id = embeddedId;

                var name = node["name"]?.ToString() ?? node.ToString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    result[id] = name;
            }
        }

        // Shape B: array of platform objects.
        if (platformsNode is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node == null)
                    continue;

                var id = node["id"]?.ToString();
                var name = node["name"]?.ToString() ?? node.ToString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    result[id] = name;
            }
        }

        return result;
    }
}
