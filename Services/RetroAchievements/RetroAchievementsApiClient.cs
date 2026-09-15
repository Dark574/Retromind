using System;
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
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements request timed out.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new RetroAchievementsApiException(
                "The RetroAchievements service could not be reached.", ex);
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
