using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog.Achievements;

public sealed class GogAchievementsApiClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int MaximumAchievementCount = 5000;
    private const string GameplayBaseUri = "https://gameplay.gog.com/clients/";

    private readonly HttpClient _httpClient;

    public GogAchievementsApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<GogGameProgress> GetGameProgressAsync(
        string gameId,
        string userId,
        string accessToken,
        string language = "en-US",
        CancellationToken cancellationToken = default)
    {
        gameId = ValidateNumericIdentifier(gameId, nameof(gameId));
        userId = ValidateNumericIdentifier(userId, nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var requestUri = new Uri(
            $"{GameplayBaseUri}{gameId}/users/{userId}/achievements",
            UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken.Trim());
        request.Headers.TryAddWithoutValidation(
            "X-Gog-Lc",
            NormalizeLanguage(language));

        try
        {
            using var response = await _httpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new GogAchievementsApiException(
                    response.StatusCode,
                    $"GOG rejected the achievement request " +
                    $"(HTTP {(int)response.StatusCode}).");
            }

            var responseBody = await ReadBoundedResponseAsync(
                    response,
                    cancellationToken)
                .ConfigureAwait(false);
            return ParseProgress(responseBody, gameId);
        }
        catch (GogAchievementsApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new GogAchievementsApiException(
                "The GOG gameplay request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GogAchievementsApiException(
                "The GOG gameplay service could not be reached.",
                ex);
        }
        catch (IOException ex)
        {
            throw new GogAchievementsApiException(
                "The GOG gameplay response could not be read.",
                ex);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new GogAchievementsApiException(
                "The GOG gameplay response exceeds the allowed size.");
        }

        await using var responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await responseStream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            if (memory.Length + read > MaximumResponseBytes)
            {
                throw new GogAchievementsApiException(
                    "The GOG gameplay response exceeds the allowed size.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static GogGameProgress ParseProgress(
        ReadOnlyMemory<byte> responseBody,
        string gameId)
    {
        try
        {
            using var json = JsonDocument.Parse(responseBody);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                throw new GogAchievementsApiException(
                    "GOG returned an invalid achievement response.");
            }

            if (items.GetArrayLength() > MaximumAchievementCount)
            {
                throw new GogAchievementsApiException(
                    "GOG returned too many achievements.");
            }

            var achievements = new List<GogAchievement>(items.GetArrayLength());
            var knownIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var achievementId = ReadIdentifier(item, "achievement_id");
                if (string.IsNullOrWhiteSpace(achievementId) ||
                    !knownIds.Add(achievementId))
                {
                    continue;
                }

                var unlockedAtUtc = ReadTimestamp(item, "date_unlocked");
                var isUnlocked = unlockedAtUtc.HasValue;
                var isVisibleWhileLocked = ReadBoolean(item, "visible");
                var isHidden = !isVisibleWhileLocked;
                var hideDetails = isHidden && !isUnlocked;
                achievements.Add(new GogAchievement
                {
                    AchievementId = achievementId,
                    Key = ReadOptionalString(item, "achievement_key") ?? achievementId,
                    Title = hideDetails
                        ? string.Empty
                        : ReadOptionalString(item, "name") ?? string.Empty,
                    Description = hideDetails
                        ? string.Empty
                        : ReadOptionalString(item, "description") ?? string.Empty,
                    IsHidden = isHidden,
                    IsUnlocked = isUnlocked,
                    UnlockedAtUtc = unlockedAtUtc,
                    ImageUrl = hideDetails
                        ? null
                        : ReadOptionalString(
                            item,
                            isUnlocked ? "image_url_unlocked" : "image_url_locked"),
                    Rarity = Math.Clamp(ReadDouble(item, "rarity"), 0, 100),
                    RarityLevelDescription = ReadOptionalString(
                        item,
                        "rarity_level_description") ?? string.Empty
                });
            }

            return new GogGameProgress
            {
                GameId = gameId,
                Achievements = achievements
            };
        }
        catch (GogAchievementsApiException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new GogAchievementsApiException(
                "GOG returned an invalid achievement response.",
                ex);
        }
    }

    private static string ValidateNumericIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        value = value.Trim();
        if (value.Length > 32 ||
            !value.All(character => character is >= '0' and <= '9') ||
            value.All(character => character == '0'))
        {
            throw new ArgumentException(
                "GOG identifiers must be positive numeric values of at most 32 digits.",
                parameterName);
        }

        return value;
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en-US";

        try
        {
            return CultureInfo.GetCultureInfo(language.Trim()).Name;
        }
        catch (CultureNotFoundException)
        {
            return "en-US";
        }
    }

    private static string? ReadIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString()?.Trim(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => property.TryGetInt32(out var number) && number != 0,
            _ => false
        };
    }

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out var value) &&
               double.IsFinite(value)
            ? value
            : 0;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        var raw = ReadOptionalString(element, propertyName);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
