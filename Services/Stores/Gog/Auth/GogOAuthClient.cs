using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Retromind.Services.Stores.Gog.Auth;

public sealed class GogOAuthClient
{
    private static readonly Uri AuthorizeEndpoint = new("https://auth.gog.com/auth");
    private static readonly Uri TokenEndpoint = new("https://auth.gog.com/token");

    public Uri BuildAuthorizeUri(
        string clientId,
        Uri redirectUri,
        string state,
        string? codeChallenge = null,
        string? codeChallengeMethod = null)
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

        var queryString = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return new Uri($"{AuthorizeEndpoint}?{queryString}");
    }

    public Task<GogTokenSet> ExchangeCodeAsync(
        string clientId,
        Uri redirectUri,
        string authorizationCode,
        string? codeVerifier,
        CancellationToken ct = default)
    {
        throw new NotImplementedException($"Token exchange is not implemented yet. Endpoint: {TokenEndpoint}");
    }

    public Task<GogTokenSet> RefreshAsync(
        string clientId,
        string refreshToken,
        CancellationToken ct = default)
    {
        throw new NotImplementedException($"Token refresh is not implemented yet. Endpoint: {TokenEndpoint}");
    }
}
