using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Models.Stores;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogOAuthClient
{
    private static readonly Uri AuthorizeEndpoint = new("https://auth.gog.com/auth");
    private static readonly Uri TokenEndpoint = new("https://auth.gog.com/token");
    private static readonly Uri UserDataEndpoint = new("https://embed.gog.com/userData.json");

    private readonly HttpClient _httpClient;

    public GogOAuthClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Uri BuildAuthorizeUri(
        string clientId,
        Uri redirectUri,
        string state,
        string? codeChallenge = null,
        string? codeChallengeMethod = null,
        string? layout = "client2")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["response_type"] = "code",
            ["state"] = state
        };

        if (!string.IsNullOrWhiteSpace(codeChallenge))
            query["code_challenge"] = codeChallenge;

        if (!string.IsNullOrWhiteSpace(codeChallengeMethod))
            query["code_challenge_method"] = codeChallengeMethod;

        if (!string.IsNullOrWhiteSpace(layout))
            query["layout"] = layout;

        var queryString = BuildQueryString(query);
        return new Uri($"{AuthorizeEndpoint}?{queryString}");
    }

    public async Task<GogTokenSet> ExchangeCodeAsync(
        string clientId,
        string? clientSecret,
        Uri redirectUri,
        string authorizationCode,
        string? codeVerifier,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("grant_type", "authorization_code"),
            new("code", authorizationCode),
            new("redirect_uri", redirectUri.ToString())
        };

        if (!string.IsNullOrWhiteSpace(clientSecret))
            form.Add(new KeyValuePair<string, string>("client_secret", clientSecret));

        if (!string.IsNullOrWhiteSpace(codeVerifier))
            form.Add(new KeyValuePair<string, string>("code_verifier", codeVerifier));

        return await RequestTokenAsync(form, ct).ConfigureAwait(false);
    }

    public async Task<GogTokenSet> RefreshAsync(
        string clientId,
        string? clientSecret,
        string refreshToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken)
        };

        if (!string.IsNullOrWhiteSpace(clientSecret))
            form.Add(new KeyValuePair<string, string>("client_secret", clientSecret));

        return await RequestTokenAsync(form, ct).ConfigureAwait(false);
    }

    public async Task<StoreAccountInfo> GetAccountInfoAsync(string accessToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, UserDataEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GOG account request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {ExtractErrorDetail(responseBody)}");

        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;

        var accountId = ReadString(root, "userId") ?? ReadString(root, "id");
        var displayName = ReadString(root, "username") ?? ReadString(root, "userName");
        var email = ReadString(root, "email");

        if (string.IsNullOrWhiteSpace(accountId))
            accountId = !string.IsNullOrWhiteSpace(displayName) ? displayName : "gog-user";

        return new StoreAccountInfo(accountId, displayName, email);
    }

    private async Task<GogTokenSet> RequestTokenAsync(
        IReadOnlyCollection<KeyValuePair<string, string>> form,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"GOG token request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {ExtractErrorDetail(responseBody)}");

        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;

        var accessToken = ReadString(root, "access_token");
        var refreshToken = ReadString(root, "refresh_token");
        var expiresIn = ReadInt(root, "expires_in");

        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(refreshToken) ||
            expiresIn == null)
        {
            throw new InvalidOperationException("GOG token response was missing required fields.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, expiresIn.Value));
        return new GogTokenSet(accessToken, refreshToken, expiresAt);
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

    private static string ExtractErrorDetail(string? jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
            return "No response body.";

        try
        {
            using var json = JsonDocument.Parse(jsonOrText);
            var root = json.RootElement;
            var error = ReadString(root, "error");
            var description = ReadString(root, "error_description");
            if (!string.IsNullOrWhiteSpace(error) || !string.IsNullOrWhiteSpace(description))
                return $"{error ?? "error"}: {description ?? "no description"}";
        }
        catch
        {
            // Ignore parse failures and fall through to raw text.
        }

        var trimmed = jsonOrText.Trim();
        return trimmed.Length <= 220 ? trimmed : trimmed[..220] + "...";
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> values)
    {
        var parts = new List<string>();
        foreach (var pair in values)
            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");

        return string.Join("&", parts);
    }
}
