using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Services.Achievements;

namespace Retromind.Services.SteamAchievements;

public sealed class SteamApiClient
{
    private const int SchemaCacheVersion = 1;
    private const int SchemaSynchronizationGateCount = 64;
    private static readonly TimeSpan SchemaCacheLifetime = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        WriteIndented = false
    };
    private const string PlayerSummariesEndpoint =
        "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";
    private const string GameSchemaEndpoint =
        "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/";
    private const string PlayerAchievementsEndpoint =
        "https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string>? _schemaCacheDirectoryProvider;
    private readonly SemaphoreSlim[] _schemaGates = CreateSchemaSynchronizationGates();

    public SteamApiClient(HttpClient httpClient)
        : this(httpClient, TimeProvider.System, schemaCacheDirectoryProvider: null)
    {
    }

    public SteamApiClient(
        HttpClient httpClient,
        StoreAchievementCachePathProvider cachePathProvider)
        : this(
            httpClient,
            TimeProvider.System,
            cachePathProvider == null
                ? throw new ArgumentNullException(nameof(cachePathProvider))
                : cachePathProvider.GetSteamSchemaCacheDirectory)
    {
    }

    internal SteamApiClient(
        HttpClient httpClient,
        TimeProvider timeProvider,
        string schemaCacheDirectory)
        : this(
            httpClient,
            timeProvider,
            () => Path.GetFullPath(schemaCacheDirectory))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaCacheDirectory);
    }

    private SteamApiClient(
        HttpClient httpClient,
        TimeProvider timeProvider,
        Func<string>? schemaCacheDirectoryProvider)
    {
        _httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _schemaCacheDirectoryProvider = schemaCacheDirectoryProvider;
    }

    public async Task<SteamUserProfile> GetPlayerSummaryAsync(
        string steamId64,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamId64);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        steamId64 = steamId64.Trim();

        if (!IsSteamId64(steamId64))
        {
            throw new ArgumentException(
                "The SteamID64 must contain exactly 17 digits.",
                nameof(steamId64));
        }

        var requestUri = new Uri(
            $"{PlayerSummariesEndpoint}?steamids={steamId64}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation(
            "x-webapi-key",
            apiKey.Trim());

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new SteamApiException(
                "The Steam Web API request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new SteamApiException(
                "The Steam Web API could not be reached.");
        }

        using (response)
        {
            var responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new SteamApiException(
                    response.StatusCode,
                    $"Steam rejected the profile request " +
                    $"(HTTP {(int)response.StatusCode}).");
            }

            try
            {
                using var json = JsonDocument.Parse(responseBody);
                var root = json.RootElement;

                if (!root.TryGetProperty("response", out var responseElement) ||
                    responseElement.ValueKind != JsonValueKind.Object ||
                    !responseElement.TryGetProperty("players", out var players) ||
                    players.ValueKind != JsonValueKind.Array)
                {
                    throw new SteamApiException(
                        "Steam returned an invalid profile response.");
                }

                foreach (var player in players.EnumerateArray())
                {
                    var returnedSteamId = ReadOptionalString(
                        player,
                        "steamid");

                    if (!string.Equals(
                            returnedSteamId,
                            steamId64,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var personaName = ReadOptionalString(
                        player,
                        "personaname");

                    if (string.IsNullOrWhiteSpace(personaName))
                    {
                        throw new SteamApiException(
                            "Steam returned a profile without a display name.");
                    }

                    return new SteamUserProfile(
                        steamId64,
                        personaName,
                        ReadOptionalString(player, "profileurl"),
                        ReadOptionalString(player, "avatarfull"));
                }

                throw new SteamApiException(
                    "Steam did not return the requested account. " +
                    "Check the SteamID64 and the profile privacy settings.");
            }
            catch (SteamApiException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new SteamApiException(
                    "Steam returned an invalid profile response.",
                    ex);
            }
        }
    }

    public async Task<SteamGameProgress> GetGameProgressAsync(
        uint appId,
        string steamId64,
        string apiKey,
        string language = "english",
        CancellationToken cancellationToken = default)
    {
        if (appId == 0)
            throw new ArgumentOutOfRangeException(nameof(appId));

        ArgumentException.ThrowIfNullOrWhiteSpace(steamId64);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        steamId64 = steamId64.Trim();
        if (!IsSteamId64(steamId64))
        {
            throw new ArgumentException(
                "The SteamID64 must contain exactly 17 digits.",
                nameof(steamId64));
        }

        language = string.IsNullOrWhiteSpace(language)
            ? "english"
            : language.Trim();

        var appIdText = appId.ToString(CultureInfo.InvariantCulture);
        var encodedLanguage = Uri.EscapeDataString(language);
        var schema = await GetGameSchemaAsync(
                appId,
                appIdText,
                language,
                encodedLanguage,
                apiKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (schema.Achievements.Count == 0)
        {
            return new SteamGameProgress
            {
                AppId = appId,
                Title = schema.Title
            };
        }

        var progressUri = new Uri(
            $"{PlayerAchievementsEndpoint}?steamid={steamId64}" +
            $"&appid={appIdText}&l={encodedLanguage}");
        var progressBody = await SendAuthenticatedRequestAsync(
            progressUri,
            apiKey,
            "achievement progress",
            cancellationToken).ConfigureAwait(false);
        var playerProgress = ParsePlayerProgress(
            progressBody,
            steamId64);

        var achievements = new List<SteamAchievement>(schema.Achievements.Count);
        foreach (var definition in schema.Achievements)
        {
            playerProgress.Achievements.TryGetValue(
                definition.ApiName,
                out var userState);

            var isUnlocked = userState?.IsUnlocked == true;
            var hideDetails = definition.IsHidden && !isUnlocked;
            achievements.Add(new SteamAchievement
            {
                ApiName = definition.ApiName,
                Title = hideDetails ? string.Empty : definition.Title,
                Description = hideDetails ? string.Empty : definition.Description,
                IsHidden = definition.IsHidden,
                IsUnlocked = isUnlocked,
                UnlockedAtUtc = isUnlocked ? userState?.UnlockedAtUtc : null,
                ImageUrl = hideDetails
                    ? null
                    : isUnlocked
                        ? definition.UnlockedImageUrl
                        : definition.LockedImageUrl
            });
        }

        return new SteamGameProgress
        {
            AppId = appId,
            Title = FirstNonEmpty(playerProgress.GameTitle, schema.Title) ?? string.Empty,
            Achievements = achievements
        };
    }

    private async Task<GameSchema> GetGameSchemaAsync(
        uint appId,
        string appIdText,
        string language,
        string encodedLanguage,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var schemaUri = new Uri(
            $"{GameSchemaEndpoint}?appid={appIdText}&l={encodedLanguage}");
        if (_schemaCacheDirectoryProvider == null)
        {
            var responseBody = await SendAuthenticatedRequestAsync(
                    schemaUri,
                    apiKey,
                    "game schema",
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseGameSchema(responseBody, appId);
        }

        var normalizedLanguage = language.ToLowerInvariant();
        var cacheDirectory = _schemaCacheDirectoryProvider();
        var cachePath = GetSchemaCachePath(cacheDirectory, appId, normalizedLanguage);
        var gate = _schemaGates[(int)(appId % SchemaSynchronizationGateCount)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = await TryLoadSchemaCacheAsync(
                    cachePath,
                    appId,
                    normalizedLanguage,
                    cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (cached != null && IsFreshSchema(cached, now))
                return cached.Schema;

            try
            {
                var responseBody = await SendAuthenticatedRequestAsync(
                        schemaUri,
                        apiKey,
                        "game schema",
                        cancellationToken)
                    .ConfigureAwait(false);
                var schema = ParseGameSchema(responseBody, appId);
                await TrySaveSchemaCacheAsync(
                        cachePath,
                        appId,
                        normalizedLanguage,
                        responseBody,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return schema;
            }
            catch (SteamApiException) when (cached != null)
            {
                return cached.Schema;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<SchemaCacheEntry?> TryLoadSchemaCacheAsync(
        string cachePath,
        uint appId,
        string language,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
            return null;

        try
        {
            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<SchemaCacheDocument>(
                    stream,
                    CacheJsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (document == null ||
                document.SchemaVersion != SchemaCacheVersion ||
                document.AppId != appId ||
                !string.Equals(document.Language, language, StringComparison.Ordinal) ||
                document.FetchedAtUtc == default ||
                string.IsNullOrWhiteSpace(document.ResponseBody))
            {
                return null;
            }

            return new SchemaCacheEntry(
                ParseGameSchema(document.ResponseBody, appId),
                document.FetchedAtUtc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   JsonException or
                                   SteamApiException)
        {
            return null;
        }
    }

    private static async Task TrySaveSchemaCacheAsync(
        string cachePath,
        uint appId,
        string language,
        string responseBody,
        DateTimeOffset fetchedAtUtc,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.GetDirectoryName(cachePath)!;
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        new SchemaCacheDocument
                        {
                            SchemaVersion = SchemaCacheVersion,
                            AppId = appId,
                            Language = language,
                            FetchedAtUtc = fetchedAtUtc,
                            ResponseBody = responseBody
                        },
                        CacheJsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static string GetSchemaCachePath(
        string cacheDirectory,
        uint appId,
        string language)
    {
        var languageHash = SHA256.HashData(Encoding.UTF8.GetBytes(language));
        return Path.Combine(
            cacheDirectory,
            Convert.ToHexStringLower(languageHash),
            $"app-{appId.ToString(CultureInfo.InvariantCulture)}.json");
    }

    private static bool IsFreshSchema(SchemaCacheEntry entry, DateTimeOffset now) =>
        entry.FetchedAtUtc <= now && now - entry.FetchedAtUtc < SchemaCacheLifetime;

    private static SemaphoreSlim[] CreateSchemaSynchronizationGates()
    {
        var gates = new SemaphoreSlim[SchemaSynchronizationGateCount];
        for (var index = 0; index < gates.Length; index++)
            gates[index] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A later cache write uses a unique temporary path.
        }
    }

    private async Task<string> SendAuthenticatedRequestAsync(
        Uri requestUri,
        string apiKey,
        string requestDescription,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.TryAddWithoutValidation(
            "x-webapi-key",
            apiKey.Trim());

        try
        {
            using var response = await _httpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            var responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new SteamApiException(
                    response.StatusCode,
                    $"Steam rejected the {requestDescription} request " +
                    $"(HTTP {(int)response.StatusCode}).");
            }

            return responseBody;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new SteamApiException(
                "The Steam Web API request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new SteamApiException(
                "The Steam Web API could not be reached.",
                ex);
        }
        catch (IOException ex)
        {
            throw new SteamApiException(
                "The Steam Web API response could not be read.",
                ex);
        }
    }

    private static GameSchema ParseGameSchema(string responseBody, uint appId)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            if (!root.TryGetProperty("game", out var game) ||
                game.ValueKind != JsonValueKind.Object)
            {
                throw new SteamApiException(
                    "Steam returned an invalid game schema response.");
            }

            var title = ReadOptionalString(game, "gameName") ?? string.Empty;
            var achievements = new List<AchievementDefinition>();
            if (!game.TryGetProperty("availableGameStats", out var availableStats) ||
                availableStats.ValueKind != JsonValueKind.Object ||
                !availableStats.TryGetProperty("achievements", out var values))
            {
                return new GameSchema(title, achievements);
            }

            if (values.ValueKind != JsonValueKind.Array)
            {
                throw new SteamApiException(
                    "Steam returned an invalid game schema response.");
            }

            var knownNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.EnumerateArray())
            {
                var apiName = ReadOptionalString(value, "name");
                if (string.IsNullOrWhiteSpace(apiName) ||
                    !knownNames.Add(apiName))
                {
                    continue;
                }

                achievements.Add(new AchievementDefinition(
                    apiName,
                    ReadOptionalString(value, "displayName") ?? apiName,
                    ReadOptionalString(value, "description") ?? string.Empty,
                    ReadBoolean(value, "hidden"),
                    ReadOptionalString(value, "icon"),
                    ReadOptionalString(value, "icongray")));
            }

            return new GameSchema(title, achievements);
        }
        catch (SteamApiException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SteamApiException(
                $"Steam returned an invalid game schema response for app {appId}.",
                ex);
        }
    }

    private static PlayerProgress ParsePlayerProgress(
        string responseBody,
        string steamId64)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            if (!root.TryGetProperty("playerstats", out var playerStats) ||
                playerStats.ValueKind != JsonValueKind.Object)
            {
                throw new SteamApiException(
                    "Steam returned an invalid achievement progress response.");
            }

            if (playerStats.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                throw new SteamApiException(
                    "Steam did not return achievement progress. " +
                    "Check the game details and the profile privacy settings.");
            }

            var returnedSteamId = ReadOptionalString(playerStats, "steamID");
            if (!string.IsNullOrWhiteSpace(returnedSteamId) &&
                !string.Equals(returnedSteamId, steamId64, StringComparison.Ordinal))
            {
                throw new SteamApiException(
                    "Steam returned achievement progress for another account.");
            }

            var achievements = new Dictionary<string, PlayerAchievementState>(
                StringComparer.Ordinal);
            if (playerStats.TryGetProperty("achievements", out var values))
            {
                if (values.ValueKind != JsonValueKind.Array)
                {
                    throw new SteamApiException(
                        "Steam returned an invalid achievement progress response.");
                }

                foreach (var value in values.EnumerateArray())
                {
                    var apiName = ReadOptionalString(value, "apiname");
                    if (string.IsNullOrWhiteSpace(apiName))
                        continue;

                    var isUnlocked = ReadBoolean(value, "achieved");
                    achievements[apiName] = new PlayerAchievementState(
                        isUnlocked,
                        isUnlocked ? ReadUnixTimestamp(value, "unlocktime") : null);
                }
            }

            return new PlayerProgress(
                ReadOptionalString(playerStats, "gameName"),
                achievements);
        }
        catch (SteamApiException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new SteamApiException(
                "Steam returned an invalid achievement progress response.",
                ex);
        }
    }

    private static string? ReadOptionalString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBoolean(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => property.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String =>
                int.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var number) && number != 0,
            _ => false
        };
    }

    private static DateTimeOffset? ReadUnixTimestamp(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        long seconds;
        if (property.ValueKind == JsonValueKind.Number)
        {
            if (!property.TryGetInt64(out seconds))
                return null;
        }
        else if (property.ValueKind == JsonValueKind.String)
        {
            if (!long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out seconds))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (seconds <= 0)
            return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static bool IsSteamId64(string value)
    {
        return value.Length == 17 &&
               value.All(character => character is >= '0' and <= '9');
    }

    private sealed record GameSchema(
        string Title,
        IReadOnlyList<AchievementDefinition> Achievements);

    private sealed record AchievementDefinition(
        string ApiName,
        string Title,
        string Description,
        bool IsHidden,
        string? UnlockedImageUrl,
        string? LockedImageUrl);

    private sealed record PlayerProgress(
        string? GameTitle,
        IReadOnlyDictionary<string, PlayerAchievementState> Achievements);

    private sealed record PlayerAchievementState(
        bool IsUnlocked,
        DateTimeOffset? UnlockedAtUtc);

    private sealed record SchemaCacheEntry(
        GameSchema Schema,
        DateTimeOffset FetchedAtUtc);

    private sealed class SchemaCacheDocument
    {
        public int SchemaVersion { get; set; }
        public uint AppId { get; set; }
        public string Language { get; set; } = string.Empty;
        public DateTimeOffset FetchedAtUtc { get; set; }
        public string ResponseBody { get; set; } = string.Empty;
    }
}
