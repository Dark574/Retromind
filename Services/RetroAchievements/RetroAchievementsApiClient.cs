using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.RetroAchievements;

/// <summary>
/// Minimal client for the public RetroAchievements Web API.
/// Request URIs contain the API key and therefore must never be logged.
/// </summary>
public sealed class RetroAchievementsApiClient
{
    private const string UserProfileEndpoint =
        "https://retroachievements.org/API/API_GetUserProfile.php";
    private const string GameListEndpoint =
        "https://retroachievements.org/API/API_GetGameList.php";
    private const string GameInfoAndUserProgressEndpoint =
        "https://retroachievements.org/API/API_GetGameInfoAndUserProgress.php";

    private readonly HttpClient _httpClient;

    public RetroAchievementsApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<RetroAchievementsUserProfile> GetUserProfileAsync(
        string username,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var requestUri = new Uri(
            $"{UserProfileEndpoint}?u={Uri.EscapeDataString(username.Trim())}&y={Uri.EscapeDataString(apiKey.Trim())}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements service could not be reached.");
        }

        using (response)
        {
            var responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var apiError = RedactSecret(TryReadApiError(responseBody), apiKey);
                var detail = string.IsNullOrWhiteSpace(apiError)
                    ? $"HTTP {(int)response.StatusCode}"
                    : apiError;
                throw new RetroAchievementsApiException(
                    response.StatusCode,
                    $"RetroAchievements rejected the profile request ({detail}).");
            }

            try
            {
                using var json = JsonDocument.Parse(responseBody);
                var root = json.RootElement;

                var apiError = RedactSecret(TryReadApiError(root), apiKey);
                if (!string.IsNullOrWhiteSpace(apiError))
                    throw new RetroAchievementsApiException(apiError);

                var returnedUsername = ReadRequiredString(root, "User", "user");
                var userUlid = ReadRequiredString(root, "ULID", "ulid");

                return new RetroAchievementsUserProfile(
                    returnedUsername,
                    userUlid,
                    ReadInt(root, "TotalPoints", "totalPoints"),
                    ReadInt(root, "TotalSoftcorePoints", "totalSoftcorePoints"),
                    ReadInt(root, "TotalTruePoints", "totalTruePoints"),
                    ReadOptionalString(root, "UserPic", "userPic"));
            }
            catch (RetroAchievementsApiException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new RetroAchievementsApiException(
                    "RetroAchievements returned an invalid profile response.", ex);
            }
        }
    }

    public async Task<IReadOnlyList<RetroAchievementsGameCatalogEntry>> GetGameCatalogAsync(
        uint consoleId,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (consoleId == 0)
            throw new ArgumentOutOfRangeException(nameof(consoleId));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var requestUri = new Uri(
            $"{GameListEndpoint}?i={consoleId.ToString(CultureInfo.InvariantCulture)}&f=1&h=1&y={Uri.EscapeDataString(apiKey.Trim())}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements service could not be reached.");
        }

        using (response)
        {
            var responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var apiError = RedactSecret(TryReadApiError(responseBody), apiKey);
                var detail = string.IsNullOrWhiteSpace(apiError)
                    ? $"HTTP {(int)response.StatusCode}"
                    : apiError;
                throw new RetroAchievementsApiException(
                    response.StatusCode,
                    $"RetroAchievements rejected the game catalog request ({detail}).");
            }

            try
            {
                using var json = JsonDocument.Parse(responseBody);
                var root = json.RootElement;

                var apiError = RedactSecret(TryReadApiError(root), apiKey);
                if (!string.IsNullOrWhiteSpace(apiError))
                    throw new RetroAchievementsApiException(apiError);

                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new RetroAchievementsApiException(
                        "RetroAchievements returned an invalid game catalog response.");
                }

                var games = new List<RetroAchievementsGameCatalogEntry>();
                foreach (var item in root.EnumerateArray())
                {
                    var gameId = ReadInt(item, "ID", "id");
                    var title = ReadOptionalString(item, "Title", "title");
                    var returnedConsoleId = ReadInt(item, "ConsoleID", "consoleId");
                    if (gameId <= 0 ||
                        string.IsNullOrWhiteSpace(title) ||
                        returnedConsoleId <= 0 ||
                        (uint)returnedConsoleId != consoleId)
                    {
                        continue;
                    }

                    var hashes = ReadHashes(item);
                    if (hashes.Count == 0)
                        continue;

                    games.Add(new RetroAchievementsGameCatalogEntry
                    {
                        GameId = gameId,
                        Title = title.Trim(),
                        ConsoleId = (uint)returnedConsoleId,
                        ConsoleName = ReadOptionalString(item, "ConsoleName", "consoleName")?.Trim() ?? string.Empty,
                        ImageIconPath = ReadOptionalString(item, "ImageIcon", "imageIcon"),
                        AchievementCount = ReadInt(item, "NumAchievements", "numAchievements"),
                        Hashes = hashes
                    });
                }

                return games;
            }
            catch (RetroAchievementsApiException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new RetroAchievementsApiException(
                    "RetroAchievements returned an invalid game catalog response.", ex);
            }
        }
    }

    public async Task<RetroAchievementsGameProgress> GetGameInfoAndUserProgressAsync(
        int gameId,
        string userIdentifier,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
            throw new ArgumentOutOfRangeException(nameof(gameId));
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var requestUri = new Uri(
            $"{GameInfoAndUserProgressEndpoint}?g={gameId.ToString(CultureInfo.InvariantCulture)}" +
            $"&u={Uri.EscapeDataString(userIdentifier.Trim())}&a=1&y={Uri.EscapeDataString(apiKey.Trim())}");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements service could not be reached.");
        }

        using (response)
        {
            var responseBody = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var apiError = RedactSecret(TryReadApiError(responseBody), apiKey);
                var detail = string.IsNullOrWhiteSpace(apiError)
                    ? $"HTTP {(int)response.StatusCode}"
                    : apiError;
                throw new RetroAchievementsApiException(
                    response.StatusCode,
                    $"RetroAchievements rejected the game progress request ({detail}).");
            }

            try
            {
                using var json = JsonDocument.Parse(responseBody);
                var root = json.RootElement;

                var apiError = RedactSecret(TryReadApiError(root), apiKey);
                if (!string.IsNullOrWhiteSpace(apiError))
                    throw new RetroAchievementsApiException(apiError);

                var returnedGameId = ReadInt(root, "ID", "id");
                var title = ReadOptionalString(root, "Title", "title");
                var consoleId = ReadInt(root, "ConsoleID", "consoleId");
                if (returnedGameId != gameId ||
                    string.IsNullOrWhiteSpace(title) ||
                    consoleId <= 0)
                {
                    throw new RetroAchievementsApiException(
                        "RetroAchievements returned an invalid game progress response.");
                }

                return new RetroAchievementsGameProgress
                {
                    GameId = returnedGameId,
                    Title = title.Trim(),
                    ConsoleId = (uint)consoleId,
                    ConsoleName = ReadOptionalString(root, "ConsoleName", "consoleName")?.Trim() ?? string.Empty,
                    ImageIconPath = ReadOptionalString(root, "ImageIcon", "imageIcon"),
                    ImageTitlePath = ReadOptionalString(root, "ImageTitle", "imageTitle"),
                    ImageInGamePath = ReadOptionalString(root, "ImageIngame", "imageIngame"),
                    ImageBoxArtPath = ReadOptionalString(root, "ImageBoxArt", "imageBoxArt"),
                    AchievementCount = ReadInt(root, "NumAchievements", "numAchievements"),
                    AwardedCount = ReadInt(root, "NumAwardedToUser", "numAwardedToUser"),
                    AwardedHardcoreCount = ReadInt(
                        root,
                        "NumAwardedToUserHardcore",
                        "numAwardedToUserHardcore"),
                    CompletionPercent = ReadPercent(root, "UserCompletion", "userCompletion"),
                    CompletionHardcorePercent = ReadPercent(
                        root,
                        "UserCompletionHardcore",
                        "userCompletionHardcore"),
                    UserTotalPlaytime = ReadInt(root, "UserTotalPlaytime", "userTotalPlaytime"),
                    HighestAwardKind = ReadOptionalString(root, "HighestAwardKind", "highestAwardKind"),
                    HighestAwardAtUtc = ReadDateTimeOffset(
                        root,
                        "HighestAwardDate",
                        "highestAwardDate"),
                    Achievements = ReadAchievements(root)
                };
            }
            catch (RetroAchievementsApiException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw new RetroAchievementsApiException(
                    "RetroAchievements returned an invalid game progress response.", ex);
            }
        }
    }

    private static string? TryReadApiError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return null;

        try
        {
            using var json = JsonDocument.Parse(responseBody);
            return TryReadApiError(json.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadApiError(JsonElement root)
    {
        var error = ReadOptionalString(root, "Error", "error", "Message", "message");
        if (!string.IsNullOrWhiteSpace(error))
            return LimitErrorDetail(error);

        if (TryGetProperty(root, out var errors, "Errors", "errors") &&
            errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errors.EnumerateArray())
            {
                var nestedError = ReadOptionalString(
                    item,
                    "Title",
                    "title",
                    "Detail",
                    "detail",
                    "Message",
                    "message",
                    "Code",
                    "code");
                if (!string.IsNullOrWhiteSpace(nestedError))
                    return LimitErrorDetail(nestedError);
            }
        }

        if (TryGetProperty(root, out var success, "Success", "success") &&
            success.ValueKind is JsonValueKind.False)
        {
            return "The request was not successful.";
        }

        return null;
    }

    private static string ReadRequiredString(JsonElement root, params string[] propertyNames)
    {
        var value = ReadOptionalString(root, propertyNames);
        if (string.IsNullOrWhiteSpace(value))
            throw new RetroAchievementsApiException(
                "The RetroAchievements profile response is missing required account data.");

        return value;
    }

    private static string? ReadOptionalString(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var value, propertyNames))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static int ReadInt(JsonElement root, params string[] propertyNames)
    {
        if (!TryGetProperty(root, out var value, propertyNames))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static List<string> ReadHashes(JsonElement root)
    {
        var hashes = new List<string>();
        if (!TryGetProperty(root, out var values, "Hashes", "hashes") ||
            values.ValueKind != JsonValueKind.Array)
        {
            return hashes;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
                continue;

            var hash = value.GetString()?.Trim();
            if (IsValidHash(hash))
                hashes.Add(hash!.ToLowerInvariant());
        }

        return hashes;
    }

    private static IReadOnlyList<RetroAchievementsAchievement> ReadAchievements(JsonElement root)
    {
        var achievements = new List<RetroAchievementsAchievement>();
        if (!TryGetProperty(root, out var values, "Achievements", "achievements") ||
            values.ValueKind != JsonValueKind.Object)
        {
            return achievements;
        }

        foreach (var property in values.EnumerateObject())
        {
            var item = property.Value;
            var achievementId = ReadInt(item, "ID", "id");
            var title = ReadOptionalString(item, "Title", "title");
            if (achievementId <= 0 || string.IsNullOrWhiteSpace(title))
                continue;

            achievements.Add(new RetroAchievementsAchievement
            {
                AchievementId = achievementId,
                Title = title.Trim(),
                Description = ReadOptionalString(item, "Description", "description")?.Trim() ?? string.Empty,
                Points = ReadInt(item, "Points", "points"),
                TrueRatio = ReadInt(item, "TrueRatio", "trueRatio"),
                Author = ReadOptionalString(item, "Author", "author")?.Trim() ?? string.Empty,
                BadgeName = ReadOptionalString(item, "BadgeName", "badgeName")?.Trim() ?? string.Empty,
                DisplayOrder = ReadInt(item, "DisplayOrder", "displayOrder"),
                Type = ReadOptionalString(item, "type", "Type")?.Trim(),
                EarnedAtUtc = ReadDateTimeOffset(item, "DateEarned", "dateEarned"),
                EarnedHardcoreAtUtc = ReadDateTimeOffset(
                    item,
                    "DateEarnedHardcore",
                    "dateEarnedHardcore")
            });
        }

        achievements.Sort(static (left, right) =>
        {
            var order = left.DisplayOrder.CompareTo(right.DisplayOrder);
            return order != 0 ? order : left.AchievementId.CompareTo(right.AchievementId);
        });
        return achievements;
    }

    private static double ReadPercent(JsonElement root, params string[] propertyNames)
    {
        var value = ReadOptionalString(root, propertyNames)?.Trim().TrimEnd('%');
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var percent)
            ? percent
            : 0;
    }

    private static DateTimeOffset? ReadDateTimeOffset(
        JsonElement root,
        params string[] propertyNames)
    {
        var value = ReadOptionalString(root, propertyNames);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static bool IsValidHash(string? value)
    {
        if (value is not { Length: 32 })
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }

        return true;
    }

    private static bool TryGetProperty(
        JsonElement root,
        out JsonElement value,
        params string[] propertyNames)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                if (root.TryGetProperty(propertyName, out value))
                    return true;
            }
        }

        value = default;
        return false;
    }

    private static string LimitErrorDetail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= 220 ? trimmed : trimmed[..220] + "...";
    }

    private static string? RedactSecret(string? value, string secret)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var trimmedSecret = secret.Trim();
        return string.IsNullOrEmpty(trimmedSecret)
            ? value
            : value.Replace(trimmedSecret, "[redacted]", StringComparison.Ordinal);
    }
}
