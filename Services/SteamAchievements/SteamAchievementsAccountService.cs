using System;
using System.Threading;
using System.Threading.Tasks;
using Retromind.Services.Stores.Security;

namespace Retromind.Services.SteamAchievements;

/// <summary>
/// Coordinates Steam account verification and secure Web API key storage.
/// </summary>
public sealed class SteamAchievementsAccountService
{
    private static readonly SecretKey WebApiKey =
        new("retromind:steam", "web-api-key");

    private readonly SteamApiClient _apiClient;
    private readonly ISecretStore _secretStore;

    public SteamAchievementsAccountService(
        SteamApiClient apiClient,
        ISecretStore secretStore)
    {
        _apiClient = apiClient ??
            throw new ArgumentNullException(nameof(apiClient));
        _secretStore = secretStore ??
            throw new ArgumentNullException(nameof(secretStore));
    }

    public Task<string?> GetApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        return _secretStore.GetAsync(WebApiKey, cancellationToken);
    }

    public SecretStorageStatus ApiKeyStorageStatus => _secretStore.GetStorageStatus(WebApiKey);

    public async Task StoreApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var status = await _secretStore.SetWithStatusAsync(
            WebApiKey,
            apiKey.Trim(),
            cancellationToken).ConfigureAwait(false);
        if (status == SecretStorageStatus.Failed)
            throw new InvalidOperationException("The Steam Web API key could not be stored.");
    }

    public Task DeleteApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        return _secretStore.DeleteAsync(WebApiKey, cancellationToken);
    }

    public async Task<SteamUserProfile> VerifyAsync(
        string steamId64,
        string? pendingApiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamId64);

        var apiKey = string.IsNullOrWhiteSpace(pendingApiKey)
            ? await GetApiKeyAsync(cancellationToken).ConfigureAwait(false)
            : pendingApiKey.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No Steam Web API key is configured.");
        }

        return await _apiClient
            .GetPlayerSummaryAsync(
                steamId64.Trim(),
                apiKey,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
