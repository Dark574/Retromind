using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog.Playtime;

internal interface IGogPlaytimeApiClient
{
    Task PostSessionAsync(
        string gameId,
        string userId,
        string accessToken,
        long sessionDate,
        int timeMinutes,
        CancellationToken cancellationToken = default);
}

internal sealed class GogPlaytimeApiClient : IGogPlaytimeApiClient
{
    private const string GameplayBaseUri = "https://gameplay.gog.com/games/";
    private readonly HttpClient _httpClient;

    internal GogPlaytimeApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task PostSessionAsync(
        string gameId,
        string userId,
        string accessToken,
        long sessionDate,
        int timeMinutes,
        CancellationToken cancellationToken = default)
    {
        gameId = ValidateNumericIdentifier(gameId, nameof(gameId));
        userId = ValidateNumericIdentifier(userId, nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (sessionDate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionDate));
        if (timeMinutes <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeMinutes));

        var requestUri = new Uri(
            $"{GameplayBaseUri}{gameId}/users/{userId}/sessions",
            UriKind.Absolute);
        var payload = JsonSerializer.Serialize(new SessionPayload(sessionDate, timeMinutes));
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            accessToken.Trim());

        try
        {
            using var response = await _httpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new GogPlaytimeApiException(
                    response.StatusCode,
                    $"GOG rejected the playtime session (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (GogPlaytimeApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GogPlaytimeApiException("The GOG playtime request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GogPlaytimeApiException(
                "The GOG gameplay service could not be reached.",
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

    private sealed record SessionPayload(
        [property: JsonPropertyName("session_date")] long SessionDate,
        [property: JsonPropertyName("time")] int TimeMinutes);
}

internal sealed class GogPlaytimeApiException : Exception
{
    internal GogPlaytimeApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    internal GogPlaytimeApiException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    internal HttpStatusCode? StatusCode { get; }
}
