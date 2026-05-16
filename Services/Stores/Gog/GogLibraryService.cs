using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;
using Retromind.Services.Stores.Gog.Auth;

namespace Retromind.Services.Stores.Gog;

public sealed class GogLibraryService
{
    private static readonly Uri FilteredProductsEndpoint = new("https://embed.gog.com/account/getFilteredProducts");
    private static readonly TimeSpan OwnedGamesCacheTtl = TimeSpan.FromMinutes(3);
    private readonly GogAuthService _authService;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _ownedGamesCacheLock = new(1, 1);
    private IReadOnlyList<StoreGameRecord>? _cachedOwnedGames;
    private DateTimeOffset _cachedOwnedGamesValidUntilUtc = DateTimeOffset.MinValue;

    public GogLibraryService(GogAuthService authService, HttpClient httpClient)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IReadOnlyList<StoreGameRecord>> GetOwnedGamesAsync(CancellationToken ct = default)
    {
        if (TryGetOwnedGamesFromCache(out var cached))
            return cached;

        await _ownedGamesCacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryGetOwnedGamesFromCache(out cached))
                return cached;

            var accessToken = await _authService.GetValidAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                ClearOwnedGamesCache();
                return [];
            }

            var records = new List<StoreGameRecord>();
            var seenGameIds = new HashSet<string>(StringComparer.Ordinal);

            var page = 1;
            var totalPages = 1;
            const int maxPages = 200;

            while (page <= totalPages && page <= maxPages)
            {
                var pageRoot = await GetFilteredProductsPageAsync(accessToken, page, ct).ConfigureAwait(false);

                var parsedTotalPages = ReadInt(pageRoot, "totalPages");
                if (parsedTotalPages.HasValue && parsedTotalPages.Value > 0)
                    totalPages = parsedTotalPages.Value;

                if (!pageRoot.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var product in products.EnumerateArray())
                {
                    var storeGameId = ReadString(product, "id");
                    if (string.IsNullOrWhiteSpace(storeGameId))
                        continue;

                    if (!seenGameIds.Add(storeGameId))
                        continue;

                    var title = ReadString(product, "title");
                    if (string.IsNullOrWhiteSpace(title))
                        title = $"GOG {storeGameId}";

                    var platform = InferPlatform(product);
                    records.Add(new StoreGameRecord("gog", storeGameId, title, platform, null));
                }

                page++;
            }

            var snapshot = records.ToArray();
            _cachedOwnedGames = snapshot;
            _cachedOwnedGamesValidUntilUtc = DateTimeOffset.UtcNow.Add(OwnedGamesCacheTtl);
            return snapshot;
        }
        finally
        {
            _ownedGamesCacheLock.Release();
        }
    }

    private bool TryGetOwnedGamesFromCache(out IReadOnlyList<StoreGameRecord> records)
    {
        if (_cachedOwnedGames is { } cached &&
            DateTimeOffset.UtcNow < _cachedOwnedGamesValidUntilUtc)
        {
            records = cached;
            return true;
        }

        records = Array.Empty<StoreGameRecord>();
        return false;
    }

    private void ClearOwnedGamesCache()
    {
        _cachedOwnedGames = null;
        _cachedOwnedGamesValidUntilUtc = DateTimeOffset.MinValue;
    }

    private async Task<JsonElement> GetFilteredProductsPageAsync(string accessToken, int page, CancellationToken ct)
    {
        var requestUri = new Uri($"{FilteredProductsEndpoint}?mediaType=1&page={page}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GOG library request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {ExtractErrorDetail(responseBody)}");

        using var json = JsonDocument.Parse(responseBody);
        return json.RootElement.Clone();
    }

    private static string? InferPlatform(JsonElement product)
    {
        if (!product.TryGetProperty("worksOn", out var worksOn) || worksOn.ValueKind != JsonValueKind.Object)
            return null;

        var supportsLinux = ReadBool(worksOn, "Linux");
        var supportsWindows = ReadBool(worksOn, "Windows");
        var supportsMac = ReadBool(worksOn, "Mac");

        if (supportsLinux)
            return "Linux";
        if (supportsWindows)
            return "Windows";
        if (supportsMac)
            return "Mac";

        return null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
        {
            return intValue;
        }

        return null;
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var numberValue) && numberValue != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var boolValue) && boolValue,
            _ => false
        };
    }

    private static string ExtractErrorDetail(string? jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
            return "No response body.";

        var trimmed = jsonOrText.Trim();
        return trimmed.Length <= 220 ? trimmed : trimmed[..220] + "...";
    }
}
